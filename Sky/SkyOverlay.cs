using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Models;
using PierCam.Video;

namespace PierCam.Sky;

internal enum SkyKind { Constellation, Star, Galaxy, Cluster, Nebula, Planet, Moon, Direction }

/// <summary>
/// Constellation figures, constellation names, named stars and the Messier objects, from the
/// embedded sky-objects.csv (26 KB, derived from d3-celestial). Parsed once, on first use.
/// </summary>
internal sealed class SkyCatalogue
{
    public sealed record Figure(int Rank, (double ra, double dec)[] Points);
    public sealed record Entry(string Id, string Label, SkyKind Kind, double Ra, double Dec, double Mag, int Rank, double SizeArcmin);

    public List<Figure> Figures { get; } = new();
    public List<Entry> Entries { get; } = new();

    private static readonly Lazy<SkyCatalogue> Shared = new(Load);
    public static SkyCatalogue Instance => Shared.Value;

    private static SkyCatalogue Load()
    {
        var cat = new SkyCatalogue();
        using var stream = typeof(SkyCatalogue).Assembly.GetManifestResourceStream("PierCam.Sky.sky-objects.csv")
                           ?? throw new InvalidOperationException("sky-object catalogue missing from the build");
        using var reader = new StreamReader(stream);
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 2 || line[0] == '#') continue;
            var p = line.Split(',');
            switch (p[0])
            {
                case "L":
                    var pts = new (double, double)[(p.Length - 3) / 2];
                    for (var i = 0; i < pts.Length; i++) pts[i] = (D(p[3 + 2 * i]), D(p[4 + 2 * i]));
                    cat.Figures.Add(new Figure(int.Parse(p[2], CultureInfo.InvariantCulture), pts));
                    break;
                case "C":
                    cat.Entries.Add(new Entry("C:" + p[1], p[2], SkyKind.Constellation, D(p[4]), D(p[5]), 0,
                        int.Parse(p[3], CultureInfo.InvariantCulture), 0));
                    break;
                case "S":
                    cat.Entries.Add(new Entry("S:" + p[1], p[1], SkyKind.Star, D(p[2]), D(p[3]), D(p[4]), 0, 0));
                    break;
                case "D":
                    var kind = p[3] switch
                    {
                        "e" or "s" or "i" => SkyKind.Galaxy,
                        "gc" or "oc" or "pos" => SkyKind.Cluster,
                        _ => SkyKind.Nebula,
                    };
                    var label = p[2].Length > 0 ? $"{p[1]} {p[2]}" : p[1];
                    cat.Entries.Add(new Entry("D:" + p[1], label, kind, D(p[5]), D(p[6]), D(p[4]), 0, D(p[7])));
                    break;
            }
        }
        return cat;
    }
}

/// <summary>
/// The overlay as a ready-to-blend list of pixels: built once every few seconds on a background
/// thread, blended into every frame. Blending is a single pass over the overlay's own pixels -
/// tens of thousands, not the frame's millions - so drawing it costs almost nothing per frame.
/// </summary>
internal sealed class OverlayRaster
{
    public int Width { get; }
    public int Height { get; }
    private int[] _idx = new int[4096];
    private uint[] _col = new uint[4096];
    private int _n;

    public OverlayRaster(int width, int height) { Width = width; Height = height; }

    public int Count => _n;

    public void Add(int x, int y, byte r, byte g, byte b, double alpha)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || alpha <= 0.02) return;
        if (_n == _idx.Length)
        {
            Array.Resize(ref _idx, _n * 2);
            Array.Resize(ref _col, _n * 2);
        }
        _idx[_n] = y * Width + x;
        _col[_n] = r | (uint)g << 8 | (uint)b << 16 | (uint)Math.Clamp((int)(alpha * 255 + 0.5), 0, 255) << 24;
        _n++;
    }

    public void Blend(byte[] rgb)
    {
        for (var i = 0; i < _n; i++)
        {
            var o = _idx[i] * 3;
            var c = _col[i];
            int a = (int)(c >> 24), r = (int)(c & 255), g = (int)(c >> 8 & 255), b = (int)(c >> 16 & 255);
            rgb[o] = (byte)(rgb[o] + ((r - rgb[o]) * a >> 8));
            rgb[o + 1] = (byte)(rgb[o + 1] + ((g - rgb[o + 1]) * a >> 8));
            rgb[o + 2] = (byte)(rgb[o + 2] + ((b - rgb[o + 2]) * a >> 8));
        }
    }

    // ── primitives, all anti-aliased by distance ──

    public void Segment(double ax, double ay, double bx, double by, double stroke, byte r, byte g, byte b, double alpha)
    {
        int x0 = (int)(Math.Min(ax, bx) - stroke - 1), x1 = (int)(Math.Max(ax, bx) + stroke + 1);
        int y0 = (int)(Math.Min(ay, by) - stroke - 1), y1 = (int)(Math.Max(ay, by) + stroke + 1);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
        double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var t = len2 == 0 ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
            double dx = x - (ax + t * vx), dy = y - (ay + t * vy);
            var cover = Math.Clamp(stroke / 2 + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
            if (cover > 0) Add(x, y, r, g, b, cover * alpha);
        }
    }

    /// <summary>An ellipse outline (rx = ry for a circle), optionally dashed into <paramref name="dashes"/> arcs.</summary>
    public void Ellipse(double cx, double cy, double rx, double ry, double stroke, byte r, byte g, byte b, double alpha, int dashes = 0)
    {
        var steps = Math.Max(16, (int)(Math.Max(rx, ry) * 0.8));
        double px = cx + rx, py = cy;
        for (var i = 1; i <= steps; i++)
        {
            var a = 2 * Math.PI * i / steps;
            double qx = cx + rx * Math.Cos(a), qy = cy + ry * Math.Sin(a);
            if (dashes == 0 || (int)(i * 2.0 * dashes / steps) % 2 == 0)
                Segment(px, py, qx, qy, stroke, r, g, b, alpha);
            px = qx; py = qy;
        }
    }

    public void Text(string text, int x, int y, int scale, byte r, byte g, byte b, double alpha)
    {
        // A dark shadow first, so the label reads over bright sky and pale equipment alike.
        var off = Math.Max(1, scale / 2);
        TextOverlay.ForEachPixel(text, x + off, y + off, scale, (px, py) => Add(px, py, 0, 0, 0, 0.6 * alpha));
        TextOverlay.ForEachPixel(text, x, y, scale, (px, py) => Add(px, py, r, g, b, alpha));
    }
}

/// <summary>
/// Labels the sky on the pier camera's frame: constellation figures and names, named stars, deep-
/// sky objects, the planets and the Moon, and the compass directions - all from the target
/// marker's calibration, so nothing here is detected or solved; every object is simply projected.
///
/// Stability is the point. What is shown depends only on fixed rules (the type is on, the object
/// is within the density setting, it is above the horizon and inside the frame), never on a
/// ranking redone each frame, so nothing swaps in and out. An object enters with a margin and
/// leaves with another, so one riding the frame's edge does not blink; a label keeps the side it
/// was given while that side stays clear. The sky turns a quarter of a degree a minute, a few
/// pixels, so the overlay is rebuilt every few seconds in the background and blended into each
/// frame in between.
/// </summary>
internal sealed class SkyOverlayService : IDisposable
{
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly Func<int, int, LensModel?> _lens;
    private readonly LowPriorityScheduler _scheduler = new(1);

    private OverlayRaster? _raster;
    private DateTime _builtUtc;
    private LensModel? _builtLens;
    private int _builtVersion = -1;
    private int _version;
    private int _building;

    // What the last build showed, and where each label went: the memory that keeps it steady.
    private readonly Dictionary<string, int> _labelSide = new();
    private readonly HashSet<string> _shown = new();

    private readonly Func<bool?> _roofOpen;

    /// <param name="roofOpen">What a roof file says: open, closed, or null when there is none or it is unreadable.</param>
    public SkyOverlayService(AppSettings settings, Func<int, int, LensModel?> lens, Func<bool?>? roofOpen = null)
    {
        _settings = settings;
        _lens = lens;
        _roofOpen = roofOpen ?? (() => null);
        _area = SnapshotArea();
    }

    // ── is the night sky actually in view? ──
    //
    // Labels over a closed roof, a cloud deck or a daylight sky mark nothing. Three checks, the
    // cheapest first: a roof file that says closed; the Sun above civil twilight; and - the one
    // that needs nothing but the camera - whether the bright stars are where the calibration puts
    // them. That last check works at any site and also catches cloud, dew and a lens cap. The
    // answer only changes after two checks agree, a few seconds apart, so a passing cloud or an
    // odd frame cannot make the overlay blink.

    private bool _skyVisible;
    private int _agreeing;
    private DateTime _checkedUtc;

    /// <summary>Why the overlay is hidden, for the settings panel; empty while it shows.</summary>
    public string HiddenReason { get; private set; } = "WAITING FOR THE FIRST LOOK AT THE SKY.";

    private void CheckSky(byte[] rgb, int w, int h, LensModel lens, DateTime utc, double circle, (double x, double y)[][] area)
    {
        _checkedUtc = utc;
        bool? now;
        string reason;
        if (_roofOpen() == false) { now = false; reason = "HIDDEN: THE ROOF IS CLOSED."; }
        else if (PierCam.Capture.SunCalculator.Altitude(utc, _settings.Site.Latitude, _settings.Site.Longitude) > -6)
        { now = false; reason = "HIDDEN: TOO LIGHT FOR STARS."; }
        else
        {
            var (seen, expected) = StarsInView(rgb, w, h, lens, utc, _settings.Site.Latitude, _settings.Site.Longitude, circle, area);
            // Too few bright stars inside the view to judge by - a small sky area, or a part of the
            // sky with none. Better shown than hidden for good.
            if (expected < 6) { now = true; reason = ""; }
            else if (seen >= expected * 0.4) { now = true; reason = ""; }
            else if (seen <= expected * 0.15) { now = false; reason = $"HIDDEN: NO STARS IN VIEW ({seen} OF {expected}) - CLOUD, OR THE ROOF IS SHUT."; }
            else { now = null; reason = HiddenReason; }
        }

        // A roof that says closed, or daylight, is believed at once; the stars need a second look.
        var certain = reason.StartsWith("HIDDEN: THE ROOF") || reason.StartsWith("HIDDEN: TOO LIGHT");
        if (now is { } v && v != _skyVisible)
        {
            if (certain || ++_agreeing >= 2) { _skyVisible = v; _agreeing = 0; }
        }
        else _agreeing = 0;
        HiddenReason = _skyVisible ? "" : reason.Length > 0 ? reason : HiddenReason;
    }

    private static readonly Lazy<List<CatalogueStar>> CheckStars =
        new(() => BrightStars.Load().FindAll(s => s.Mag <= 2.5));

    /// <summary>
    /// Of the bright stars the calibration puts inside the visible sky, how many show as a point of
    /// light above their surroundings. About a hundred small windows - nothing next to a frame.
    /// </summary>
    internal static (int seen, int expected) StarsInView(byte[] rgb, int w, int h, LensModel lens, DateTime utc,
        double lat, double lon, double circle, (double x, double y)[][]? area)
    {
        int seen = 0, expected = 0;
        var r = Math.Max(2, (int)Math.Round(0.0012 * w));
        var ring = new List<int>(32);
        foreach (var s in CheckStars.Value)
        {
            var (ra, dec) = SkyMath.PrecessFromJ2000(s.RaJ2000, s.DecJ2000, utc);
            var (alt, az) = SkyMath.ToAltAz(ra, dec, utc, lat, lon);
            if (alt < 12) continue;                                   // low stars drown in haze
            if (!lens.TryProject(alt + SkyMath.Refraction(alt), az, out var x, out var y)) continue;
            var margin = 8 * r;
            if (x < margin || y < margin || x >= w - margin || y >= h - margin) continue;
            if (!InArea(area, x, y)) continue;
            if (!double.IsPositiveInfinity(circle) && (x - lens.Cx) * (x - lens.Cx) + (y - lens.Cy) * (y - lens.Cy) > (circle - margin) * (circle - margin)) continue;
            expected++;

            int cx = (int)x, cy = (int)y, peak = 0;
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var o = ((cy + dy) * w + cx + dx) * 3;
                peak = Math.Max(peak, rgb[o] + rgb[o + 1] + rgb[o + 2]);
            }
            ring.Clear();
            for (var a = 0; a < 24; a++)
            {
                var th = a * Math.PI / 12;
                int bx = cx + (int)(4 * r * Math.Cos(th)), by = cy + (int)(4 * r * Math.Sin(th));
                var o = (by * w + bx) * 3;
                ring.Add(rgb[o] + rgb[o + 1] + rgb[o + 2]);
            }
            ring.Sort();
            if ((peak - ring[ring.Count / 2]) / 3.0 >= 18) seen++;
        }
        return (seen, expected);
    }

    /// <summary>
    /// Call on the UI thread when any overlay setting changes, the sky area or the frame's turn
    /// included; the next frame rebuilds it.
    /// </summary>
    public void Invalidate()
    {
        _area = SnapshotArea();
        Interlocked.Increment(ref _version);
    }

    /// <summary>The sky area as it stood at the last change: a rebuild never reads a list being edited.</summary>
    private sealed record AreaSnapshot((double x, double y)[][] Polygons, PierCam.Imaging.FrameOrientation Orientation);
    private volatile AreaSnapshot _area;

    private AreaSnapshot SnapshotArea() => new(
        _settings.SkyOverlay.SkyArea.Where(r => r.Points.Count >= 3)
            .Select(r => r.Points.Select(pt => (pt.X, pt.Y)).ToArray()).ToArray(),
        _settings.Camera.Orientation);

    /// <summary>The sky area in a frame's own pixels, turned the way the frame is.</summary>
    private static (double x, double y)[][] AreaIn(AreaSnapshot a, int w, int h) =>
        a.Polygons.Select(poly => poly.Select(pt =>
        {
            var (ux, uy) = a.Orientation.MapUnit(pt.x, pt.y);
            return (ux * w, uy * h);
        }).ToArray()).ToArray();

    public bool Enabled => _settings.SkyOverlay.Enabled;
    public bool BurnIntoRecordings => _settings.SkyOverlay is { Enabled: true, BurnIntoRecordings: true };

    /// <summary>Blends the overlay into a frame, if there is one ready. Returns whether it drew.</summary>
    public bool Draw(byte[] rgb, int w, int h)
    {
        if (!Enabled) return false;
        var lens = _lens(w, h);
        if (lens is null) return false;

        var raster = _raster;
        var now = DateTime.UtcNow;
        var stale = raster is null || raster.Width != w || raster.Height != h || !ReferenceEquals(lens, _builtLens)
                    || _builtVersion != Volatile.Read(ref _version) || now - _builtUtc > RebuildEvery;
        // Hidden, nothing is built, so "stale" would be true every frame: look again on the same
        // few-second rhythm instead.
        if (!_skyVisible && now - _checkedUtc < RebuildEvery) stale = false;
        if (stale && Interlocked.CompareExchange(ref _building, 1, 0) == 0)
        {
            var version = Volatile.Read(ref _version);
            var circle = ImageCircle(EstimateImageRadius(rgb, w, h, lens.Cx, lens.Cy));
            try { CheckSky(rgb, w, h, lens, now, circle, AreaIn(_area, w, h)); }
            catch (Exception ex) { App.Log(ex, "Sky overlay check"); }
            if (_skyVisible)
                Task.Factory.StartNew(() => Rebuild(lens, w, h, now, version, circle), CancellationToken.None, TaskCreationOptions.None, _scheduler);
            else Volatile.Write(ref _building, 0);      // hidden: nothing to build
        }

        if (!_skyVisible) return false;
        if (raster is null || raster.Width != w || raster.Height != h) return false;
        raster.Blend(rgb);
        return raster.Count > 0;
    }

    // The last few image-circle measurements; their median is used, so one frame with a
    // telescope across the sky, or a flash of light, cannot move the edge.
    private readonly double[] _circles = new double[5];
    private int _circleCount;

    private double ImageCircle(double latest)
    {
        _circles[_circleCount++ % _circles.Length] = latest;
        var n = Math.Min(_circleCount, _circles.Length);
        var sorted = _circles[..n];
        Array.Sort(sorted);
        return sorted[n / 2];
    }

    private void Rebuild(LensModel lens, int w, int h, DateTime utc, int version, double circle)
    {
        try
        {
            if (!ReferenceEquals(lens, _builtLens) || _builtVersion != version) { _shown.Clear(); _labelSide.Clear(); }
            var raster = Build(lens, w, h, utc, _settings.SkyOverlay, _settings.Site.Latitude, _settings.Site.Longitude, _shown, _labelSide, circle,
                AreaIn(_area, w, h));
            _raster = raster;
            _builtUtc = utc;
            _builtLens = lens;
            _builtVersion = version;
        }
        catch (Exception ex) { App.Log(ex, "Sky overlay"); }
        finally { Volatile.Write(ref _building, 0); }
    }

    // ───────────────────────────── building one overlay ─────────────────────────────

    private sealed record Item(string Id, string Label, SkyKind Kind, double X, double Y, double Radius, int Priority);

    private static readonly int[] RankFor = { 1, 1, 2, 2, 3 };
    private static readonly double[] StarMag = { 1.0, 1.5, 2.0, 2.3, 2.6 };
    private static readonly double[] DeepMag = { 5.0, 6.0, 7.5, 9.0, 99 };

    private static (byte r, byte g, byte b) Colour(SkyKind k) => k switch
    {
        SkyKind.Constellation => (150, 180, 225),
        SkyKind.Star => (225, 225, 232),
        SkyKind.Galaxy => (235, 150, 150),
        SkyKind.Cluster => (240, 215, 130),
        SkyKind.Nebula => (140, 215, 170),
        SkyKind.Planet => (255, 205, 110),
        SkyKind.Moon => (235, 235, 205),
        _ => (255, 255, 255),
    };

    /// <param name="circle">Radius of the lens's image circle in pixels, from its centre; infinity when the sky fills the frame.</param>
    /// <param name="area">The hand-drawn sky area in frame pixels; null or empty for the whole picture.</param>
    internal static OverlayRaster Build(LensModel lens, int w, int h, DateTime utc, SkyOverlaySettings s,
        double lat, double lon, HashSet<string> shown, Dictionary<string, int> labelSide, double circle = double.PositiveInfinity,
        (double x, double y)[][]? area = null)
    {
        var raster = new OverlayRaster(w, h);
        var cat = SkyCatalogue.Instance;
        var level = Math.Clamp(s.Density, 1, 5) - 1;
        var k = Math.Max(0.5, Math.Min(w, h) / 1080.0);
        var stroke = Math.Max(1.1, 1.3 * k);
        var pxPerDeg = lens.F * Math.PI / 180;

        bool Project(double ra, double dec, out double x, out double y, out double alt)
        {
            var (rd, dd) = SkyMath.PrecessFromJ2000(ra, dec, utc);
            (alt, var az) = SkyMath.ToAltAz(rd, dd, utc, lat, lon);
            x = y = 0;
            if (alt < -1) return false;
            return lens.TryProject(alt + SkyMath.Refraction(Math.Max(alt, 0)), az, out x, out y);
        }
        // Inside the frame, and inside the part of it the lens actually images: past the image
        // circle the projection still has answers, but the frame there is black.
        bool Inside(double x, double y, double margin)
        {
            if (x < margin || y < margin || x >= w - margin || y >= h - margin) return false;
            if (!InArea(area, x, y)) return false;
            if (double.IsPositiveInfinity(circle)) return true;
            double dx = x - lens.Cx, dy = y - lens.Cy, r = circle - margin;
            return r > 0 && dx * dx + dy * dy <= r * r;
        }

        // 1. Constellation figures, as curves: each stick is cut into short steps along the great
        // circle and every step projected, because through a fisheye a straight line on the sky
        // is a curve on the frame. A small gap at each star leaves the star itself visible.
        if (s.ConstellationLines)
        {
            var (lr, lg, lb) = (115, 155, 210);
            foreach (var fig in cat.Figures)
            {
                if (fig.Rank > RankFor[level]) continue;
                for (var i = 1; i < fig.Points.Length; i++)
                    Stick(fig.Points[i - 1], fig.Points[i]);
            }

            void Stick((double ra, double dec) a, (double ra, double dec) b)
            {
                var va = Unit(a.ra, a.dec); var vb = Unit(b.ra, b.dec);
                var ang = Math.Acos(Math.Clamp(va.x * vb.x + va.y * vb.y + va.z * vb.z, -1, 1)) * 180 / Math.PI;
                const double gap = 0.7;
                if (ang <= 2 * gap) return;
                var steps = Math.Max(1, (int)Math.Ceiling((ang - 2 * gap) / 0.75));
                double px = 0, py = 0; var have = false;
                for (var j = 0; j <= steps; j++)
                {
                    var t = (gap + (ang - 2 * gap) * j / steps) / ang;
                    var (ra, dec) = Slerp(va, vb, ang * Math.PI / 180, t);
                    if (!Project(ra, dec, out var x, out var y, out var alt) || alt < 0) { have = false; continue; }
                    if (!Inside(x, y, 0)) { have = false; continue; }
                    if (have && Math.Abs(x - px) + Math.Abs(y - py) < 0.2 * w)
                        raster.Segment(px, py, x, y, stroke, (byte)lr, (byte)lg, (byte)lb, 0.55);
                    px = x; py = y; have = true;
                }
            }
        }

        // 2. Everything with a label, under the visibility rules.
        var items = new List<Item>();
        var nowShown = new HashSet<string>();
        void Consider(string id, string label, SkyKind kind, double x, double y, double alt, double radius, int priority)
        {
            // In: clear of the horizon and well inside the frame. Out: below it, or past the edge.
            var was = shown.Contains(id);
            var visible = was ? alt > -0.5 && Inside(x, y, -0.01 * w) : alt > 1.5 && Inside(x, y, 0.02 * w);
            if (!visible) return;
            nowShown.Add(id);
            items.Add(new Item(id, label, kind, x, y, radius, priority));
        }

        foreach (var e in cat.Entries)
        {
            var on = e.Kind switch
            {
                SkyKind.Constellation => s.ConstellationNames && e.Rank <= RankFor[level],
                SkyKind.Star => s.StarNames && e.Mag <= StarMag[level],
                SkyKind.Galaxy => s.Galaxies && e.Mag <= DeepMag[level],
                SkyKind.Cluster => s.Clusters && e.Mag <= DeepMag[level],
                SkyKind.Nebula => s.Nebulae && e.Mag <= DeepMag[level],
                _ => false,
            };
            if (!on || !Project(e.Ra, e.Dec, out var x, out var y, out var alt)) continue;
            var radius = e.Kind is SkyKind.Galaxy or SkyKind.Cluster or SkyKind.Nebula
                ? Math.Clamp(e.SizeArcmin / 120 * pxPerDeg, 6 * k, 40 * k)
                : e.Kind == SkyKind.Star ? 6 * k : 0;
            var priority = e.Kind switch
            {
                SkyKind.Constellation => 20 + e.Rank * 10,
                SkyKind.Star => 10 + (int)(e.Mag * 10),
                _ => 60 + (int)Math.Min(e.Mag * 5, 100),
            };
            Consider(e.Id, e.Label, e.Kind, x, y, alt, radius, priority);
        }

        if (s.Planets)
        {
            var bodies = level >= 4 ? 7 : level >= 3 ? 6 : 5;
            for (var i = 0; i < bodies; i++)
            {
                var body = (SolarSystem.Body)i;
                var (ra, dec) = SolarSystem.Planet(body, utc);
                if (Project(ra, dec, out var x, out var y, out var alt))
                    Consider("P:" + body, body.ToString().ToUpperInvariant(), SkyKind.Planet, x, y, alt, 7 * k, 0);
            }
            var (mAlt, mAz) = SolarSystem.MoonAltAz(utc, lat, lon);
            if (mAlt > -1 && lens.TryProject(mAlt + SkyMath.Refraction(Math.Max(mAlt, 0)), mAz, out var mx, out var my))
                Consider("P:Moon", "MOON", SkyKind.Moon, mx, my, mAlt, Math.Max(9 * k, 0.26 * pxPerDeg * 1.3), 0);
        }

        if (s.Directions)
        {
            // As low as the frame shows in that direction: the horizon where it is in view,
            // otherwise the rim of the image, climbing towards the zenith until it is inside.
            foreach (var (name, az) in new[] { ("N", 0.0), ("E", 90.0), ("S", 180.0), ("W", 270.0) })
                for (var alt = 4.0; alt < 89; alt += 1)
                    if (lens.TryProject(alt, az, out var x, out var y) && Inside(x, y, 0.035 * w))
                    {
                        Consider("N:" + name, name, SkyKind.Direction, x, y, 4, 0, 5);
                        break;
                    }
        }

        shown.Clear();
        shown.UnionWith(nowShown);

        // 3. Symbols, then labels. Labels are placed in priority order; each keeps the side it had
        // last time while that side is clear, and otherwise takes the first clear one.
        foreach (var it in items) Symbol(raster, it, stroke, k);

        var placed = new List<(int x, int y, int w, int h)>();
        var scale = Math.Max(1, (int)Math.Round(1.6 * k));
        items.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        foreach (var it in items)
        {
            var big = it.Kind is SkyKind.Constellation or SkyKind.Direction;
            var sc = it.Kind == SkyKind.Direction ? scale + 1 : scale;
            var (tw, th) = TextOverlay.Measure(it.Label, sc);
            var gap = it.Radius + 6 * k;
            (int x, int y)[] sides = big
                ? new[] { ((int)(it.X - tw / 2.0), (int)(it.Y - th / 2.0)), ((int)(it.X - tw / 2.0), (int)(it.Y - th * 2.0)), ((int)(it.X - tw / 2.0), (int)(it.Y + th)) }
                : new[]
                {
                    ((int)(it.X + gap), (int)(it.Y - th / 2.0)), ((int)(it.X - gap - tw), (int)(it.Y - th / 2.0)),
                    ((int)(it.X - tw / 2.0), (int)(it.Y - gap - th)), ((int)(it.X - tw / 2.0), (int)(it.Y + gap)),
                };
            var order = new List<int>();
            if (labelSide.TryGetValue(it.Id, out var last) && last < sides.Length) order.Add(last);
            for (var i = 0; i < sides.Length; i++) if (!order.Contains(i)) order.Add(i);

            var pad = sc * 2;
            var chosen = -1;
            foreach (var i in order)
            {
                var (lx, ly) = sides[i];
                if (lx < 4 || ly < 4 || lx + tw > w - 4 || ly + th > h - 4) continue;
                var clear = true;
                foreach (var p in placed)
                    if (lx - pad < p.x + p.w && p.x < lx + tw + pad && ly - pad < p.y + p.h && p.y < ly + th + pad) { clear = false; break; }
                if (clear) { chosen = i; break; }
            }
            if (chosen < 0) { labelSide.Remove(it.Id); continue; }
            labelSide[it.Id] = chosen;
            var (cx, cy) = sides[chosen];
            placed.Add((cx, cy, tw, th));
            var (r, g, b) = Colour(it.Kind);
            raster.Text(it.Label, cx, cy, sc, r, g, b, it.Kind == SkyKind.Constellation ? 0.8 : 0.95);
        }
        return raster;
    }

    private static void Symbol(OverlayRaster raster, Item it, double stroke, double k)
    {
        var (r, g, b) = Colour(it.Kind);
        switch (it.Kind)
        {
            case SkyKind.Galaxy:
                raster.Ellipse(it.X, it.Y, it.Radius, it.Radius * 0.55, stroke, r, g, b, 0.85);
                break;
            case SkyKind.Cluster:
                raster.Ellipse(it.X, it.Y, it.Radius, it.Radius, stroke, r, g, b, 0.85, dashes: 8);
                break;
            case SkyKind.Nebula:
                var d = it.Radius;
                raster.Segment(it.X - d, it.Y - d, it.X + d, it.Y - d, stroke, r, g, b, 0.85);
                raster.Segment(it.X + d, it.Y - d, it.X + d, it.Y + d, stroke, r, g, b, 0.85);
                raster.Segment(it.X + d, it.Y + d, it.X - d, it.Y + d, stroke, r, g, b, 0.85);
                raster.Segment(it.X - d, it.Y + d, it.X - d, it.Y - d, stroke, r, g, b, 0.85);
                break;
            case SkyKind.Planet:
            case SkyKind.Moon:
                raster.Ellipse(it.X, it.Y, it.Radius, it.Radius, stroke * 1.3, r, g, b, 0.9);
                break;
            case SkyKind.Star:
                // A small, faint ring: enough to say which star, without hiding it.
                raster.Ellipse(it.X, it.Y, it.Radius, it.Radius, stroke * 0.8, r, g, b, 0.5);
                break;
        }
    }

    /// <summary>
    /// How far the lens images from its centre, measured on a frame: at every radius inside the
    /// image circle some direction shows sky, and outside it nothing is ever lit. So the 90th
    /// percentile of brightness around each ring stays up inside the circle and drops to black at
    /// its edge. 200 rings of 180 samples - a few tens of thousands of reads, every few seconds.
    /// Infinity when there is no edge to find: a sky that fills the frame.
    /// </summary>
    internal static double EstimateImageRadius(byte[] rgb, int w, int h, double cx, double cy)
    {
        const int bins = 200, angles = 180;
        var maxR = Math.Max(Math.Max(Math.Sqrt(cx * cx + cy * cy), Math.Sqrt((w - cx) * (w - cx) + cy * cy)),
            Math.Max(Math.Sqrt(cx * cx + (h - cy) * (h - cy)), Math.Sqrt((w - cx) * (w - cx) + (h - cy) * (h - cy))));
        var step = maxR / bins;
        var level = new double[bins];
        var ring = new List<int>(angles);
        for (var i = 0; i < bins; i++)
        {
            ring.Clear();
            var r = (i + 0.5) * step;
            for (var a = 0; a < angles; a++)
            {
                var th = a * 2 * Math.PI / angles;
                int x = (int)(cx + r * Math.Cos(th)), y = (int)(cy + r * Math.Sin(th));
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                var o = (y * w + x) * 3;
                ring.Add(rgb[o] + rgb[o + 1] + rgb[o + 2]);
            }
            if (ring.Count < angles / 8) { level[i] = double.NaN; continue; }
            ring.Sort();
            level[i] = ring[(int)(ring.Count * 0.9)] / 3.0;
        }

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in level) if (!double.IsNaN(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (hi - lo < 25) return double.PositiveInfinity;
        var cut = lo + 0.35 * (hi - lo);

        var edge = -1;
        for (var i = bins - 1; i >= 0; i--)
            if (!double.IsNaN(level[i]) && level[i] > cut) { edge = i; break; }
        if (edge < 0) return double.PositiveInfinity;
        // The brightness has to fall away beyond the edge for it to be one; if the frame ends
        // first, the sky runs off the sensor.
        var beyond = 0;
        for (var i = edge + 1; i < bins; i++) if (!double.IsNaN(level[i])) beyond++;
        return beyond < 3 ? double.PositiveInfinity : (edge + 1) * step;
    }

    /// <summary>
    /// Inside any of the outlined areas (each by the even-odd rule, as the blur mask fills); true
    /// when none is drawn. A few hundred thousand comparisons a rebuild at most.
    /// </summary>
    internal static bool InArea((double x, double y)[][]? area, double x, double y)
    {
        if (area is null || area.Length == 0) return true;
        foreach (var poly in area)
        {
            var inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var (xi, yi) = poly[i];
                var (xj, yj) = poly[j];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }

    private static (double x, double y, double z) Unit(double raDeg, double decDeg)
    {
        double ra = raDeg * Math.PI / 180, dec = decDeg * Math.PI / 180;
        return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
    }

    private static (double ra, double dec) Slerp((double x, double y, double z) a, (double x, double y, double z) b, double omega, double t)
    {
        var so = Math.Sin(omega);
        double fa = Math.Sin((1 - t) * omega) / so, fb = Math.Sin(t * omega) / so;
        double x = fa * a.x + fb * b.x, y = fa * a.y + fb * b.y, z = fa * a.z + fb * b.z;
        return (SkyMath.Wrap360(Math.Atan2(y, x) * 180 / Math.PI), Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180 / Math.PI);
    }

    public void Dispose() => _scheduler.Dispose();
}
