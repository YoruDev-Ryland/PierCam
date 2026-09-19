using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PierCam.Sky;

/// <summary>Where the telescope was pointing at one moment, as N.I.N.A. reported it.</summary>
internal sealed record Pointing(
    DateTime ReceivedUtc, double RaHoursJNow, double DecDegJNow, bool Tracking, bool Parked, bool Slewing, string? TargetName);

/// <summary>
/// Reads the telescope's position from N.I.N.A.'s Advanced API plugin.
///
/// The plugin exposes actions as plain GET requests - /equipment/mount/park, /slew,
/// /sequence/start, /sequence/edit and others all *do* something when fetched. So the paths this
/// class can ever request are fixed here and nowhere else; the user configures the address, never
/// the path. Two, both read-only by their source:
///   /v2/api/equipment/mount/info            - small; polled every few seconds
///   /v2/api/image-history?imageType=LIGHT   - the latest light frame only, for the target name
/// (/sequence/state would give the target too, but is tens of megabytes per call.)
///
/// Runs on its own timer; the capture loop only ever reads <see cref="Latest"/>, so a slow or
/// absent N.I.N.A. can never hold up a frame.
/// </summary>
internal sealed class NinaPointing : IDisposable
{
    private const string MountInfoPath = "/v2/api/equipment/mount/info";
    private const string LatestLightPath = "/v2/api/image-history?imageType=LIGHT";

    private static readonly TimeSpan MountPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NamePeriod = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Func<string> _baseUrl;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private volatile Pointing? _latest;
    private volatile string _problem = "Not started";

    // The name is only trusted while the mount stays where it was when that frame was saved.
    private string? _name;
    private DateTime _nameImageTime;
    private (double ra, double dec)? _nameAnchor;

    public NinaPointing(Func<string> baseUrl) => _baseUrl = baseUrl;

    /// <summary>The newest position, or null. Consider it stale after <see cref="MaxAge"/>.</summary>
    public Pointing? Latest => _latest;
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    /// <summary>Why there is no usable position, for the status line; empty when all is well.</summary>
    public string Problem => _problem;

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Stops polling entirely - switched off means no requests at all.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _loop = null;
            _latest = null;
            _problem = string.Empty;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastName = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var root = (_baseUrl() ?? "").Trim().TrimEnd('/');
                if (!Uri.TryCreate(root, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
                {
                    SetProblem("The N.I.N.A. address is not a valid http address.");
                }
                else
                {
                    var mount = await GetJson(new Uri(baseUri, MountInfoPath), ct).ConfigureAwait(false);
                    var r = mount.GetProperty("Response");
                    var connected = r.TryGetProperty("Connected", out var c) && c.ValueKind == JsonValueKind.True;
                    if (!connected)
                    {
                        _latest = null;
                        SetProblem("N.I.N.A. is running, but no mount is connected.");
                    }
                    else
                    {
                        var ra = r.GetProperty("RightAscension").GetDouble();
                        var dec = r.GetProperty("Declination").GetDouble();
                        var tracking = r.TryGetProperty("TrackingEnabled", out var te) && te.ValueKind == JsonValueKind.True;
                        var parked = r.TryGetProperty("AtPark", out var ap) && ap.ValueKind == JsonValueKind.True;
                        var slewing = r.TryGetProperty("Slewing", out var sl) && sl.ValueKind == JsonValueKind.True;

                        if (DateTime.UtcNow - lastName > NamePeriod)
                        {
                            lastName = DateTime.UtcNow;
                            await RefreshName(baseUri, ra, dec, ct).ConfigureAwait(false);
                        }
                        // A slew of more than a degree means a new target: forget the old name.
                        if (_nameAnchor is { } a && Separation(a.ra, a.dec, ra, dec) > 1.0) { _name = null; _nameAnchor = null; }

                        _latest = new Pointing(DateTime.UtcNow, ra, dec, tracking, parked, slewing, _name);
                        SetProblem(string.Empty);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _latest = null;
                SetProblem("Can't reach N.I.N.A.'s Advanced API - is N.I.N.A. running with the plugin enabled?");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                _latest = null;
                SetProblem("N.I.N.A. answered, but not in a form PierCam understands.");
            }
            Changed?.Invoke();
            try { await Task.Delay(MountPeriod, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshName(Uri baseUri, double ra, double dec, CancellationToken ct)
    {
        try
        {
            var doc = await GetJson(new Uri(baseUri, LatestLightPath), ct).ConfigureAwait(false);
            if (!doc.TryGetProperty("Success", out var ok) || ok.ValueKind != JsonValueKind.True) return;
            var list = doc.GetProperty("Response");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return;
            var img = list[0];
            var name = img.TryGetProperty("TargetName", out var tn) ? tn.GetString() : null;
            var when = img.TryGetProperty("Date", out var dt) && dt.TryGetDateTime(out var d) ? d.ToUniversalTime() : DateTime.MinValue;
            // Only a frame from the last hour can describe what the mount is doing now; a new
            // frame re-anchors the name to wherever the mount is pointing at that moment.
            if (string.IsNullOrWhiteSpace(name) || DateTime.UtcNow - when > TimeSpan.FromHours(1)) return;
            // Upper-cased and trimmed once here, so drawing the label allocates nothing per frame.
            var label = name.Trim().ToUpperInvariant();
            if (label.Length > 32) label = label[..32];
            if (when != _nameImageTime) { _nameImageTime = when; _name = label; _nameAnchor = (ra, dec); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // The name is a nicety; the reticle does not depend on it.
        }
    }

    private async Task<JsonElement> GetJson(Uri uri, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private void SetProblem(string p) => _problem = p;

    /// <summary>Angular distance in degrees between two RA (hours) / Dec (degrees) positions.</summary>
    private static double Separation(double ra1h, double dec1, double ra2h, double dec2)
    {
        double a1 = ra1h * 15 * SkyMath.D2R, a2 = ra2h * 15 * SkyMath.D2R, d1 = dec1 * SkyMath.D2R, d2 = dec2 * SkyMath.D2R;
        var c = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(a1 - a2);
        return Math.Acos(Math.Clamp(c, -1, 1)) * SkyMath.R2D;
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate) { _cts?.Cancel(); loop = _loop; _cts = null; _loop = null; }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _http.Dispose();
    }
}
