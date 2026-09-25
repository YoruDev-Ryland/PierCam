using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PierCam.Video;

/// <summary>How hard to denoise before encoding. See <see cref="FfmpegEncoder.DenoiseFilter"/>.</summary>
internal enum DenoiseLevel { Off, Light, Medium, Strong }

internal sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Streams RGB24 frames straight into an H.264 encoder.
///
/// This is the whole space story. SharpCap's PNG-per-frame output runs ~3.8 MB a frame, so a
/// 2,300-frame night costs about 9 GB. Piping the same frames into x264 at a constant quality
/// writes a single file in the tens of megabytes, and writes it *as the night goes*, so there is
/// no post-processing pass and no point where the full frame set exists on disk.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly ConcurrentQueue<string> _stderr = new();
    private readonly string _partPath;
    private readonly string _finalPath;
    private readonly int _frameBytes;
    private bool _finished;
    private bool _disposed;

    public long FramesWritten { get; private set; }
    public string FinalPath => _finalPath;

    /// <summary>Where the encoder is right now — the fragmented file that survives a crash.</summary>
    public string WorkingPath => _partPath;

    public static string? Locate(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var local = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;

        local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>
    /// Spatial:temporal denoise strengths for hqdn3d.
    ///
    /// Sensor noise is what makes an astro timelapse expensive to encode — it changes completely
    /// every frame, so the encoder cannot predict any of it. Denoising before encoding cuts the
    /// bitrate several-fold *and* looks better.
    ///
    /// The temporal terms are deliberately kept small. hqdn3d's temporal filter averages a pixel
    /// against the same pixel in previous frames, and in a timelapse the sky rotates between
    /// frames — turn it up and stars smear into short trails.
    /// </summary>
    /// <summary>
    /// The real output size for a sensor inside a chosen resolution box.
    ///
    /// The resolution picker offers 16:9 sizes, but sensors are not all 16:9 — the ASI676MC is
    /// square, an ASI294 is 4:3, an ASI2600 is 3:2. Scaling a square frame to 1920×1080 does not
    /// crop it, it *stretches* it, and the night is ruined in a way no later pass can undo. So the
    /// chosen size is treated as a box to fit inside, keeping the sensor's own shape: a square
    /// sensor asked for 1920×1080 records 1080×1080.
    ///
    /// It never enlarges, either. A 1304×976 sensor asked for 1080p gains nothing from being
    /// blown up and would cost bitrate for the privilege, so it records at its own size.
    ///
    /// Both dimensions come back even, which H.264 with yuv420p requires.
    /// </summary>
    public static (int Width, int Height) FitWithin(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (Even(boxWidth), Even(boxHeight));
        if (boxWidth <= 0 || boxHeight <= 0) return (Even(sourceWidth), Even(sourceHeight));

        var scale = Math.Min(boxWidth / (double)sourceWidth, boxHeight / (double)sourceHeight);
        if (scale >= 1) return (Even(sourceWidth), Even(sourceHeight));

        return (Even((int)Math.Round(sourceWidth * scale)), Even((int)Math.Round(sourceHeight * scale)));

        static int Even(int v) => Math.Max(2, v - (v & 1));
    }

    public static string? DenoiseFilter(DenoiseLevel level) => level switch
    {
        DenoiseLevel.Light => "hqdn3d=3:2:1:1",
        DenoiseLevel.Medium => "hqdn3d=6:4:2:2",
        DenoiseLevel.Strong => "hqdn3d=10:7:3:3",
        _ => null
    };

    public FfmpegEncoder(string ffmpegPath, string finalPath, int width, int height,
        int inputFps, int crf, string preset, int outputWidth, int outputHeight,
        DenoiseLevel denoise = DenoiseLevel.Off)
    {
        _finalPath = finalPath;
        _partPath = Path.ChangeExtension(finalPath, ".part.mp4");
        _frameBytes = width * height * 3;

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        TryDelete(_partPath);

        var args = new StringBuilder();
        args.Append("-hide_banner -nostdin -loglevel warning -y ");
        args.Append($"-f rawvideo -pixel_format rgb24 -video_size {width}x{height} -framerate {inputFps} -i pipe:0 ");
        args.Append("-an ");

        var filters = new List<string>();
        if (DenoiseFilter(denoise) is { } dn) filters.Add(dn);
        if (outputWidth != width || outputHeight != height)
            filters.Add($"scale={outputWidth}:{outputHeight}:flags=lanczos");
        if (filters.Count > 0) args.Append($"-vf \"{string.Join(",", filters)}\" ");
        args.Append($"-c:v libx264 -preset {preset} -crf {crf} -pix_fmt yuv420p ");
        args.Append($"-g {Math.Max(1, inputFps * 2)} ");
        // Fragmented MP4 while recording: if the machine loses power at 04:00, the partial file
        // is still a playable video of everything captured up to that point.
        args.Append("-movflags +frag_keyframe+empty_moov+default_base_moof ");
        args.Append($"-f mp4 \"{_partPath}\"");

        var psi = new ProcessStartInfo(ffmpegPath, args.ToString())
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is null) return;
            _stderr.Enqueue(e.Data);
            // Keep only the tail: ffmpeg can be chatty and this runs for a whole night.
            while (_stderr.Count > 40) _stderr.TryDequeue(out _);
        };

        if (!_proc.Start()) throw new InvalidOperationException("Could not start ffmpeg.");
        _proc.BeginErrorReadLine();
        _stdin = _proc.StandardInput.BaseStream;
    }

    /// <summary>Appends one frame. Throws if ffmpeg has died so the session can report it.</summary>
    public void WriteFrame(byte[] rgb24)
    {
        if (_finished) throw new InvalidOperationException("Encoder already finished.");
        if (rgb24.Length < _frameBytes) throw new ArgumentException("Frame buffer too small.", nameof(rgb24));

        try
        {
            _stdin.Write(rgb24, 0, _frameBytes);
            FramesWritten++;
        }
        catch (IOException ex)
        {
            throw new IOException($"ffmpeg stopped accepting frames. {LastError()}", ex);
        }
    }

    public void Flush() => _stdin.Flush();

    /// <summary>
    /// Closes the stream and remuxes the fragmented file into a normal, seekable MP4.
    /// The remux is a stream copy, so it takes a second or two regardless of night length.
    /// </summary>
    public void Finish(string ffmpegPath, TimeSpan timeout)
    {
        if (_finished) return;
        _finished = true;

        try { _stdin.Flush(); _stdin.Close(); } catch (IOException) { /* already gone */ }

        if (!_proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { _proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _proc.WaitForExit(5000);
        }

        if (FramesWritten == 0)
        {
            TryDelete(_partPath);
            return;
        }

        if (!Remux(ffmpegPath))
        {
            // Remux failed — keep the fragmented file under the final name rather than lose
            // the night. It still plays; it just is not optimally seekable.
            TryDelete(_finalPath);
            try { File.Move(_partPath, _finalPath); } catch (IOException) { }
        }
    }

    private bool Remux(string ffmpegPath)
    {
        if (!File.Exists(_partPath)) return false;
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath,
                $"-hide_banner -nostdin -loglevel error -y -i \"{_partPath}\" -c copy -movflags +faststart \"{_finalPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0 || !File.Exists(_finalPath)) return false;
            TryDelete(_partPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public string LastError()
    {
        var lines = _stderr.ToArray();
        return lines.Length == 0 ? "(no ffmpeg output)" : string.Join(" | ", lines[^Math.Min(5, lines.Length)..]);
    }

    public bool HasExited
    {
        get { try { return _proc.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_finished) { _stdin.Close(); _proc.WaitForExit(10_000); } } catch (Exception) { /* shutting down */ }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch (Exception) { /* shutting down */ }
        _proc.Dispose();
    }
}
