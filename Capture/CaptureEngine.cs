using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using PierCam.Camera;
using PierCam.Imaging;
using PierCam.Models;
using PierCam.Video;

namespace PierCam.Capture;

internal enum EngineState { Disconnected, Connecting, Previewing, Recording, Error }

internal sealed class EngineStatus
{
    public EngineState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public long FramesCaptured { get; init; }
    public long FramesRecorded { get; init; }
    public double? SensorTempC { get; init; }
    public double LastFrameSeconds { get; init; }
    public DateTime? RecordingStarted { get; init; }
    public DateTime? NextCaptureDue { get; init; }
    public string? RecordingTitle { get; init; }
    public long VideoBytesSoFar { get; init; }
    public long EstimatedRawBytes { get; init; }

    /// <summary>Non-null only while auto-exposure is actively driving the camera.</summary>
    public double? AutoExposureSeconds { get; init; }
    public int? AutoExposureGain { get; init; }
    public double? SkyLevel { get; init; }

    /// <summary>Share of pixels at full scale on the last frame. High means blown out.</summary>
    public double ClippedFraction { get; init; }
}

/// <summary>
/// Owns the camera and the single capture thread.
///
/// Everything downstream — live view, recording, statistics — is fed from this one loop, so the
/// camera is never contended and there is exactly one place where frames are produced. Two RGB
/// buffers are allocated when a camera connects and reused until it disconnects; nothing in the
/// steady-state loop allocates, which is the point of the whole design.
/// </summary>
internal sealed class CaptureEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _frameLock = new();

    private AsiCamera? _camera;
    private FrameProcessor? _processor;
    private AutoStretchAnalyzer? _analyzer;
    private readonly SmoothedStretch _smoothed = new();
    /// <summary>Separate ramps: the recording one glides, the preview one is allowed to jump.</summary>
    private readonly AutoExposureController _recordAe = new();
    private readonly AutoExposureController _previewAe = new();

    /// <summary>Live exposure/gain while auto-exposure is driving them.</summary>
    private ExposurePoint _currentExposure;

    private byte[]? _rawBuffer;
    private byte[]? _workRgb;
    private byte[]? _latestRgb;
    private long _frameSequence;

    private Thread? _thread;
    private CancellationTokenSource? _cts;

    private volatile RecordingSession? _recording;
    private readonly object _recordGate = new();

    /// <summary>
    /// When held, the session stays open but stops consuming frames — used when the roof shuts
    /// mid-night. The camera reverts to preview settings so the live view stays useful while
    /// waiting, and the finished video is one file with a gap rather than several fragments.
    /// </summary>
    private volatile bool _hold;
    private volatile string _holdReason = string.Empty;
    private DateTime? _holdSince;

    private long _framesCaptured;
    private double _lastFrameSeconds;
    private double? _sensorTemp;
    private volatile string _message = "Disconnected";
    private volatile EngineState _state = EngineState.Disconnected;

    /// <summary>Raised when a new frame has been published. Fires on the capture thread.</summary>
    public event Action? FrameReady;

    /// <summary>Raised when a recording session ends, with the final manifest.</summary>
    public event Action<TimelapseManifest>? SessionFinished;

    public event Action? StatusChanged;

    public CaptureEngine(AppSettings settings) => _settings = settings;

    public int Width => _processor?.Width ?? 0;
    public int Height => _processor?.Height ?? 0;
    public long FrameSequence => Interlocked.Read(ref _frameSequence);
    public bool IsRecording => _recording is not null;

    /// <summary>True when a session is open but paused (roof shut, typically).</summary>
    public bool IsHeld => _hold && _recording is not null;

    /// <summary>Pauses or resumes frame capture without ending the session.</summary>
    public void SetHold(bool hold, string reason)
    {
        if (_hold == hold) return;
        _hold = hold;
        _holdReason = reason;

        var session = _recording;
        if (hold)
        {
            _holdSince = DateTime.Now;
            SetState(EngineState.Recording, $"Held — {reason}");
        }
        else
        {
            if (_holdSince is { } since && session is not null)
                session.HeldTime += DateTime.Now - since;
            _holdSince = null;
            SetState(EngineState.Recording, session is null ? "Live view running" : "Recording");
        }
        StatusChanged?.Invoke();
    }
    public CameraDescriptor? ConnectedCamera => _camera?.Descriptor;
    public string? CameraSerial => _camera is { SerialNumber.Length: > 0 } c ? c.SerialNumber : null;

    /// <summary>
    /// The target marker, when the app has one. Read once per frame. Every call into it is fenced
    /// off inside the loop: the marker is decoration, and whatever goes wrong in it, the frame
    /// goes on without it.
    /// </summary>
    public PierCam.Sky.TargetMarkerService? Marker { get; set; }

    /// <summary>
    /// Posts the night to a Discord channel, when one is configured. Offered the same frame the
    /// live view gets — timestamp, marker ring and all — and only while a session is recording.
    /// </summary>
    public PierCam.Net.DiscordPoster? Discord { get; set; }
    private int _markerFaults;

    private void MarkerFault(Exception ex)
    {
        if (Interlocked.Increment(ref _markerFaults) <= 3) App.Log(ex, "Target marker");
    }

    public EngineStatus Status
    {
        get
        {
            var rec = _recording;
            var auto = rec is not null
                ? _settings.Camera.AutoExposure
                : _settings.Camera.PreviewAutoExposure && _state == EngineState.Previewing;
            return new EngineStatus
            {
                State = _state,
                Message = _message,
                FramesCaptured = Interlocked.Read(ref _framesCaptured),
                FramesRecorded = rec?.Encoder.FramesWritten ?? 0,
                SensorTempC = _sensorTemp,
                LastFrameSeconds = _lastFrameSeconds,
                RecordingStarted = rec?.Manifest.StartedLocal,
                NextCaptureDue = _nextDue,
                RecordingTitle = rec?.Manifest.Title,
                VideoBytesSoFar = rec?.CurrentSizeBytes ?? 0,
                EstimatedRawBytes = rec?.EstimatedRawBytes ?? 0,
                AutoExposureSeconds = auto ? _currentExposure.Seconds : null,
                AutoExposureGain = auto ? _currentExposure.Gain : null,
                SkyLevel = _analyzer?.LastSkyLevel,
                ClippedFraction = _analyzer?.LastClippedFraction ?? 0
            };
        }
    }

    private DateTime? _nextDue;

    // ---- lifecycle ------------------------------------------------------------------

    public void Connect(CameraDescriptor descriptor)
    {
        Disconnect();

        SetState(EngineState.Connecting, $"Opening {descriptor.Name}…");
        var cam = new AsiCamera();
        cam.Open(descriptor);

        var c = _settings.Camera;
        if (c.RoiWidth > 0 && c.RoiHeight > 0 && (c.RoiWidth != cam.Width || c.RoiHeight != cam.Height))
            cam.SetRoi(c.RoiWidth, c.RoiHeight, Math.Max(1, c.Binning), AsiImgType.Raw16);

        _camera = cam;
        _processor = new FrameProcessor(cam.Width, cam.Height, descriptor.IsColor, descriptor.Bayer, cam.ImageType);
        _analyzer = new AutoStretchAnalyzer(_processor);
        _smoothed.Reset();

        // Allocated once per connection and reused for every frame from here on.
        _rawBuffer = new byte[cam.FrameBytes];
        _workRgb = new byte[_processor.RgbBytes];
        _latestRgb = new byte[_processor.RgbBytes];

        _settings.LastCameraId = descriptor.Id;

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "PierCam capture",
            Priority = ThreadPriority.AboveNormal
        };
        SetState(EngineState.Previewing, "Live view running");
        _thread.Start();
    }

    public void Disconnect()
    {
        StopRecording("Camera disconnected");

        _cts?.Cancel();
        if (_thread is not null && _thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(8)))
            _message = "Capture thread did not stop cleanly";
        _thread = null;

        _cts?.Dispose();
        _cts = null;

        _camera?.Dispose();
        _camera = null;
        _processor = null;
        _analyzer = null;
        _rawBuffer = null;
        _workRgb = null;
        _latestRgb = null;

        SetState(EngineState.Disconnected, "Disconnected");
    }

    /// <summary>
    /// Copies the most recent frame out for display. The lock is held only for the copy, so the
    /// capture thread stalls for a couple of milliseconds at most.
    /// </summary>
    public bool CopyLatestFrame(Action<byte[]> consume)
    {
        lock (_frameLock)
        {
            if (_latestRgb is null || Interlocked.Read(ref _frameSequence) == 0) return false;
            consume(_latestRgb);
            return true;
        }
    }

    // ---- recording ------------------------------------------------------------------

    public void StartRecording(string title)
    {
        lock (_recordGate)
        {
            if (_recording is not null) return;
            if (_processor is null || _camera is null) throw new InvalidOperationException("No camera connected.");

            var ffmpeg = FfmpegEncoder.Locate(_settings.FfmpegPath)
                ?? throw new FfmpegNotFoundException(
                    "ffmpeg.exe was not found. Put it in PierCam's tools folder or set its path in Settings.");

            var v = _settings.Video;
            var started = DateTime.Now;
            var folderName = $"{started:yyyy-MM-dd_HH-mm}";
            var folder = Path.Combine(_settings.TimelapseRoot, folderName);
            var suffix = 1;
            while (Directory.Exists(folder)) folder = Path.Combine(_settings.TimelapseRoot, $"{folderName}_{++suffix}");
            Directory.CreateDirectory(folder);

            // The chosen resolution is a box to fit the sensor inside, not a shape to force it
            // into: a square sensor scaled to 1920×1080 comes out stretched.
            var (outW, outH) = FfmpegEncoder.FitWithin(_processor.Width, _processor.Height, v.OutputWidth, v.OutputHeight);

            var manifest = new TimelapseManifest
            {
                Title = string.IsNullOrWhiteSpace(title) ? started.ToString("dddd d MMMM yyyy") : title,
                StartedLocal = started,
                Fps = v.Fps,
                Width = outW,
                Height = outH,
                ExposureSeconds = _settings.Camera.ExposureSeconds,
                Gain = _settings.Camera.Gain,
                IntervalSeconds = _settings.Session.IntervalSeconds,
                CameraName = _camera.Descriptor.Name,
                CameraSerial = _camera.SerialNumber,
                SensorTempStartC = _sensorTemp,
                Status = SessionStatus.Recording
            };
            manifest.Save(folder);

            var encoder = new FfmpegEncoder(ffmpeg, Path.Combine(folder, manifest.VideoFile),
                _processor.Width, _processor.Height, v.Fps, v.Crf, v.Preset,
                outW, outH, v.Denoise);

            _recording = new RecordingSession(manifest, encoder, folder, ffmpeg, _processor.Width, _processor.Height);

            // A second encoder for the copy with the target marker burned in, fed the same frames.
            // It is an extra: if it cannot start, the night records as normal without it.
            if (Marker?.WantsMarkedCopy == true)
            {
                try
                {
                    _recording.StartMarkedCopy(new FfmpegEncoder(ffmpeg, Path.Combine(folder, RecordingSession.MarkedVideoName),
                        _processor.Width, _processor.Height, v.Fps, v.Crf, v.Preset,
                        outW, outH, v.Denoise));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    MarkerFault(ex);
                }
            }
            _smoothed.Reset();
            SetState(EngineState.Recording, $"Recording · {manifest.Title}");
        }
    }

    public void StopRecording(string? reason = null)
    {
        RecordingSession? session;
        lock (_recordGate)
        {
            session = _recording;
            _recording = null;
        }
        if (session is null) return;

        // Close out any open hold so the recorded total is right.
        if (_holdSince is { } heldFrom) session.HeldTime += DateTime.Now - heldFrom;
        _hold = false;
        _holdSince = null;

        var m = session.Manifest;
        m.EndedLocal = DateTime.Now;
        m.HeldMinutes = (int)Math.Round(session.HeldTime.TotalMinutes);
        m.FrameCount = (int)session.Encoder.FramesWritten;
        m.SensorTempEndC = _sensorTemp;
        m.EstimatedRawBytes = session.EstimatedRawBytes;
        m.Status = m.FrameCount > 0 ? SessionStatus.Complete : SessionStatus.Failed;
        m.Message = reason;

        try
        {
            session.Encoder.Finish(session.FfmpegPath, TimeSpan.FromMinutes(5));
            if (File.Exists(m.VideoPath)) m.VideoBytes = new FileInfo(m.VideoPath).Length;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            m.Status = SessionStatus.Interrupted;
            m.Message = $"{reason} ({ex.Message})".Trim();
        }
        finally
        {
            session.Encoder.Dispose();
        }

        FinishMarkedCopy(session);
        m.Save(session.Folder);
        SessionFinished?.Invoke(m);

        if (_state == EngineState.Recording) SetState(EngineState.Previewing, "Live view running");
    }

    /// <summary>
    /// Closes the marked copy and records it in the manifest. A copy the marker never appeared in
    /// (not calibrated, telescope parked all night, target never in view) would only be a duplicate
    /// of the clean video, so it is discarded rather than kept.
    /// </summary>
    private static void FinishMarkedCopy(RecordingSession session)
    {
        var encoder = session.TakeMarkedCopy();
        if (encoder is null) return;

        var m = session.Manifest;
        var video = Path.Combine(session.Folder, RecordingSession.MarkedVideoName);
        var poster = Path.Combine(session.Folder, RecordingSession.MarkedPosterName);
        try
        {
            if (session.MarkedFramesWithMarker > 0)
            {
                encoder.Finish(session.FfmpegPath, TimeSpan.FromMinutes(5));
                if (File.Exists(video))
                {
                    m.MarkedVideoFile = RecordingSession.MarkedVideoName;
                    m.MarkedVideoBytes = new FileInfo(video).Length;
                    m.MarkedPosterFile = File.Exists(poster) ? RecordingSession.MarkedPosterName : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            App.Log(ex, "Marked copy");
        }
        finally
        {
            encoder.Dispose();
        }

        if (m.MarkedVideoFile is null)
        {
            RecordingSession.TryDelete(encoder.WorkingPath);
            RecordingSession.TryDelete(video);
            RecordingSession.TryDelete(poster);
        }
    }

    /// <summary>
    /// Feeds the marked copy: draws the marker into the frame (which then also serves as the live
    /// view's) and hands it to the second encoder. Any failure drops the copy for the rest of the
    /// night and nothing more - the clean recording carries on regardless.
    /// </summary>
    private void WriteMarkedCopy(RecordingSession session, byte[] rgb, int width, int height,
        PierCam.Sky.MarkerPlacement? place, PierCam.Sky.TargetMarkerService? marker, ref bool drawn)
    {
        var encoder = session.MarkedCopy;
        if (encoder is null || !ReferenceEquals(session, _recording)) return;
        try
        {
            if (place is { } at && marker is not null)
            {
                marker.Draw(rgb, width, height, at);
                drawn = true;
                session.MarkedFramesWithMarker++;
            }
            if (encoder.HasExited) throw new IOException($"ffmpeg exited unexpectedly. {encoder.LastError()}");
            encoder.WriteFrame(rgb);

            var n = encoder.FramesWritten;
            if (n == 10 || (n > 10 && (n & (n - 1)) == 0))
                session.WritePoster(rgb, width, height, RecordingSession.MarkedPosterName);
            if (n % 120 == 0) encoder.Flush();
        }
        catch (Exception ex)
        {
            // A stop racing this write is the normal end of the night, not a failure.
            if (!ReferenceEquals(session, _recording)) return;
            MarkerFault(ex);
            if (session.TakeMarkedCopy() is { } dead)
            {
                dead.Dispose();
                RecordingSession.TryDelete(dead.WorkingPath);
            }
        }
    }

    // ---- capture loop ----------------------------------------------------------------

    private void Run(CancellationToken ct)
    {
        var sw = new Stopwatch();
        var tempPoll = Stopwatch.StartNew();
        var scheduleAnchor = DateTime.UtcNow;
        long scheduledIndex = 0;
        var configuredForRecording = (bool?)null;
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var camera = _camera;
            var processor = _processor;
            var raw = _rawBuffer;
            var work = _workRgb;
            if (camera is null || processor is null || raw is null || work is null) break;

            var session = _recording;
            var recording = session is not null && !_hold;

            try
            {
                // Re-apply camera controls only when switching between preview and session
                // settings; touching them every frame confuses the SDK's exposure pipeline.
                if (configuredForRecording != recording)
                {
                    ApplyCameraSettings(camera, recording);
                    configuredForRecording = recording;
                    scheduleAnchor = DateTime.UtcNow;
                    scheduledIndex = 0;
                    _recordAe.Reset();
                    _previewAe.Reset();
                    _currentExposure = new ExposurePoint(
                        recording ? _settings.Camera.ExposureSeconds : _settings.Camera.PreviewExposureSeconds,
                        recording ? _settings.Camera.Gain : _settings.Camera.PreviewGain);
                }

                var autoExposing = recording
                    ? _settings.Camera.AutoExposure
                    : _settings.Camera.PreviewAutoExposure;

                var exposure = TimeSpan.FromSeconds(autoExposing
                    ? _currentExposure.Seconds
                    : recording
                        ? _settings.Camera.ExposureSeconds
                        : _settings.Camera.PreviewExposureSeconds);

                sw.Restart();
                var ok = Grab(camera, raw, exposure, ct);
                if (ct.IsCancellationRequested) break;

                if (!ok)
                {
                    consecutiveFailures++;
                    _message = $"Frame failed ({consecutiveFailures})";
                    StatusChanged?.Invoke();
                    if (consecutiveFailures >= 5 && !TryRecoverCamera(ct)) break;
                    continue;
                }
                consecutiveFailures = 0;

                var stretch = ChooseStretch(raw, recording);
                processor.Process(raw, work, stretch);

                if (autoExposing) StepAutoExposure(camera, recording);

                // Target marker, part 1: hand over a clean frame if calibration asked for one (before
                // anything is drawn on it), and find where the marker goes. No allocation, no waiting.
                var marker = Marker;
                var frameUtc = DateTime.UtcNow - exposure / 2;
                PierCam.Sky.MarkerPlacement? place = null;
                if (marker is not null)
                {
                    try
                    {
                        if (marker.WantsCalibrationFrame) marker.OfferCalibrationFrame(work, processor.Width, processor.Height, frameUtc);
                        place = marker.PlacementFor(DateTime.UtcNow, processor.Width, processor.Height);
                    }
                    catch (Exception ex) { MarkerFault(ex); place = null; }
                }

                if (_settings.Video.BurnTimestamp)
                {
                    var label = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    TextOverlay.Draw(work, processor.Width, processor.Height, label,
                        TextOverlay.Corner.BottomLeft, Math.Max(1, _settings.Video.TimestampScale));
                }

                // Part 2: burned into the recording only when asked; otherwise drawn after the frame
                // has gone to the encoder, so the recording stays clean and only the live view shows it.
                // With a marked copy running, the recording stays clean and the copy gets the marker.
                var burnIn = false;
                var markedCopy = recording && session?.MarkedCopy is not null;
                if (place is { } burnAt && recording && marker is not null && !markedCopy)
                {
                    try { if (marker.BurnIntoRecordings) { marker.Draw(work, processor.Width, processor.Height, burnAt); burnIn = true; } }
                    catch (Exception ex) { MarkerFault(ex); }
                }

                if (recording && session is not null)
                {
                    var before = session.Encoder.FramesWritten;
                    WriteToSession(session, work, raw, processor, ct);
                    var written = session.Encoder.FramesWritten;
                    if (marker is not null && written > before)
                    {
                        try { marker.FrameRecorded(session.Folder, written - 1, frameUtc, place); }
                        catch (Exception ex) { MarkerFault(ex); }
                    }
                    if (markedCopy && written > before)
                        WriteMarkedCopy(session, work, processor.Width, processor.Height, place, marker, ref burnIn);
                }

                if (place is { } showAt && !burnIn && marker is not null)
                {
                    try { marker.Draw(work, processor.Width, processor.Height, showAt); }
                    catch (Exception ex) { MarkerFault(ex); }
                }

                Publish(work);

                // Discord gets the frame exactly as it was just published, and only from a
                // session that is actually recording. Copying it is a few milliseconds once an
                // hour; everything after that happens on the poster's own thread.
                if (recording && Discord is { } discord)
                {
                    try
                    {
                        var when = DateTime.Now;
                        if (discord.WantsStill(when))
                            discord.PostStill(work, processor.Width, processor.Height, StillCaption(session!, when), when);
                    }
                    catch (Exception ex) { App.Log(ex, "Discord still"); }
                }

                _lastFrameSeconds = sw.Elapsed.TotalSeconds;
                Interlocked.Increment(ref _framesCaptured);
                FrameReady?.Invoke();
                StatusChanged?.Invoke();

                if (tempPoll.Elapsed > TimeSpan.FromSeconds(20))
                {
                    _sensorTemp = camera.TemperatureC;
                    tempPoll.Restart();
                }

                WaitForNextSlot(recording, ref scheduleAnchor, ref scheduledIndex, ct);
            }
            catch (CameraException ex) when (ex.Code is AsiError.CameraRemoved or AsiError.CameraClosed)
            {
                _message = "Camera disconnected — retrying";
                SetState(EngineState.Error, _message);
                if (!TryRecoverCamera(ct)) break;
                configuredForRecording = null;
            }
            catch (ObjectDisposedException)
            {
                // The session was stopped from the UI thread between this loop reading _recording
                // and writing the frame. Nothing is wrong; just pick up the new state next time.
            }
            catch (IOException ex)
            {
                // The encoder died. Keep the camera alive; the night's frames up to now survive.
                //
                // Unless the session was stopped from the UI thread while this iteration was in
                // flight, in which case the encoder closing is a consequence of that stop and
                // there is nothing wrong. Reporting it as a fault would end every scheduled night
                // in an error state over a completed recording, which is worse than useless —
                // it teaches you to distrust a status that was right every other time.
                if (!ReferenceEquals(session, _recording)) continue;

                StopRecording($"Encoder failed: {ex.Message}");
                SetState(EngineState.Error, $"Recording stopped: {ex.Message}");
            }
            catch (Exception ex)
            {
                _message = ex.Message;
                SetState(EngineState.Error, ex.Message);
                if (!ct.WaitHandle.WaitOne(2000)) { /* retry after a pause */ }
            }
        }
    }

    private bool Grab(AsiCamera camera, byte[] raw, TimeSpan exposure, CancellationToken ct)
    {
        // Short exposures stream far more smoothly in video mode; long subs need snap mode so
        // they can be cancelled the instant the user stops the session.
        if (exposure <= TimeSpan.FromSeconds(1))
        {
            camera.StartVideo();
            var waitMs = (int)Math.Max(500, exposure.TotalMilliseconds * 3 + 2000);
            return camera.TryGetVideoFrame(raw, waitMs);
        }

        camera.StopVideo();
        return camera.CaptureSnap(raw, exposure, ct);
    }

    /// <summary>
    /// Feeds the sky level just measured into the appropriate exposure ramp and applies it.
    /// </summary>
    private readonly Stopwatch _aeGate = Stopwatch.StartNew();
    private int _aeSettleFrames;

    private void StepAutoExposure(AsiCamera camera, bool recording)
    {
        var analyzer = _analyzer;
        if (analyzer is null) return;

        // A change to exposure or gain does not affect the frame already in flight, and in
        // video mode the SDK may have two more queued behind it. Measuring before those clear
        // means reacting to the old setting, which makes the loop oscillate — badly, because at
        // a few milliseconds a sub the camera delivers frames faster than it can respond.
        // Waiting a couple of frames and a fixed interval turns it into a stable loop.
        if (_aeSettleFrames > 0) { _aeSettleFrames--; return; }
        if (!recording && _aeGate.ElapsedMilliseconds < 250) return;

        var controller = recording ? _recordAe : _previewAe;
        var limits = recording
            ? ExposureLimits.ForRecording(_settings.Camera)
            : ExposureLimits.ForPreview(_settings.Camera);

        var next = controller.Next(analyzer.LastSkyLevel, analyzer.LastClippedFraction,
            _currentExposure, limits);

        if (Math.Abs(next.Seconds - _currentExposure.Seconds) < 1e-9 && next.Gain == _currentExposure.Gain)
            return;

        _currentExposure = next;
        camera.SetExposure(TimeSpan.FromSeconds(next.Seconds));
        camera.SetGain(next.Gain);
        _aeGate.Restart();
        _aeSettleFrames = recording ? 0 : 2;

        // Remember where the preview settled so the next launch starts close instead of
        // spending its first several frames climbing out of saturation again.
        if (!recording)
        {
            _settings.Camera.PreviewExposureSeconds = next.Seconds;
            _settings.Camera.PreviewGain = next.Gain;
        }
    }

    private void ApplyCameraSettings(AsiCamera camera, bool recording)
    {
        var c = _settings.Camera;
        camera.SetExposure(TimeSpan.FromSeconds(recording ? c.ExposureSeconds : c.PreviewExposureSeconds));
        camera.SetGain(recording ? c.Gain : c.PreviewGain);
        camera.SetOffset(c.Offset);
        camera.SetWhiteBalance(c.WhiteBalanceRed, c.WhiteBalanceBlue);
    }

    private StretchParams ChooseStretch(byte[] raw, bool recording)
    {
        var s = _settings.Stretch;
        var analyzer = _analyzer!;
        analyzer.TargetBackground = s.TargetBackground;
        analyzer.ShadowClip = s.ShadowClip;
        analyzer.ShadowDepth = s.ShadowDepth;
        analyzer.NeutraliseBackground = s.NeutraliseBackground;

        switch (s.Mode)
        {
            case StretchMode.Manual:
                return new StretchParams { Black = s.ManualBlack, Midtone = s.ManualMidtone, White = s.ManualWhite };

            case StretchMode.Auto when !recording:
                return analyzer.Analyze(raw);

            default:
                _smoothed.WindowFrames = Math.Max(1, s.SmoothingFrames);
                return _smoothed.Update(analyzer.Analyze(raw));
        }
    }

    private void WriteToSession(RecordingSession session, byte[] rgb, byte[] raw,
        FrameProcessor processor, CancellationToken ct)
    {
        // A stop from the UI thread nulls _recording and then closes the encoder, while this
        // thread may still be holding the session it read at the top of the iteration. An
        // encoder that has exited because its session was stopped is the expected end of a
        // night, not a failure — only an encoder belonging to the *current* session dying
        // underneath us is.
        if (session.Encoder.HasExited)
        {
            if (!ReferenceEquals(session, _recording)) return;
            throw new IOException($"ffmpeg exited unexpectedly. {session.Encoder.LastError()}");
        }

        session.Encoder.WriteFrame(rgb);
        session.EstimatedRawBytes += processor.Width * (long)processor.Height * 2;

        if (_settings.Session.KeepRawFrames && processor.ImageType == AsiImgType.Raw16)
            session.SaveRawFrame(raw, processor.Width, processor.Height);

        // Poster refreshes on powers of two, so the library gets a thumbnail within the first
        // minute and the final one always lands somewhere in the second half of the night.
        var n = session.Encoder.FramesWritten;
        if (n == 10 || (n > 10 && (n & (n - 1)) == 0))
            session.WritePoster(rgb, processor.Width, processor.Height);

        if (n % 120 == 0)
        {
            session.Encoder.Flush();
            session.Manifest.FrameCount = (int)n;
            session.Manifest.EstimatedRawBytes = session.EstimatedRawBytes;
            session.Manifest.Save(session.Folder);
            CheckDiskSpace(session, ct);
        }
    }

    private void CheckDiskSpace(RecordingSession session, CancellationToken ct)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(session.Folder));
            if (root is null) return;
            var free = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);
            if (free < _settings.Session.MinFreeDiskGb)
                StopRecording($"Stopped: only {free:F1} GB free on {root}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The line under a still: where the night is up to, in the terms the app itself uses.</summary>
    private string StillCaption(RecordingSession session, DateTime now)
    {
        var frames = session.Encoder.FramesWritten;
        var elapsed = now - session.Manifest.StartedLocal;
        var sky = _analyzer?.LastSkyLevel is { } level ? $" · sky {level * 100:0}%" : string.Empty;
        return $"**{session.Manifest.Title}** · {now:HH:mm} · {frames:N0} frames in " +
               $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m · {_currentExposure.Seconds:0.##}s @ gain {_currentExposure.Gain}{sky}";
    }

    private void Publish(byte[] work)
    {
        lock (_frameLock)
        {
            (_latestRgb, _workRgb) = (work, _latestRgb);
            Interlocked.Increment(ref _frameSequence);
        }
    }

    /// <summary>
    /// Holds the cadence on an absolute schedule rather than sleeping a fixed amount after each
    /// frame, so a slow readout does not slowly drag the whole night out of step.
    /// </summary>
    private void WaitForNextSlot(bool recording, ref DateTime anchor, ref long index, CancellationToken ct)
    {
        if (!recording)
        {
            // Hold the live view to a sane rate. With a millisecond sub the camera will deliver
            // 40 fps, and debayering 1080p that often costs real CPU to show a sky that moves
            // over minutes. Sleeping here also keeps the capture thread off the core the
            // encoder wants during a session.
            _nextDue = null;
            var maxFps = Math.Clamp(_settings.Camera.PreviewMaxFps, 0.5, 60.0);
            var minPeriodMs = 1000.0 / maxFps;
            var spentMs = _lastFrameSeconds * 1000.0;
            var restMs = (int)(minPeriodMs - spentMs);
            if (restMs > 1) ct.WaitHandle.WaitOne(restMs);
            return;
        }

        var interval = _settings.Session.IntervalSeconds;
        if (interval <= 0)
        {
            _nextDue = null;
            return;
        }

        index++;
        var due = anchor.AddSeconds(interval * index);
        var now = DateTime.UtcNow;

        if (due < now)
        {
            // Capture took longer than the interval; resync rather than trying to catch up.
            anchor = now;
            index = 0;
            _nextDue = null;
            return;
        }

        _nextDue = due.ToLocalTime();
        StatusChanged?.Invoke();
        var wait = due - now;
        while (wait > TimeSpan.Zero && !ct.IsCancellationRequested)
        {
            var slice = wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            if (ct.WaitHandle.WaitOne(slice)) return;
            // A stop request mid-wait must be honoured immediately, hence the 1-second slices.
            if (_recording is null && recording) return;
            wait = due - DateTime.UtcNow;
        }
        _nextDue = null;
    }

    private bool TryRecoverCamera(CancellationToken ct)
    {
        var descriptor = _camera?.Descriptor;
        if (descriptor is null) return false;

        for (var attempt = 1; attempt <= 60 && !ct.IsCancellationRequested; attempt++)
        {
            _message = $"Reconnecting to {descriptor.Name} (attempt {attempt})…";
            SetState(EngineState.Error, _message);
            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) return false;

            try
            {
                _camera?.Dispose();
                var cam = new AsiCamera();
                cam.Open(descriptor);
                _camera = cam;
                SetState(_recording is not null ? EngineState.Recording : EngineState.Previewing, "Camera reconnected");
                return true;
            }
            catch (Exception ex) when (ex is CameraException or InvalidOperationException)
            {
                // Camera still absent; keep trying. An unattended pier camera should survive a
                // USB dropout without losing the rest of the night.
            }
        }
        return false;
    }

    private void SetState(EngineState state, string message)
    {
        _state = state;
        _message = message;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        Disconnect();
    }
}

/// <summary>State for one in-progress recording.</summary>
internal sealed class RecordingSession
{
    public TimelapseManifest Manifest { get; }
    public FfmpegEncoder Encoder { get; }
    public string Folder { get; }
    public string FfmpegPath { get; }
    public long EstimatedRawBytes { get; set; }

    /// <summary>Total time the session spent paused, e.g. with the roof shut.</summary>
    public TimeSpan HeldTime { get; set; }

    private readonly int _width;
    private readonly int _height;

    public const string MarkedVideoName = "timelapse-marked.mp4";
    public const string MarkedPosterName = "poster-marked.jpg";

    private FfmpegEncoder? _markedCopy;

    /// <summary>The encoder for the copy with the marker burned in, while there is one.</summary>
    public FfmpegEncoder? MarkedCopy => Volatile.Read(ref _markedCopy);

    /// <summary>Frames of the marked copy the marker was actually drawn on.</summary>
    public long MarkedFramesWithMarker { get; set; }

    public void StartMarkedCopy(FfmpegEncoder encoder) => Volatile.Write(ref _markedCopy, encoder);

    /// <summary>
    /// Hands the marked copy's encoder to exactly one caller - whichever of stopping the night or
    /// a failed write gets there first - so it is never finished and thrown away at the same time.
    /// </summary>
    public FfmpegEncoder? TakeMarkedCopy() => Interlocked.Exchange(ref _markedCopy, null);

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public RecordingSession(TimelapseManifest manifest, FfmpegEncoder encoder, string folder,
        string ffmpegPath, int width, int height)
    {
        Manifest = manifest;
        Encoder = encoder;
        Folder = folder;
        FfmpegPath = ffmpegPath;
        _width = width;
        _height = height;
    }

    public long CurrentSizeBytes
    {
        get
        {
            try
            {
                var p = Encoder.WorkingPath;
                return File.Exists(p) ? new FileInfo(p).Length : 0;
            }
            catch (IOException) { return 0; }
        }
    }

    private int _rawFrameIndex;

    /// <summary>
    /// Optional archive of the untouched sensor data as 16-bit greyscale PNG — still in its Bayer
    /// pattern, so it stays useful for stacking later. Off by default: this is the option that
    /// costs gigabytes a night, and the whole point of the video pipeline is not needing it.
    /// </summary>
    public void SaveRawFrame(byte[] raw, int width, int height)
    {
        try
        {
            var dir = Path.Combine(Folder, "frames");
            Directory.CreateDirectory(dir);

            var source = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Gray16, null, raw, width * 2);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

            var path = Path.Combine(dir, $"frame_{++_rawFrameIndex:D5}.png");
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // Archiving is best-effort; the video is the deliverable.
        }
    }

    public void WritePoster(byte[] rgb, int width, int height, string? fileName = null)
    {
        try
        {
            var source = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, rgb, width * 3);

            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

            var name = fileName ?? Manifest.PosterFile;
            var tmp = Path.Combine(Folder, name + ".tmp");
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, Path.Combine(Folder, name), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // A missing thumbnail is cosmetic; never let it interrupt a recording.
        }
    }
}
