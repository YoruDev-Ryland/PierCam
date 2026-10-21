using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using PierCam.Models;

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
    private int _abandonMinutes = 1440;
    private RoofSource _source = RoofSource.File;
    private string? _alpacaUrl;
    private int _alpacaDevice;
    private string _alpacaName = string.Empty;
    private string? _ninaUrl;

    /// <summary>Raised after every poll that changed the state. Fires on a pool thread.</summary>
    public event Action<RoofStatus>? Changed;

    public RoofStatus Status { get { lock (_gate) return _status; } }

    /// <summary>Starts, restarts or (with nothing to read) stops watching a roof status file.</summary>
    /// <param name="abandonMinutes">Age past which the file is no longer believed at all.</param>
    public void Configure(string? path, int pollSeconds, int abandonMinutes) =>
        Configure(new SessionSettings
        {
            RoofSource = RoofSource.File, RoofStatusPath = path ?? string.Empty,
            RoofPollSeconds = pollSeconds, RoofAbandonMinutes = abandonMinutes,
        });

    /// <summary>Starts, restarts or stops watching, from whichever source the session names.</summary>
    public void Configure(SessionSettings s)
    {
        lock (_gate)
        {
            _source = s.RoofSource;
            _path = string.IsNullOrWhiteSpace(s.RoofStatusPath) ? null : s.RoofStatusPath;
            _alpacaUrl = string.IsNullOrWhiteSpace(s.AlpacaUrl) ? null : s.AlpacaUrl.Trim().TrimEnd('/');
            _alpacaDevice = s.AlpacaDevice;
            _alpacaName = s.AlpacaDeviceName;
            _ninaUrl = string.IsNullOrWhiteSpace(s.NinaSafetyUrl) ? null : s.NinaSafetyUrl.Trim().TrimEnd('/');
            _abandonMinutes = Math.Max(1, s.RoofAbandonMinutes);

            _timer?.Dispose();
            _timer = null;

            var configured = _source switch
            {
                RoofSource.Alpaca => _alpacaUrl is not null,
                RoofSource.Nina => _ninaUrl is not null,
                _ => _path is not null,
            };
            if (!configured)
            {
                _status = RoofStatus.Unconfigured;
                return;
            }

            var period = TimeSpan.FromSeconds(Math.Clamp(s.RoofPollSeconds, 5, 600));
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
        RoofSource source;
        string? path, alpacaUrl, ninaUrl;
        int abandonMinutes, device;
        string name;
        lock (_gate)
        {
            source = _source; path = _path; abandonMinutes = _abandonMinutes;
            alpacaUrl = _alpacaUrl; device = _alpacaDevice; name = _alpacaName; ninaUrl = _ninaUrl;
        }

        RoofStatus next;
        switch (source)
        {
            case RoofSource.Alpaca when alpacaUrl is not null:
                next = ReadAlpaca(alpacaUrl, device, name);
                break;
            case RoofSource.Nina when ninaUrl is not null:
                next = ReadNina(ninaUrl);
                break;
            case RoofSource.File when path is not null:
                next = Read(path, abandonMinutes);
                break;
            default:
                return;
        }

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

        // Only give up on the file when it is old enough that something is genuinely broken — and
        // only when it claims the roof is OPEN.
        //
        // A stale OPEN is the dangerous one: if the writer died while the roof actually shut,
        // believing it would put a night of closed roof in the video. A stale CLOSED reaches the
        // same decision whether it is believed or called Unknown — nothing films either way — so
        // believing it costs nothing and says something true. It also stops a perfectly ordinary
        // weathered-out spell, where the roof shut and simply never moved again, from being
        // reported as a fault.
        if (state == RoofState.Open && age > TimeSpan.FromMinutes(abandonMinutes))
            return new RoofStatus(RoofState.Unknown, reported, now,
                $"abandoned — no update for {FormatAge(age)}", age, IsQuiet: true);

        var quiet = age > QuietAfter;
        var detail = quiet
            ? $"{FormatAge(age)} since last change"
            : $"updated {FormatAge(age)} ago";

        return new RoofStatus(state, reported, now, detail, age, quiet);
    }

    // ── safety monitors ─────────────────────────────────────────────────────────────────
    //
    // A safety monitor answers one question, "is it safe?", and at an observatory like SFRO that
    // is the roof: safe means open. Both readers only ever ask; nothing is connected, changed or
    // commanded, on the Alpaca server or in N.I.N.A. A monitor that cannot be reached, or that
    // N.I.N.A. is not connected to, is Unknown - never Closed - so "record when status unknown"
    // decides, as it does for an unreadable file.

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Alpaca asks each client for a number to tell it apart; one per run of PierCam.</summary>
    private static readonly uint AlpacaClientId = (uint)Random.Shared.Next(1, int.MaxValue);
    private static int _alpacaTransaction;

    internal static RoofStatus ReadAlpaca(string url, int device, string name)
    {
        var now = DateTime.Now;
        var who = name.Length > 0 ? name : $"device {device}";
        try
        {
            var request = $"{url}/api/v1/safetymonitor/{device}/issafe" +
                          $"?ClientID={AlpacaClientId}&ClientTransactionID={(uint)Interlocked.Increment(ref _alpacaTransaction)}";
            using var response = Http.GetAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new RoofStatus(RoofState.Unknown, null, now, $"{who}: the server answered {(int)response.StatusCode}");
            using var doc = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = doc.RootElement;
            if (root.TryGetProperty("ErrorNumber", out var number) && number.GetInt32() != 0)
            {
                var message = root.TryGetProperty("ErrorMessage", out var m) ? m.GetString() : null;
                return new RoofStatus(RoofState.Unknown, null, now, $"{who}: error {number.GetInt32()} {message}".Trim());
            }
            var safe = root.GetProperty("Value").GetBoolean();
            return new RoofStatus(safe ? RoofState.Open : RoofState.Closed, now, now, $"{who} says {(safe ? "safe" : "unsafe")}");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException
                                   or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException
                                   or FormatException or UriFormatException)
        {
            return new RoofStatus(RoofState.Unknown, null, now, $"{who}: unreachable - {ex.Message}");
        }
    }

    internal static RoofStatus ReadNina(string url)
    {
        var now = DateTime.Now;
        try
        {
            using var response = Http.GetAsync($"{url}/v2/api/equipment/safetymonitor/info").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new RoofStatus(RoofState.Unknown, null, now, $"N.I.N.A. answered {(int)response.StatusCode}");
            using var doc = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = doc.RootElement;
            if (!root.TryGetProperty("Success", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("Error", out var e) ? e.GetString() : null;
                return new RoofStatus(RoofState.Unknown, null, now, $"N.I.N.A.: {(string.IsNullOrWhiteSpace(error) ? "no answer" : error)}");
            }
            var info = root.GetProperty("Response");
            var monitor = info.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (!info.TryGetProperty("Connected", out var connected) || !connected.GetBoolean())
                return new RoofStatus(RoofState.Unknown, null, now, "N.I.N.A. is not connected to a safety monitor");
            var safe = info.GetProperty("IsSafe").GetBoolean();
            return new RoofStatus(safe ? RoofState.Open : RoofState.Closed, now, now,
                $"N.I.N.A.'s {monitor ?? "safety monitor"} says {(safe ? "safe" : "unsafe")}");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException
                                   or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException
                                   or FormatException or UriFormatException)
        {
            return new RoofStatus(RoofState.Unknown, null, now, $"N.I.N.A. unreachable - {ex.Message}");
        }
    }

    /// <summary>The safety monitors an Alpaca server offers: (device number, name).</summary>
    internal static async System.Threading.Tasks.Task<List<(int number, string name)>> AlpacaSafetyMonitors(string url)
    {
        var list = new List<(int, string)>();
        using var response = await Http.GetAsync($"{url.Trim().TrimEnd('/')}/management/v1/configureddevices").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var d in doc.RootElement.GetProperty("Value").EnumerateArray())
            if (string.Equals(d.GetProperty("DeviceType").GetString(), "safetymonitor", StringComparison.OrdinalIgnoreCase))
                list.Add((d.GetProperty("DeviceNumber").GetInt32(), d.GetProperty("DeviceName").GetString() ?? ""));
        return list;
    }

    /// <summary>
    /// Alpaca safety monitors already set up through ASCOM Driver Connect on this PC, read from
    /// the ASCOM profile: (name, server URL, device number). Read only; nothing is changed.
    /// </summary>
    internal static List<(string name, string url, int device)> AscomAlpacaSafetyMonitors()
    {
        var found = new List<(string, string, int)>();
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\ASCOM\SafetyMonitor Drivers", @"SOFTWARE\ASCOM\SafetyMonitor Drivers" })
        {
            try
            {
                using var drivers = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                if (drivers is null) continue;
                foreach (var id in drivers.GetSubKeyNames())
                {
                    if (!id.StartsWith("ASCOM.AlpacaDynamic", StringComparison.OrdinalIgnoreCase)) continue;
                    using var key = drivers.OpenSubKey(id);
                    if (key?.GetValue("IP Address") is not string host || host.Length == 0) continue;
                    var scheme = (key.GetValue("Service Type") as string ?? "http").ToLowerInvariant();
                    var port = int.TryParse(key.GetValue("Port Number") as string, out var p) ? p : 0;
                    var device = int.TryParse(key.GetValue("Remote Device Number") as string, out var dn) ? dn : 0;
                    var defaultPort = scheme == "https" ? 443 : 80;
                    var url = port > 0 && port != defaultPort ? $"{scheme}://{host}:{port}" : $"{scheme}://{host}";
                    var name = key.GetValue(null) as string ?? id;
                    if (!found.Exists(f => f.Item2 == url && f.Item3 == device)) found.Add((name, url, device));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return found;
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
