using System;
using PierCam.Models;

namespace PierCam.Capture;

internal enum SchedulerAction
{
    None,
    /// <summary>Begin a new session for tonight.</summary>
    Start,
    /// <summary>Finish the session — the night is over.</summary>
    Stop,
    /// <summary>Keep the session but stop adding frames (roof shut).</summary>
    Hold,
    /// <summary>Resume adding frames.</summary>
    Resume
}

/// <summary>What the scheduler currently thinks, for display.</summary>
internal sealed record ScheduleView(
    bool WindowOpen,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    string Summary,
    string RoofSummary);

/// <summary>
/// Decides whether a recording should be running right now, and whether it should be adding
/// frames.
///
/// Two independent gates:
///
///   · the dark window — either fixed clock times, or real astronomical twilight for the site,
///     which is what you actually want because it tracks the seasons on its own;
///   · the roof — if the observatory never opens there is nothing to film, and if it opens at
///     two in the morning the timelapse should pick up from there.
///
/// The roof gate holds rather than stops, so a night with the roof opening and closing produces
/// one continuous video with gaps rather than a scatter of fragments.
/// </summary>
internal sealed class NightlyScheduler
{
    private readonly AppSettings _settings;
    private readonly Func<RoofStatus> _roof;
    private DateTime? _completedNight;

    public NightlyScheduler(AppSettings settings, Func<RoofStatus> roof)
    {
        _settings = settings;
        _roof = roof;
    }

    private SessionSettings S => _settings.Session;

    // ── window ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dark window covering <paramref name="now"/>, or the next one if we are between
    /// nights. Falls back to the fixed times whenever the sun never reaches the chosen depth.
    /// </summary>
    public DarkWindow? WindowFor(DateTime now)
    {
        if (S.Schedule == ScheduleMode.Astronomical)
        {
            var window = AstronomicalWindow(now);
            if (window is not null) return window;
        }
        return FixedWindow(now);
    }

    private DarkWindow? AstronomicalWindow(DateTime now)
    {
        var site = _settings.Site;
        if (!site.IsSet) return null;

        // The night that contains "now" starts either today or yesterday, depending on which
        // side of local noon we are on.
        var nightDate = SunCalculator.NightDateFor(now);
        var tonight = SunCalculator.NightOf(nightDate, site.Latitude, site.Longitude, S.Twilight);

        if (tonight is { } t && now < t.End) return t;

        // Tonight's darkness is already over; report the next one.
        return SunCalculator.NightOf(now.Date, site.Latitude, site.Longitude, S.Twilight)
               ?? SunCalculator.NightOf(now.Date.AddDays(1), site.Latitude, site.Longitude, S.Twilight);
    }

    private DarkWindow FixedWindow(DateTime now)
    {
        var nightDate = S.StartTime > S.EndTime
            ? (now.TimeOfDay < S.EndTime ? now.Date.AddDays(-1) : now.Date)
            : now.Date;

        var start = nightDate + S.StartTime;
        var end = nightDate + S.EndTime;
        if (S.StartTime > S.EndTime) end = end.AddDays(1);
        return new DarkWindow(start, end);
    }

    public bool IsWindowOpen(DateTime now) => WindowFor(now) is { } w && w.Contains(now);

    /// <summary>Identifies the night, so a manual stop is not undone but tomorrow still arms.</summary>
    public DateTime NightKey(DateTime now) => SunCalculator.NightDateFor(now);

    public DateTime? NextStart(DateTime now)
    {
        var w = WindowFor(now);
        if (w is null) return null;
        if (w.Value.Start > now) return w.Value.Start;

        // Inside or past tonight's window — look at tomorrow.
        if (S.Schedule == ScheduleMode.Astronomical && _settings.Site.IsSet)
        {
            var next = SunCalculator.NightOf(now.Date, _settings.Site.Latitude,
                _settings.Site.Longitude, S.Twilight);
            if (next is { } n && n.Start > now) return n.Start;
            return SunCalculator.NightOf(now.Date.AddDays(1), _settings.Site.Latitude,
                _settings.Site.Longitude, S.Twilight)?.Start;
        }
        return FixedWindow(now).Start.AddDays(1);
    }

    // ── roof ──────────────────────────────────────────────────────────────────

    /// <summary>Whether the roof itself permits frames, before any run-on is considered.</summary>
    private bool RoofOpenNow(out string reason)
    {
        if (!S.RequireRoofOpen) { reason = "roof gate off"; return true; }

        var status = _roof();
        switch (status.State)
        {
            case RoofState.Open:
                reason = "roof open";
                return true;
            case RoofState.Closed:
                reason = "roof closed";
                return false;
            default:
                // Unknown is deliberately treated as "do not film". An unreadable or stale file
                // most often means the share is down, and guessing "open" would fill a video
                // with pictures of a shut roof.
                reason = $"roof unknown ({status.Detail})";
                return S.RecordWhenRoofUnknown;
        }
    }

    /// <summary>
    /// True when the roof gate permits frames right now, including the run-on after a closure.
    ///
    /// The roof file is written once the roof has finished moving, so by the time it reads
    /// CLOSED the interesting part is over. Filming on for a few minutes is what puts the
    /// closing roof in the video rather than cutting to black just before it.
    ///
    /// The run-on only follows a closure that interrupted filming. A roof that was already shut
    /// when we first looked never had anything to film, so it gets no grace period — otherwise
    /// arriving at a closed observatory would start a session and record five minutes of a shut
    /// roof, which is precisely the failure the gate exists to prevent.
    /// </summary>
    public bool RoofPermits(DateTime now, out string reason)
    {
        if (TrackRoof(now, out reason)) return true;
        if (_roofShutSince is not { } since) return false;

        var linger = TimeSpan.FromMinutes(Math.Max(0, S.RoofLingerMinutes));
        var left = linger - (now - since);
        if (left <= TimeSpan.Zero) return false;

        reason = $"{reason} — filming {Math.Ceiling(left.TotalMinutes)}m more";
        return true;
    }

    /// <summary>
    /// Raw roof permission, keeping the closure clock up to date as it goes. Called on every
    /// decision whether or not a session is running, so that when one does start the gate
    /// already knows whether the roof was open a moment ago — that is what tells a closure
    /// worth filming apart from a roof that was shut all along.
    /// </summary>
    private bool TrackRoof(DateTime now, out string reason)
    {
        if (RoofOpenNow(out reason))
        {
            _roofShutSince = null;
            _roofWasOpen = true;
            return true;
        }

        if (_roofWasOpen && _roofShutSince is null) _roofShutSince = now;
        _roofWasOpen = false;
        return false;
    }

    private DateTime? _roofShutSince;
    private bool _roofWasOpen;

    /// <summary>
    /// Run-on remaining after a closure, or null if none is in progress. Pure — the display
    /// path must not be able to advance the gate's state just by drawing itself.
    /// </summary>
    public TimeSpan? RunOnLeft(DateTime now)
    {
        if (_roofShutSince is not { } since) return null;
        var left = TimeSpan.FromMinutes(Math.Max(0, S.RoofLingerMinutes)) - (now - since);
        return left > TimeSpan.Zero ? left : null;
    }

    // ── decisions ─────────────────────────────────────────────────────────────

    public void MarkHandled(DateTime now) => _completedNight = NightKey(now);
    public void Rearm() => _completedNight = null;

    public SchedulerAction Evaluate(DateTime now, bool isRecording, bool isHeld)
    {
        if (S.Schedule == ScheduleMode.Manual)
        {
            // Even a hand-started session respects the roof, if the gate is on.
            if (!isRecording) return SchedulerAction.None;
            var allowed = RoofPermits(now, out _);
            if (!allowed && !isHeld) return SchedulerAction.Hold;
            if (allowed && isHeld) return SchedulerAction.Resume;
            return SchedulerAction.None;
        }

        var open = IsWindowOpen(now);

        if (!open)
        {
            if (isRecording) return SchedulerAction.Stop;
            if (_completedNight is not null && _completedNight != NightKey(now)) _completedNight = null;
            return SchedulerAction.None;
        }

        var roofOk = RoofPermits(now, out _);

        if (!isRecording)
        {
            if (_completedNight == NightKey(now)) return SchedulerAction.None;

            // This is the "started at 2am because the roof finally opened" case: the window has
            // been open for hours but nothing was recorded, and the moment the roof opens the
            // session begins.
            //
            // Deliberately the raw roof state rather than roofOk: the run-on extends a session
            // that is already filming and must never begin one, or a roof shutting while nothing
            // was recording would open a session purely to film the aftermath of it.
            return TrackRoof(now, out _) ? SchedulerAction.Start : SchedulerAction.None;
        }

        if (!roofOk && !isHeld) return SchedulerAction.Hold;
        if (roofOk && isHeld) return SchedulerAction.Resume;
        return SchedulerAction.None;
    }

    // ── display ───────────────────────────────────────────────────────────────

    public ScheduleView Describe(DateTime now, bool isRecording, bool isHeld)
    {
        var roofStatus = _roof();
        var roofText = !S.RequireRoofOpen
            ? "ROOF GATE OFF"
            : roofStatus.State switch
            {
                RoofState.Open => $"ROOF OPEN · {roofStatus.Detail.ToUpperInvariant()}",
                RoofState.Closed => $"ROOF CLOSED · {roofStatus.Detail.ToUpperInvariant()}",
                _ => $"ROOF UNKNOWN · {roofStatus.Detail.ToUpperInvariant()}"
            };

        // Say so while the run-on is going, or a still-recording session with a shut roof looks
        // like the gate has failed.
        if (isRecording && RunOnLeft(now) is { } runOn)
            roofText += $" · STILL FILMING, {Math.Ceiling(runOn.TotalMinutes)}M LEFT";

        if (S.Schedule == ScheduleMode.Manual)
            return new ScheduleView(false, null, null, "RECORDS ONLY WHEN YOU PRESS THE BUTTON.", roofText);

        var window = WindowFor(now);
        if (window is null)
            return new ScheduleView(false, null, null,
                "SET YOUR SITE LATITUDE AND LONGITUDE TO USE TWILIGHT TIMES.", roofText);

        var w = window.Value;
        var open = w.Contains(now);
        string summary;

        if (isHeld)
        {
            summary = $"HOLDING — WAITING FOR THE ROOF. DARK UNTIL {w.End:HH:mm}.";
        }
        else if (open && isRecording)
        {
            summary = $"RECORDING UNTIL {w.End:HH:mm} ({Humanise(w.End - now)} TO GO).";
        }
        else if (open)
        {
            summary = $"DARK NOW — UNTIL {w.End:HH:mm} ({Humanise(w.End - now)} LEFT).";
        }
        else
        {
            var next = NextStart(now);
            summary = next is { } n
                ? $"NEXT DARK {n:ddd HH:mm} ({Humanise(n - now)} FROM NOW), UNTIL {w.End:HH:mm}."
                : "NO DARK WINDOW FOUND FOR THIS SITE.";
        }

        var label = S.Schedule == ScheduleMode.Astronomical
            ? $"{S.Twilight.ToString().ToUpperInvariant()} TWILIGHT · {w.Start:HH:mm}–{w.End:HH:mm} · {Humanise(w.Length)}"
            : $"FIXED · {w.Start:HH:mm}–{w.End:HH:mm}";

        return new ScheduleView(open, w.Start, w.End, label + "\n" + summary, roofText);
    }

    private static string Humanise(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}M";
        return $"{(int)t.TotalHours}H {t.Minutes}M";
    }
}
