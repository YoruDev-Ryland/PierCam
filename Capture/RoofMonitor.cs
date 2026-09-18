using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace PierCam.Capture;

internal enum RoofState
{
    /// <summary>Not configured, unreadable, or the file has gone stale.</summary>
    Unknown,
    Open,
    Closed
}

/// <summary>An immutable snapshot of what the roof file last said.</summary>
internal sealed record RoofStatus(
    RoofState State,
    DateTime? ReportedLocal,
    DateTime CheckedLocal,
    string Detail,
    TimeSpan? Age = null,
    bool IsQuiet = false)
{
    public static RoofStatus Unconfigured => new(RoofState.Unknown, null, DateTime.Now, "not configured");

    public bool IsOpen => State == RoofState.Open;
}

/// <summary>
/// Watches the observatory's roof status file.
///
/// At SFRO this is a text file on the site SMB share, one per building, rewritten about once a
/// minute:
///
///     2026-09-17 07:28:17PM CST Roof Status: OPEN
///
/// It lives on a network share, so every read happens on a background thread — a hung SMB call
/// must never reach the UI or the capture loop.
///
/// Age is deliberately NOT treated as "unknown". Observed at SFRO: all fourteen buildings were
/// stamped within one second of each other, and only the building whose roof actually moved
/// carried a later timestamp. The files are rewritten on a state change, not on a heartbeat, so
/// a roof that sits open all night legitimately has an hours-old file. Calling that Unknown
/// would refuse to record on exactly the nights worth recording.
///
/// What is still guarded is a file that cannot be read at all — the share going away — and a
/// file so old that something is clearly wrong, which is a much longer horizon than a missed
/// update.
/// </summary>
internal sealed class RoofMonitor : IDisposable
{
    private static readonly Regex StatusPattern = new(
        @"Roof\s*Status\s*:\s*(?<state>OPEN|CLOSED)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimePattern = new(
        @"^(?<stamp>\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}:\d{2}\s*(?:AM|PM))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Past this the file is only annotated as quiet; the state is still believed.</summary>
    private static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(45);

    private readonly object _gate = new();
    private Timer? _timer;
    private RoofStatus _status = RoofStatus.Unconfigured;

    private string? _path;
    private int _abandonMinutes = 720;

    /// <summary>Raised after every poll that changed the state. Fires on a pool thread.</summary>
    public event Action<RoofStatus>? Changed;

    public RoofStatus Status { get { lock (_gate) return _status; } }

    /// <summary>Starts, restarts or (with a null path) stops watching.</summary>
    /// <param name="abandonMinutes">Age past which the file is no longer believed at all.</param>
    public void Configure(string? path, int pollSeconds, int abandonMinutes)
    {
        lock (_gate)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            _abandonMinutes = Math.Max(1, abandonMinutes);

            _timer?.Dispose();
            _timer = null;

            if (_path is null)
            {
                _status = RoofStatus.Unconfigured;
                return;
            }

            var period = TimeSpan.FromSeconds(Math.Clamp(pollSeconds, 5, 600));
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, period);
        }
    }

    /// <summary>Reads the file once, out of band from the scheduled poll.</summary>
    public RoofStatus PollNow()
    {
        Poll();
        return Status;
    }

    private void Poll()
    {
        string? path;
        int abandonMinutes;
        lock (_gate) { path = _path; abandonMinutes = _abandonMinutes; }
        if (path is null) return;

        var next = Read(path, abandonMinutes);

        bool changed;
        lock (_gate)
        {
            changed = _status.State != next.State || _status.Detail != next.Detail;
            _status = next;
        }
        if (changed) Changed?.Invoke(next);
    }

    private static RoofStatus Read(string path, int abandonMinutes)
    {
        var now = DateTime.Now;
        string text;
        DateTime writtenLocal;

        try
        {
            // Share the handle: the site's writer rewrites this file continuously, and an
            // exclusive open would fail intermittently for no good reason.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
            writtenLocal = File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            return new RoofStatus(RoofState.Unknown, null, now, $"unreadable — {ex.Message}");
        }

        var match = StatusPattern.Match(text);
        if (!match.Success)
            return new RoofStatus(RoofState.Unknown, null, now, "no status line in file");

        var state = match.Groups["state"].Value.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
            ? RoofState.Open
            : RoofState.Closed;

        // The embedded stamp carries a zone label that is not always right (it reads CST even
        // in daylight saving), so the label is ignored and the wall-clock part is read as local.
        DateTime? reported = null;
        var tm = TimePattern.Match(text.Trim());
        if (tm.Success && DateTime.TryParseExact(
                Regex.Replace(tm.Groups["stamp"].Value, @"\s+", " ").Trim(),
                new[] { "yyyy-MM-dd hh:mm:sstt", "yyyy-MM-dd h:mm:sstt", "yyyy-MM-dd hh:mm:ss tt", "yyyy-MM-dd h:mm:ss tt" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            reported = parsed;
        }

        // Trust whichever clock is more recent; the share's timestamps and the embedded stamp
        // can disagree, and only one of them needs to be alive.
        var freshest = reported is { } r && r > writtenLocal ? r : writtenLocal;
        var age = now - freshest;

        // Only give up on the file when it is old enough that something is genuinely broken.
        if (age > TimeSpan.FromMinutes(abandonMinutes))
            return new RoofStatus(RoofState.Unknown, reported, now,
                $"abandoned — no update for {FormatAge(age)}", age, IsQuiet: true);

        var quiet = age > QuietAfter;
        var detail = quiet
            ? $"{FormatAge(age)} since last change"
            : $"updated {FormatAge(age)} ago";

        return new RoofStatus(state, reported, now, detail, age, quiet);
    }

    private static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 90 => $"{Math.Max(0, (int)age.TotalSeconds)}s",
        < 5400 => $"{(int)age.TotalMinutes}m",
        _ => $"{(int)age.TotalHours}h"
    };

    /// <summary>The usual SFRO layout, given a building number.</summary>
    public static string SfroPathFor(string shareRoot, int building) =>
        Path.Combine(shareRoot, "roof", $"building-{building}", "RoofStatusFile.txt");

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
