using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace PierCam.Video;

/// <summary>
/// Plays a timelapse by decoding it through ffmpeg rather than through a media control.
///
/// WPF's MediaElement is a shell over Windows Media Player, and WMP is an optional component that
/// is simply absent on plenty of Windows 11 installs — including the observatory machine this was
/// written on, where it throws InvalidWmpVersionException on construction and the only recourse
/// was handing the file to whatever else was installed. ffmpeg is already a hard dependency for
/// recording, and it is already spoken to over a pipe, so decoding through it costs no new
/// dependency and removes an unreliable one. It also means playback looks identical everywhere
/// and can be drawn inside the app's own frame instead of a black box we do not control.
///
/// Measured on the largest night in the library — 2,374 frames — ffmpeg decodes and scales at
/// about 360 fps, roughly six times what playback needs, so the decode thread spends most of its
/// time asleep waiting for the next frame's turn.
///
/// The frame path is the same shape as the capture loop's: two buffers, swapped under a lock,
/// with a sequence number the UI compares against. Nothing is allocated per frame.
/// </summary>
internal sealed class FramePlayer : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly object _gate = new();

    private byte[]? _front;
    private byte[]? _back;
    private long _sequence;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private Process? _proc;

    private string _file = string.Empty;
    private volatile bool _playing;
    private volatile int _frameIndex;
    private double _speed = 1.0;

    /// <summary>Raised on the decode thread when the clip runs out and loops.</summary>
    public event Action? Looped;

    public FramePlayer(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameCount { get; private set; }
    public double Fps { get; private set; } = 30;

    public long FrameSequence => Interlocked.Read(ref _sequence);
    public bool IsPlaying => _playing;
    public bool IsOpen => _thread is not null;

    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / Math.Max(1, Fps));
    public TimeSpan Position => TimeSpan.FromSeconds(_frameIndex / Math.Max(1, Fps));

    /// <summary>Where playback is, as a fraction of the clip. Drives the seek bar.</summary>
    public double Progress => FrameCount <= 1 ? 0 : Math.Clamp(_frameIndex / (double)(FrameCount - 1), 0, 1);

    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.1, 8.0);
    }

    /// <summary>
    /// Opens a clip. The frame count and rate come from the session manifest rather than from
    /// probing the file: the manifest is written by the encoder that produced it, so it is both
    /// authoritative and free.
    /// </summary>
    public void Open(string file, int sourceWidth, int sourceHeight, int frameCount, double fps, int maxWidth = 1280)
    {
        Close();

        _file = file;
        FrameCount = Math.Max(1, frameCount);
        Fps = fps > 0 ? fps : 30;

        // Decode straight to the size it will be shown at. A 1080p frame is 6 MB raw; at 1280
        // wide it is 2.7 MB, and the scaling is free next to moving the bytes about.
        var w = Math.Min(maxWidth, sourceWidth > 0 ? sourceWidth : maxWidth);
        var h = sourceWidth > 0 && sourceHeight > 0
            ? (int)Math.Round(sourceHeight * (w / (double)sourceWidth))
            : (int)Math.Round(w * 9 / 16.0);

        Width = w % 2 == 0 ? w : w - 1;
        Height = h % 2 == 0 ? h : h - 1;

        // Reused across clips. Every night in the library is the same sensor size, so opening one
        // after another would otherwise throw away and re-allocate a pair of multi-megabyte
        // buffers each time — which in an app that is judged on not growing over days is exactly
        // the wrong habit, however willing the collector is to clean up afterwards.
        var bytes = Width * Height * 3;
        if (_front is null || _front.Length != bytes)
        {
            _front = new byte[bytes];
            _back = new byte[bytes];
        }
        _frameIndex = 0;
        _playing = true;

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "PierCam playback",
            // Below normal: a stuttering preview is a nuisance, a dropped capture frame is a
            // hole in the night. Capture wins every time.
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    public void Play() => _playing = true;
    public void Pause() => _playing = false;
    public void TogglePlay() => _playing = !_playing;

    /// <summary>Jumps to a fraction of the clip. The decoder restarts there.</summary>
    public void SeekTo(double progress)
    {
        if (!IsOpen) return;
        var target = (int)Math.Round(Math.Clamp(progress, 0, 1) * Math.Max(0, FrameCount - 1));
        if (target == _frameIndex) return;
        _frameIndex = target;
        Interlocked.Exchange(ref _restartAt, target + 1);   // +1 so 0 can mean "no request"
    }

    private long _restartAt;

    /// <summary>
    /// Hands the newest frame to <paramref name="use"/> if one has arrived since last time.
    /// The buffer is only valid for the duration of the call.
    /// </summary>
    public bool CopyLatestFrame(long lastSeen, Action<byte[]> use)
    {
        lock (_gate)
        {
            if (_front is null || _sequence == lastSeen) return false;
            use(_front);
            return true;
        }
    }

    // ── decode ──────────────────────────────────────────────────────────────

    private void Run(CancellationToken ct)
    {
        var frameBytes = Width * Height * 3;
        var reading = new byte[frameBytes];
        var clock = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var startFrame = _frameIndex;
            Process? proc = null;
            try
            {
                proc = StartFfmpeg(startFrame);
                if (proc is null) return;
                _proc = proc;

                var stream = proc.StandardOutput.BaseStream;
                var index = startFrame;
                var nextDue = clock.Elapsed;
                var shown = false;

                while (!ct.IsCancellationRequested)
                {
                    // A seek or a speed change restarts the pipe rather than trying to steer it.
                    var requested = Interlocked.Exchange(ref _restartAt, 0);
                    if (requested != 0 && requested - 1 != index) break;

                    if (!_playing)
                    {
                        // Paused: hold the last frame up and stop consuming. The pipe stays open,
                        // so resuming costs nothing. After a seek, or a switch to the night's other
                        // video, the frame to hold is the one parked on, so decode that one first.
                        if (!shown && ReadExactly(stream, reading, frameBytes, ct))
                        {
                            Present(reading);
                            _frameIndex = index;
                            index++;
                        }
                        shown = true;
                        ct.WaitHandle.WaitOne(30);
                        nextDue = clock.Elapsed;
                        continue;
                    }

                    if (!ReadExactly(stream, reading, frameBytes, ct))
                    {
                        // End of clip: loop from the top, which is what you want from a timelapse.
                        _frameIndex = 0;
                        Looped?.Invoke();
                        break;
                    }

                    Present(reading);
                    _frameIndex = index;
                    index++;
                    shown = true;

                    // Pace it. Decode runs far ahead of real time, so this is where the thread
                    // spends its life.
                    nextDue += TimeSpan.FromSeconds(1.0 / (Fps * _speed));
                    var wait = nextDue - clock.Elapsed;
                    if (wait > TimeSpan.Zero)
                    {
                        if (ct.WaitHandle.WaitOne(wait)) return;
                    }
                    else if (wait < TimeSpan.FromSeconds(-0.5))
                    {
                        // Fell a long way behind (a stall, or the machine is busy). Give up on
                        // catching up rather than sprinting through frames.
                        nextDue = clock.Elapsed;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The pipe went away underneath us — fall through and start it again.
            }
            finally
            {
                KillQuietly(proc);
                if (ReferenceEquals(_proc, proc)) _proc = null;
            }
        }
    }

    private Process? StartFfmpeg(int fromFrame)
    {
        var start = fromFrame / Math.Max(1, Fps);
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -ss ahead of -i seeks by keyframe before decoding, which is near-instant. These clips
        // are encoded with frequent keyframes, so it lands close enough to scrub by.
        if (start > 0.001)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(start.ToString("0.###", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(_file);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={Width}:{Height}:flags=bilinear");
        psi.ArgumentList.Add("-");

        try
        {
            var proc = Process.Start(psi);
            // stderr has to be drained or a chatty ffmpeg can fill the pipe and wedge itself.
            proc?.BeginErrorReadLine();
            return proc;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var got = 0;
        while (got < count)
        {
            if (ct.IsCancellationRequested) return false;
            var n = stream.Read(buffer, got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private void Present(byte[] frame)
    {
        lock (_gate)
        {
            if (_back is null) return;
            Buffer.BlockCopy(frame, 0, _back, 0, frame.Length);
            (_front, _back) = (_back, _front);
            _sequence++;
        }
    }

    // ── shutdown ────────────────────────────────────────────────────────────

    public void Close()
    {
        _playing = false;
        _cts?.Cancel();
        KillQuietly(_proc);
        _proc = null;

        if (_thread is { IsAlive: true } && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // The decode thread is a background thread, so a stuck one cannot hold the app open.
        }
        _thread = null;

        _cts?.Dispose();
        _cts = null;

        // The frame buffers are deliberately kept: this player is opened and closed repeatedly as
        // you flick through the library, and they are the same size every time. Dispose drops them.
        lock (_gate) { _sequence = 0; }
    }

    private static void KillQuietly(Process? proc)
    {
        if (proc is null) return;
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception) { }
        try { proc.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        Close();
        lock (_gate)
        {
            _front = null;
            _back = null;
        }
    }
}
