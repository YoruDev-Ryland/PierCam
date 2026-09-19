using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Capture;
using PierCam.Models;
using PierCam.Video;

namespace PierCam.Sky;

internal enum MarkerPhase { Off, NeedsSite, Searching, Calibrating, Calibrated, WaitingForNight, Collecting }

/// <summary>
/// Runs the target marker end to end: calibrates the camera by itself, reads where the telescope
/// is pointing, and tells the capture loop where to draw.
///
/// Calibration, in order of preference:
///   1. An earlier night already in the library. Only real PierCam recordings are trusted - they
///      carry the camera's serial, which demo and imported entries do not - and only nights whose
///      frame times can be reconstructed (no holds, no dropped frames), because a wrong clock gives
///      a calibration that fits the stars perfectly and points the wrong way. Most recent first; the
///      result is then checked against a second night if there is one.
///   2. Failing that, tonight's sky: one frame every ten minutes once it is properly dark.
/// Once a night it checks the calibration still fits, and starts over if the camera has moved.
///
/// Nothing here can hold up a frame. The capture loop only reads volatile references and hands
/// over a frame copy when asked; everything slow happens on below-normal-priority threads.
/// </summary>
internal sealed class TargetMarkerService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Func<(string? serial, int width, int height)> _camera;
    private readonly Action<Action> _onUi;
    private readonly NinaPointing _nina;
    private readonly LowPriorityScheduler _scheduler = new(2);
    private readonly BlockingCollection<(string path, string line)> _log = new();
    private readonly Thread _logWriter;
    private readonly Timer _tick;

    private CancellationTokenSource? _work;
    private volatile MarkerPhase _phase = MarkerPhase.Off;
    private volatile string _headline = "Off";
    private volatile string _detail = string.Empty;

    // What the capture thread reads. Swapped whole, never mutated.
    private volatile LensModel? _lens;
    private volatile ScaledLens? _scaled;
    private volatile bool _wantFrame;
    private volatile bool _dark;
    private volatile ColorRgb _color = new(255, 106, 43);

    private sealed record ScaledLens(int Width, int Height, LensModel Lens);
    private sealed record ColorRgb(byte R, byte G, byte B);

    // Live collection when the library could not help, and the nightly check.
    private readonly List<(DateTime utc, int w, int h, List<DetectedStar> stars)> _tonight = new();
    private DateTime _lastLiveFrame = DateTime.MinValue;
    private DateTime? _checkedNight;
    private bool _checking;

    public TargetMarkerService(AppSettings settings, Func<(string? serial, int width, int height)> camera, Action<Action> onUi)
    {
        _settings = settings;
        _camera = camera;
        _onUi = onUi;
        _nina = new NinaPointing(() => _settings.TargetMarker.NinaApiUrl);
        _nina.Changed += () => StatusChanged?.Invoke();
        _logWriter = new Thread(WriteLog) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PierCam marker log" };
        _logWriter.Start();
        _tick = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ───────────────────────────── status, for the config page ─────────────────────────────

    public MarkerPhase Phase => _phase;
    public string Headline => _headline;
    public string Detail => _detail;

    /// <summary>What the telescope is doing, as far as the marker is concerned.</summary>
    public string TelescopeLine
    {
        get
        {
            if (_phase == MarkerPhase.Off) return string.Empty;
            if (_nina.Problem.Length > 0) return _nina.Problem;
            var p = _nina.Latest;
            if (p is null) return "Waiting for N.I.N.A.";
            if (p.Parked) return "Telescope parked - no marker.";
            if (p.Slewing) return "Telescope slewing.";
            if (!p.Tracking) return "Telescope not tracking - no marker.";
            var where = _lens is null ? "" : AimDescription(p, DateTime.UtcNow);
            return $"Tracking{(p.TargetName is null ? "" : " " + p.TargetName)}{where}.";
        }
    }

    public event Action? StatusChanged;

    private void SetStatus(MarkerPhase phase, string headline, string detail)
    {
        _phase = phase; _headline = headline; _detail = detail;
        StatusChanged?.Invoke();
    }

    public void SetColor(byte r, byte g, byte b) => _color = new ColorRgb(r, g, b);

    // ───────────────────────────── switching on and off ─────────────────────────────

    /// <summary>Call on the UI thread after loading settings, and whenever the switch changes.</summary>
    public void Apply()
    {
        var s = _settings.TargetMarker;
        if (!s.Enabled)
        {
            _work?.Cancel();
            _nina.Stop();
            _wantFrame = false;
            _lens = null; _scaled = null;
            _tick.Change(Timeout.Infinite, Timeout.Infinite);
            SetStatus(MarkerPhase.Off, "Off", "Turn on to mark where the telescope is pointing.");
            return;
        }

        _nina.Start();
        _tick.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
        if (!_settings.Site.IsSet)
        {
            SetStatus(MarkerPhase.NeedsSite, "Needs your site", "Set latitude and longitude under Site & roof - the marker needs them.");
            return;
        }

        var rec = s.Calibration;
        var cam = _camera();
        if (rec is not null && rec.Lens.IsUsable && (cam.serial is null || rec.CameraSerial.Length == 0 || rec.CameraSerial == cam.serial))
        {
            UseCalibration(rec);
            return;
        }
        StartLibraryCalibration();
    }

    /// <summary>Throw away the current calibration and work it out again.</summary>
    public void Recalibrate()
    {
        _onUi(() => { _settings.TargetMarker.Calibration = null; _settings.Save(); });
        _lens = null; _scaled = null;
        lock (_tonight) _tonight.Clear();
        if (_settings.TargetMarker.Enabled && _settings.Site.IsSet) StartLibraryCalibration();
        else Apply();
    }

    private void UseCalibration(LensCalibrationRecord rec)
    {
        _lens = rec.Lens; _scaled = null;
        var confirmed = rec.ConfirmedOn is null ? "" : $", confirmed on {rec.ConfirmedOn}";
        SetStatus(MarkerPhase.Calibrated, "Calibrated",
            $"From {rec.Source}: {rec.Stars} stars matched, {rec.RmsPx:0.0} px{confirmed}.");
    }

    // ───────────────────────────── calibrating from the library ─────────────────────────────

    private void StartLibraryCalibration()
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        _lens = null; _scaled = null;
        SetStatus(MarkerPhase.Searching, "Looking for a clear night in the library", "");
        Task.Factory.StartNew(() => CalibrateFromLibrary(cts.Token), cts.Token, TaskCreationOptions.LongRunning, _scheduler);
    }

    private sealed record Night(TimelapseManifest M, int VideoW, int VideoH, int CameraW, int CameraH, Dictionary<int, DateTime>? ExactTimes);

    private void CalibrateFromLibrary(CancellationToken ct)
    {
        try
        {
            var cam = _camera();
            var nights = EligibleNights(cam.serial, out var whyNone);
            if (nights.Count == 0)
            {
                WaitForTonight(whyNone);
                return;
            }

            string? lastReason = null;
            for (var i = 0; i < Math.Min(4, nights.Count); i++)
            {
                ct.ThrowIfCancellationRequested();
                var night = nights[i];
                var label = NightLabel(night.M);
                SetStatus(MarkerPhase.Calibrating, $"Calibrating from {label}", "Reading frames");
                var frames = ReadFrames(night, 9, ct, n => SetStatus(MarkerPhase.Calibrating, $"Calibrating from {label}", $"Reading frames: {n} of 9"));
                if (frames.Count < 3) { lastReason = $"{label}: not enough dark frames."; continue; }

                var calibrator = new LensCalibrator(_settings.Site.Latitude, _settings.Site.Longitude, 2, ct, new SyncProgress(msg =>
                    SetStatus(MarkerPhase.Calibrating, $"Calibrating from {label}", msg)), _scheduler);
                var result = calibrator.Calibrate(frames);
                if (!result.Success || result.Lens is null)
                {
                    lastReason = $"{label}: {result.Summary}";
                    App.Note($"Target marker: could not calibrate from {label} - {result.Summary}", "Marker");
                    continue;
                }

                var lens = result.Lens.ScaledTo(night.CameraW, night.CameraH) ?? result.Lens;
                var record = new LensCalibrationRecord
                {
                    Lens = lens, CalibratedUtc = DateTime.UtcNow, Source = label,
                    CameraSerial = night.M.CameraSerial ?? string.Empty, Stars = result.Stars, RmsPx = Math.Round(result.RmsPx, 2),
                };

                // A second opinion from another night, if there is one: same camera, same answer.
                foreach (var other in nights.Skip(i + 1).Take(2))
                {
                    ct.ThrowIfCancellationRequested();
                    var otherLabel = NightLabel(other.M);
                    SetStatus(MarkerPhase.Calibrating, $"Calibrating from {label}", $"Confirming against {otherLabel}");
                    var check = ReadFrames(other, 3, ct, _ => { });
                    if (check.Count < 2) continue;
                    var model = lens.ScaledTo(check[0].Width, check[0].Height);
                    if (model is null) continue;
                    var (matched, rms) = calibrator.Check(model, check);
                    var perFrame = (double)result.Stars / Math.Max(1, result.Frames);
                    if (matched >= perFrame * check.Count * 0.5 && rms < 2.5) { record.ConfirmedOn = otherLabel; break; }
                    App.Note($"Target marker: {otherLabel} did not confirm the calibration ({matched} stars, {rms:0.0} px) - camera moved between nights?", "Marker");
                }

                App.Note($"Target marker calibrated from {label}: {result.Summary}; {lens}", "Marker");
                _onUi(() => { _settings.TargetMarker.Calibration = record; _settings.Save(); });
                UseCalibration(record);
                return;
            }
            WaitForTonight(lastReason ?? "No earlier night could be used.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log(ex, "Marker calibration");
            WaitForTonight("Calibrating from the library failed unexpectedly - see the log.");
        }
        finally { ReturnScratchMemory(); }
    }

    /// <summary>
    /// A calibration run churns through large scratch arrays, and the runtime keeps the memory
    /// they used committed long afterwards; one aggressive collection hands it back to Windows.
    /// </summary>
    private static void ReturnScratchMemory() =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    private void WaitForTonight(string why)
    {
        lock (_tonight) _tonight.Clear();
        SetStatus(MarkerPhase.WaitingForNight, "Waiting for a clear, dark night", why);
    }

    private List<Night> EligibleNights(string? cameraSerial, out string whyNone)
    {
        var list = new List<Night>();
        whyNone = "There are no recorded nights to learn from yet.";
        var root = _settings.TimelapseRoot;
        if (!Directory.Exists(root)) return list;

        var seen = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var m = TimelapseManifest.TryLoad(dir);
            if (m is null) continue;
            seen++;
            if (m.Status is not (SessionStatus.Complete or SessionStatus.Interrupted) || m.FrameCount < 30 || m.EndedLocal is null) continue;
            if (!File.Exists(m.VideoPath) || m.Width <= 0 || m.Height <= 0) continue;
            // Real PierCam nights record the camera's serial; demo and imported entries do not, and
            // their dates cannot be trusted.
            if (string.IsNullOrWhiteSpace(m.CameraSerial)) continue;
            if (cameraSerial is not null && m.CameraSerial != cameraSerial) continue;

            var exact = ReadExactTimes(dir);
            // Without exact per-frame times, the times have to be reconstructed from the schedule,
            // which only holds for a night that ran without pauses or gaps.
            if (exact is null && (m.HeldMinutes > 0 || m.DroppedFrames > 0)) continue;

            int camW = m.OriginalWidth > 0 ? m.OriginalWidth : m.Width, camH = m.OriginalHeight > 0 ? m.OriginalHeight : m.Height;
            list.Add(new Night(m, m.Width, m.Height, camW, camH, exact));
        }
        if (seen > 0 && list.Count == 0)
            whyNone = "None of the recordings in the library can be used: they need to be complete PierCam nights from this camera, without pauses.";
        return list.OrderByDescending(n => n.M.StartedLocal).ToList();
    }

    private static string NightLabel(TimelapseManifest m) =>
        SunCalculator.NightDateFor(m.StartedLocal).ToString("ddd d MMM", CultureInfo.CurrentCulture) + " night";

    /// <summary>Mid-exposure time of frame n of a recording.</summary>
    private static DateTime FrameUtc(Night night, int n)
    {
        if (night.ExactTimes is not null && night.ExactTimes.TryGetValue(n, out var exact)) return exact;
        var m = night.M;
        var start = m.StartedLocal.Kind == DateTimeKind.Utc ? m.StartedLocal : m.StartedLocal.ToUniversalTime();
        // The first frame lands one exposure plus a few seconds of setup after the session starts;
        // interval mode then runs on an absolute schedule, so frame n is exactly n intervals later.
        var first = start.AddSeconds(m.ExposureSeconds / 2 + 5);
        if (m.IntervalSeconds > 0) return first.AddSeconds((double)n * m.IntervalSeconds);
        var end = m.EndedLocal!.Value.Kind == DateTimeKind.Utc ? m.EndedLocal.Value : m.EndedLocal.Value.ToUniversalTime();
        var cadence = Math.Max(m.ExposureSeconds, ((end - start).TotalSeconds - m.ExposureSeconds - 5) / Math.Max(1, m.FrameCount));
        return first.AddSeconds(n * cadence);
    }

    /// <summary>Picks up to <paramref name="want"/> properly dark frames spread across the night, and finds their stars.</summary>
    private List<CalibrationFrame> ReadFrames(Night night, int want, CancellationToken ct, Action<int> progress)
    {
        var m = night.M;
        var dark = Enumerable.Range(0, m.FrameCount)
            .Where(n => SunCalculator.Altitude(FrameUtc(night, n), _settings.Site.Latitude, _settings.Site.Longitude) < -15)
            .ToList();
        if (dark.Count < 3) return new List<CalibrationFrame>();
        var picks = Enumerable.Range(0, want).Select(i => dark[(int)Math.Round((dark.Count - 1) * (want == 1 ? 0.5 : i / (double)(want - 1)))])
            .Distinct().ToList();

        var k = StarDetector.ReductionFor(night.VideoW, night.VideoH);
        var ignore = TimestampRect(night.VideoW, night.VideoH, night.CameraW, k);
        var found = new List<(DateTime utc, List<DetectedStar> stars)>();
        int rw = night.VideoW / k, rh = night.VideoH / k;
        foreach (var n in picks)
        {
            ct.ThrowIfCancellationRequested();
            var luma = ExtractFrame(m.VideoPath, n, m.Fps, night.VideoW, night.VideoH, ct);
            progress(found.Count + 1);
            if (luma is null) continue;
            var small = StarDetector.Reduce(luma, night.VideoW, night.VideoH, k, out rw, out rh);
            found.Add((FrameUtc(night, n), StarDetector.Find(small, rw, rh, ignore)));
        }
        StarDetector.DropStatic(found);
        return found.Select(f => new CalibrationFrame(f.utc, rw, rh, f.stars)).ToList();
    }

    /// <summary>Where a burned-in timestamp sits on a frame of this size (it was drawn at camera resolution).</summary>
    private IReadOnlyList<PixelRect> TimestampRect(int w, int h, int cameraW, int reduction)
    {
        var (x, y, rw, rh) = TextOverlay.TimestampFootprint(cameraW, (int)Math.Round((double)h * cameraW / w), Math.Max(1, _settings.Video.TimestampScale));
        var s = (double)w / cameraW / reduction;
        var ry = (int)(y * s); var rhh = (int)Math.Ceiling(rh * s) + 2;
        return new[] { new PixelRect(0, ry, (int)Math.Ceiling(rw * s) + 8, rhh + 8) };
    }

    /// <summary>One frame of a recording as 8-bit luma, decoded by ffmpeg at low priority.</summary>
    private byte[]? ExtractFrame(string video, int n, int fps, int w, int h, CancellationToken ct)
    {
        var ffmpeg = FfmpegEncoder.Locate(_settings.FfmpegPath);
        if (ffmpeg is null) return null;
        // ffmpeg keeps the first frame stamped at or after -ss, so aim half a frame early: frame n
        // is then the first one kept, with no rounding trouble at the boundary.
        var t = (Math.Max(0, n - 0.5) / Math.Max(1, fps)).ToString("0.###", CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(ffmpeg, $"-v error -ss {t} -i \"{video}\" -frames:v 1 -f rawvideo -pix_fmt gray -")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p is null) return null;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        _ = p.StandardError.ReadToEndAsync(ct);
        var buf = new byte[w * h];
        var read = 0;
        var s = p.StandardOutput.BaseStream;
        while (read < buf.Length)
        {
            ct.ThrowIfCancellationRequested();
            var k = s.Read(buf, read, buf.Length - read);
            if (k <= 0) break;
            read += k;
        }
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        return read == buf.Length ? buf : null;
    }

    // ───────────────────────────── tonight's sky ─────────────────────────────

    private void Tick()
    {
        try
        {
            if (!_settings.TargetMarker.Enabled) return;
            if (_phase == MarkerPhase.NeedsSite)
            {
                // Coordinates entered since: carry on from where it stopped.
                if (_settings.Site.IsSet) _onUi(Apply);
                return;
            }
            _dark = SunCalculator.Altitude(DateTime.UtcNow, _settings.Site.Latitude, _settings.Site.Longitude) < -15;

            // A different camera, or a different crop of this one, needs its own calibration.
            var cam = _camera();
            var rec = _settings.TargetMarker.Calibration;
            if (_phase == MarkerPhase.Calibrated && rec is not null && cam.serial is not null && rec.CameraSerial.Length > 0 && rec.CameraSerial != cam.serial)
            {
                Recalibrate();
                return;
            }

            var tonight = SunCalculator.NightDateFor(DateTime.Now).Date;
            var wantLive = _dark && (_phase is MarkerPhase.WaitingForNight or MarkerPhase.Collecting)
                                 && DateTime.UtcNow - _lastLiveFrame > TimeSpan.FromMinutes(10);
            var wantCheck = _dark && _phase == MarkerPhase.Calibrated && _checkedNight != tonight && !_checking
                                  && DateTime.UtcNow - _lastLiveFrame > TimeSpan.FromMinutes(10);
            _wantFrame = wantLive || wantCheck;
            StatusChanged?.Invoke();
        }
        catch (Exception ex) { App.Log(ex, "Marker tick"); }
    }

    /// <summary>Cheap enough to ask every frame.</summary>
    public bool WantsCalibrationFrame => _wantFrame && _settings.TargetMarker.Enabled;

    /// <summary>
    /// A processed frame, before anything is drawn on it. Called on the capture thread; takes a
    /// luma copy (a few milliseconds, once every ten minutes at most) and returns at once.
    /// </summary>
    public void OfferCalibrationFrame(byte[] rgb, int w, int h, DateTime utcMid)
    {
        if (!_wantFrame) return;
        _wantFrame = false;
        _lastLiveFrame = DateTime.UtcNow;
        var luma = StarDetector.LumaFromRgb24(rgb, w, h);
        Task.Factory.StartNew(() => UseLiveFrame(luma, w, h, utcMid), CancellationToken.None, TaskCreationOptions.None, _scheduler);
    }

    private void UseLiveFrame(byte[] luma, int w, int h, DateTime utc)
    {
        try
        {
            var k = StarDetector.ReductionFor(w, h);
            var small = StarDetector.Reduce(luma, w, h, k, out var rw, out var rh);
            var stars = StarDetector.Find(small, rw, rh);

            if (_phase == MarkerPhase.Calibrated && _lens is { } lens)
            {
                // The nightly check: does the calibration still describe this sky?
                _checking = true;
                try
                {
                    _checkedNight = SunCalculator.NightDateFor(DateTime.Now).Date;
                    var model = lens.ScaledTo(rw, rh);
                    if (model is null || stars.Count < 150) return;           // cloudy: nothing to judge by
                    var calibrator = new LensCalibrator(_settings.Site.Latitude, _settings.Site.Longitude, 1, CancellationToken.None);
                    var (matched, rms) = calibrator.Check(model, new[] { new CalibrationFrame(utc, rw, rh, stars) });
                    var rec = _settings.TargetMarker.Calibration;
                    var expected = rec is null ? 60 : Math.Max(20, rec.Stars / 9.0);
                    App.Note($"Target marker nightly check: {matched} stars matched ({expected:0} expected), {rms:0.0} px", "Marker");
                    if (matched < expected * 0.25)
                    {
                        _lens = null; _scaled = null;
                        lock (_tonight) _tonight.Clear();
                        SetStatus(MarkerPhase.Collecting, "Recalibrating from tonight's sky",
                            "The calibration no longer matches the stars - has the camera moved?");
                    }
                }
                finally { _checking = false; }
                return;
            }

            List<(DateTime utc, int w, int h, List<DetectedStar> stars)> snapshot;
            lock (_tonight)
            {
                if (stars.Count >= 40) _tonight.Add((utc, rw, rh, stars));
                while (_tonight.Count > 9) _tonight.RemoveAt(0);
                snapshot = _tonight.ToList();
            }
            var spanMin = snapshot.Count < 2 ? 0 : (snapshot[^1].utc - snapshot[0].utc).TotalMinutes;
            if (snapshot.Count < 4 || spanMin < 30)
            {
                SetStatus(MarkerPhase.Collecting, "Learning from tonight's sky",
                    $"{snapshot.Count} of 4 clear frames so far, one every ten minutes." + (stars.Count < 40 ? " The last one was too cloudy to use." : ""));
                return;
            }

            SetStatus(MarkerPhase.Calibrating, "Calibrating from tonight's sky", "Searching for the camera's orientation");
            var list = snapshot.Select(s => (s.utc, stars: s.stars.ToList())).ToList();
            StarDetector.DropStatic(list);
            var frames = list.Select((s, i) => new CalibrationFrame(s.utc, snapshot[i].w, snapshot[i].h, s.stars)).ToList();
            var cal = new LensCalibrator(_settings.Site.Latitude, _settings.Site.Longitude, 2, CancellationToken.None,
                new SyncProgress(msg => SetStatus(MarkerPhase.Calibrating, "Calibrating from tonight's sky", msg)), _scheduler);
            var result = cal.Calibrate(frames);
            ReturnScratchMemory();
            if (!result.Success || result.Lens is null)
            {
                SetStatus(MarkerPhase.Collecting, "Learning from tonight's sky", $"Not yet: {result.Summary} Trying again with the next frame.");
                return;
            }
            var cam = _camera();
            var full = result.Lens.ScaledTo(w, h) ?? result.Lens;
            var record = new LensCalibrationRecord
            {
                Lens = full, CalibratedUtc = DateTime.UtcNow, Source = "tonight's sky",
                CameraSerial = cam.serial ?? string.Empty, Stars = result.Stars, RmsPx = Math.Round(result.RmsPx, 2),
            };
            App.Note($"Target marker calibrated from tonight's sky: {result.Summary}; {full}", "Marker");
            _onUi(() => { _settings.TargetMarker.Calibration = record; _settings.Save(); });
            UseCalibration(record);
        }
        catch (Exception ex) { App.Log(ex, "Marker live frame"); }
    }

    // ───────────────────────────── the capture loop's side ─────────────────────────────

    public bool BurnIntoRecordings => _settings.TargetMarker.BurnIntoRecordings;

    /// <summary>Whether a new recording should keep a clean video and burn the marker into a second copy.</summary>
    public bool WantsMarkedCopy =>
        _settings.TargetMarker is { Enabled: true, BurnIntoRecordings: true, KeepUnmarkedOriginal: true };

    /// <summary>Where to draw on a frame of this size right now, or null for no marker.</summary>
    public MarkerPlacement? PlacementFor(DateTime utc, int w, int h)
    {
        if (!_settings.TargetMarker.Enabled || _phase != MarkerPhase.Calibrated) return null;
        var lens = LensFor(w, h);
        var p = _nina.Latest;
        if (lens is null || p is null || utc - p.ReceivedUtc > NinaPointing.MaxAge || p.Parked || p.Slewing || !p.Tracking) return null;
        var (alt, az) = SkyMath.ToAltAz(p.RaHoursJNow * 15, p.DecDegJNow, utc, _settings.Site.Latitude, _settings.Site.Longitude);
        if (alt < 0) return null;
        alt += SkyMath.Refraction(alt);
        if (!lens.TryProject(alt, az, out var x, out var y)) return null;
        var margin = 0.02 * w;
        if (x < -margin || y < -margin || x > w + margin || y > h + margin) return null;
        return new MarkerPlacement(x, y, p.TargetName);
    }

    public void Draw(byte[] rgb, int w, int h, MarkerPlacement p)
    {
        var c = _color;
        ReticleDrawer.Draw(rgb, w, h, p, c.R, c.G, c.B);
    }

    private LensModel? LensFor(int w, int h)
    {
        var s = _scaled;
        if (s is not null && s.Width == w && s.Height == h) return s.Lens;
        var lens = _lens?.ScaledTo(w, h);
        if (lens is null) return null;
        _scaled = new ScaledLens(w, h, lens);
        return lens;
    }

    private string AimDescription(Pointing p, DateTime utc)
    {
        var (w, h) = (_camera().width, _camera().height);
        if (w <= 0 || h <= 0) return "";
        var (alt, _) = SkyMath.ToAltAz(p.RaHoursJNow * 15, p.DecDegJNow, utc, _settings.Site.Latitude, _settings.Site.Longitude);
        if (alt < 0) return ", below the horizon";
        return PlacementFor(utc, w, h) is null ? $", {alt:0} deg up - outside the camera's view" : $", {alt:0} deg up - marked";
    }

    /// <summary>
    /// One line per recorded frame, while the marker is on: exact capture time, and where the
    /// telescope was pointing. Future calibrations from this night then need no reconstruction of
    /// times, and the marker can be drawn over the night later on.
    /// </summary>
    public void FrameRecorded(string sessionFolder, long index, DateTime utcMid, MarkerPlacement? placement)
    {
        if (!_settings.TargetMarker.Enabled) return;
        var p = _nina.Latest;
        var fresh = p is not null && utcMid - p.ReceivedUtc < NinaPointing.MaxAge;
        var line = string.Join(",",
            index.ToString(CultureInfo.InvariantCulture),
            utcMid.ToString("o", CultureInfo.InvariantCulture),
            fresh ? p!.RaHoursJNow.ToString("0.######", CultureInfo.InvariantCulture) : "",
            fresh ? p!.DecDegJNow.ToString("0.#####", CultureInfo.InvariantCulture) : "",
            fresh ? (p!.Tracking && !p.Parked && !p.Slewing ? "1" : "0") : "",
            placement is { } q ? q.X.ToString("0.#", CultureInfo.InvariantCulture) : "",
            placement is { } r ? r.Y.ToString("0.#", CultureInfo.InvariantCulture) : "",
            fresh && p!.TargetName is not null ? '"' + p.TargetName.Replace("\"", "'") + '"' : "");
        try { _log.TryAdd((Path.Combine(sessionFolder, MarkerLogFile), line)); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    public const string MarkerLogFile = "marker.csv";
    private const string LogHeader = "frame,utc,ra_hours_jnow,dec_deg_jnow,tracking,marker_x,marker_y,target";

    private void WriteLog()
    {
        foreach (var (path, line) in _log.GetConsumingEnumerable())
        {
            try
            {
                var fresh = !File.Exists(path);
                File.AppendAllText(path, (fresh ? LogHeader + Environment.NewLine : "") + line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<int, DateTime>? ReadExactTimes(string folder)
    {
        var path = Path.Combine(folder, MarkerLogFile);
        if (!File.Exists(path)) return null;
        try
        {
            var d = new Dictionary<int, DateTime>();
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                    DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                    d[n] = t.ToUniversalTime();
            }
            return d.Count > 0 ? d : null;
        }
        catch (IOException) { return null; }
    }

    public void Dispose()
    {
        _work?.Cancel();
        _tick.Dispose();
        _nina.Dispose();
        _log.CompleteAdding();
        _logWriter.Join(TimeSpan.FromSeconds(2));
        _scheduler.Dispose();
    }

    /// <summary>IProgress that reports on the calling thread rather than capturing a context.</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _a;
        public SyncProgress(Action<string> a) => _a = a;
        public void Report(string value) => _a(value);
    }
}
