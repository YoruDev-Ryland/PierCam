using System;
using System.Collections.Generic;
using System.Threading;

namespace PierCam.Camera;

internal sealed record CameraDescriptor(int Id, string Name, int MaxWidth, int MaxHeight,
    bool IsColor, AsiBayer Bayer, int BitDepth, double PixelSizeUm);

internal sealed class CameraException : Exception
{
    public AsiError Code { get; }
    public CameraException(string op, AsiError code)
        : base($"{op} failed: {code}") => Code = code;
}

/// <summary>
/// Managed wrapper around one open ASI camera.
///
/// Deliberately allocation-free on the hot path: every capture writes into a caller-owned
/// buffer that is reused for the lifetime of the session. Nothing here retains frame data.
/// </summary>
internal sealed unsafe class AsiCamera : IDisposable
{
    private readonly object _gate = new();
    private bool _open;
    private bool _videoRunning;
    private bool _disposed;

    public CameraDescriptor Descriptor { get; private set; } = null!;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Bin { get; private set; } = 1;
    public AsiImgType ImageType { get; private set; } = AsiImgType.Raw16;
    public string SerialNumber { get; private set; } = string.Empty;

    /// <summary>Bytes one full frame occupies in the current ROI/format.</summary>
    public int FrameBytes => Width * Height * ImageType switch
    {
        AsiImgType.Raw16 => 2,
        AsiImgType.Rgb24 => 3,
        _ => 1
    };

    public static IReadOnlyList<CameraDescriptor> Enumerate()
    {
        AsiSdk.EnsureLoaded();
        var list = new List<CameraDescriptor>();
        int n;
        try { n = AsiSdk.GetNumOfConnectedCameras(); }
        catch (DllNotFoundException) { return list; }
        catch (BadImageFormatException) { return list; }

        for (var i = 0; i < n; i++)
        {
            if (AsiSdk.GetCameraProperty(out var info, i) != AsiError.Success) continue;
            list.Add(new CameraDescriptor(info.CameraId, info.Name, info.MaxWidth, info.MaxHeight,
                info.IsColorCam == AsiBool.True, info.BayerPattern, info.BitDepth, info.PixelSize));
        }
        return list;
    }

    public void Open(CameraDescriptor d)
    {
        lock (_gate)
        {
            if (_open) throw new InvalidOperationException("Camera already open.");
            Check("ASIOpenCamera", AsiSdk.OpenCamera(d.Id));
            try
            {
                Check("ASIInitCamera", AsiSdk.InitCamera(d.Id));
            }
            catch
            {
                AsiSdk.CloseCamera(d.Id);
                throw;
            }
            Descriptor = d;
            _open = true;

            if (AsiSdk.GetSerialNumber(d.Id, out var sn) == AsiError.Success)
                SerialNumber = AsiSdk.SerialToString(sn);

            // Full sensor, 16-bit, no binning is the sane default for a static sky camera.
            SetRoi(d.MaxWidth, d.MaxHeight, 1, AsiImgType.Raw16);

            // Give the USB link headroom rather than chasing max framerate; this is the
            // setting that most often causes dropped frames on long USB runs to a pier.
            TrySetControl(AsiControlType.BandwidthOverload, 80);
            TrySetControl(AsiControlType.HighSpeedMode, 0);
            TrySetControl(AsiControlType.Flip, 0);
        }
    }

    public void SetRoi(int width, int height, int bin, AsiImgType type)
    {
        lock (_gate)
        {
            RequireOpen();
            // The SDK requires width%8==0 and height%2==0.
            width -= width % 8;
            height -= height % 2;
            Check("ASISetROIFormat", AsiSdk.SetRoiFormat(Descriptor.Id, width, height, bin, type));
            Width = width; Height = height; Bin = bin; ImageType = type;
        }
    }

    /// <param name="exposure">Exposure time; the SDK works in microseconds.</param>
    public void SetExposure(TimeSpan exposure)
    {
        var us = (int)Math.Clamp(exposure.TotalMilliseconds * 1000.0, 32, int.MaxValue);
        SetControl(AsiControlType.Exposure, us);
    }

    public void SetGain(int gain) => SetControl(AsiControlType.Gain, gain);
    public void SetOffset(int offset) => TrySetControl(AsiControlType.Offset, offset);

    public void SetWhiteBalance(int red, int blue)
    {
        TrySetControl(AsiControlType.WbR, red);
        TrySetControl(AsiControlType.WbB, blue);
    }

    public void SetControl(AsiControlType type, int value)
    {
        lock (_gate)
        {
            RequireOpen();
            Check($"ASISetControlValue({type})", AsiSdk.SetControlValue(Descriptor.Id, type, value, AsiBool.False));
        }
    }

    public bool TrySetControl(AsiControlType type, int value)
    {
        lock (_gate)
        {
            if (!_open) return false;
            return AsiSdk.SetControlValue(Descriptor.Id, type, value, AsiBool.False) == AsiError.Success;
        }
    }

    public int? TryGetControl(AsiControlType type)
    {
        lock (_gate)
        {
            if (!_open) return null;
            return AsiSdk.GetControlValue(Descriptor.Id, type, out var v, out _) == AsiError.Success ? v : null;
        }
    }

    /// <summary>Sensor temperature in °C, or null if the camera does not report it.</summary>
    public double? TemperatureC
    {
        get
        {
            var raw = TryGetControl(AsiControlType.Temperature);
            return raw is null ? null : raw.Value / 10.0;
        }
    }

    public (int min, int max, int def)? GainRange => ControlRange(AsiControlType.Gain);
    public (int min, int max, int def)? ExposureRangeUs => ControlRange(AsiControlType.Exposure);

    private (int min, int max, int def)? ControlRange(AsiControlType type)
    {
        lock (_gate)
        {
            if (!_open) return null;
            if (AsiSdk.GetNumOfControls(Descriptor.Id, out var n) != AsiError.Success) return null;
            for (var i = 0; i < n; i++)
            {
                if (AsiSdk.GetControlCaps(Descriptor.Id, i, out var caps) != AsiError.Success) continue;
                if (caps.ControlType == type) return (caps.MinValue, caps.MaxValue, caps.DefaultValue);
            }
            return null;
        }
    }

    // ---- Video (streaming) mode: best for short preview exposures -------------------

    public void StartVideo()
    {
        lock (_gate)
        {
            RequireOpen();
            if (_videoRunning) return;
            Check("ASIStartVideoCapture", AsiSdk.StartVideoCapture(Descriptor.Id));
            _videoRunning = true;
        }
    }

    public void StopVideo()
    {
        lock (_gate)
        {
            if (!_open || !_videoRunning) return;
            AsiSdk.StopVideoCapture(Descriptor.Id);
            _videoRunning = false;
        }
    }

    /// <summary>Non-throwing frame grab. Returns false on timeout so the caller can keep looping.</summary>
    public bool TryGetVideoFrame(byte[] buffer, int waitMs)
    {
        if (buffer.Length < FrameBytes) throw new ArgumentException("Buffer too small for frame.", nameof(buffer));
        lock (_gate)
        {
            if (!_open || !_videoRunning) return false;
            fixed (byte* p = buffer)
            {
                var err = AsiSdk.GetVideoData(Descriptor.Id, p, FrameBytes, waitMs);
                if (err == AsiError.Success) return true;
                if (err == AsiError.Timeout) return false;
                throw new CameraException("ASIGetVideoData", err);
            }
        }
    }

    // ---- Snap (single exposure) mode: used for anything longer than ~1s -------------

    /// <summary>
    /// Takes one exposure and fills <paramref name="buffer"/>. Polls rather than blocking in the
    /// SDK so a session stop or app shutdown can interrupt a 60-second sub immediately.
    /// </summary>
    public bool CaptureSnap(byte[] buffer, TimeSpan exposure, CancellationToken ct)
    {
        if (buffer.Length < FrameBytes) throw new ArgumentException("Buffer too small for frame.", nameof(buffer));
        StopVideo();

        lock (_gate)
        {
            RequireOpen();
            Check("ASIStartExposure", AsiSdk.StartExposure(Descriptor.Id, AsiBool.False));
        }

        // Poll ~10x/sec, but never spin longer than the exposure plus generous readout slack.
        var deadline = DateTime.UtcNow + exposure + TimeSpan.FromSeconds(15);
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    lock (_gate) { if (_open) AsiSdk.StopExposure(Descriptor.Id); }
                    return false;
                }

                AsiExposureStatus status;
                lock (_gate)
                {
                    RequireOpen();
                    Check("ASIGetExpStatus", AsiSdk.GetExpStatus(Descriptor.Id, out status));
                }

                switch (status)
                {
                    case AsiExposureStatus.Success:
                        lock (_gate)
                        {
                            RequireOpen();
                            fixed (byte* p = buffer)
                                Check("ASIGetDataAfterExp", AsiSdk.GetDataAfterExp(Descriptor.Id, p, FrameBytes));
                        }
                        return true;

                    case AsiExposureStatus.Failed:
                        return false;

                    case AsiExposureStatus.Idle:
                        // Camera never started; treat as a failed sub rather than hanging.
                        return false;
                }

                if (DateTime.UtcNow > deadline)
                {
                    lock (_gate) { if (_open) AsiSdk.StopExposure(Descriptor.Id); }
                    return false;
                }

                ct.WaitHandle.WaitOne(100);
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public int DroppedFrames
    {
        get
        {
            lock (_gate)
            {
                if (!_open) return 0;
                return AsiSdk.GetDroppedFrames(Descriptor.Id, out var d) == AsiError.Success ? d : 0;
            }
        }
    }

    private void RequireOpen()
    {
        if (!_open) throw new InvalidOperationException("Camera is not open.");
    }

    private static void Check(string op, AsiError err)
    {
        if (err != AsiError.Success) throw new CameraException(op, err);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_open)
            {
                try { if (_videoRunning) AsiSdk.StopVideoCapture(Descriptor.Id); } catch { /* shutting down */ }
                try { AsiSdk.CloseCamera(Descriptor.Id); } catch { /* shutting down */ }
                _open = false;
                _videoRunning = false;
            }
        }
    }
}
