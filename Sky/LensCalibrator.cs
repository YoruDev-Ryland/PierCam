using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PierCam.Sky;

/// <summary>Stars found in one frame, and when the frame was taken.</summary>
internal sealed record CalibrationFrame(DateTime Utc, int Width, int Height, List<DetectedStar> Stars);

internal sealed record CalibrationResult(bool Success, LensModel? Lens, int Stars, double RmsPx, int Frames, string Summary);

/// <summary>
/// Works out, with no help, which way a fixed camera points and what its lens does - from
/// nothing but star detections and the times the frames were taken.
///
/// 1. Search. Every axis direction on a grid, every lens scale and projection, both parities.
///    For each, the roll is not searched but voted for: a catalogue star at radius r from the
///    centre can only be a detection at the same radius, and each such pairing implies one roll.
///    The score is how far the winning roll stands above chance, not its raw count - raw counts
///    favour lens scales that crowd the whole sky into a small circle, where random pairings are
///    plentiful. Several frames hours apart vote together; a wrong answer cannot line up with
///    stars that have moved.
/// 2. Verify. The best candidates are pulled in: a vote on the common offset of the whole
///    pattern first (a dense star field defeats nearest-neighbour matching at loose tolerance),
///    then match, refine, tighten, repeat. A right answer snowballs into hundreds of stars; a
///    wrong one stalls.
/// 3. Accept only a clear winner: enough stars, a tight fit, and no genuinely different
///    solution - one explaining the image with a different set of stars - anywhere near it.
///
/// The one thing it cannot catch is a wrong clock: a time error is a rotation of the sky about
/// the pole, and fits the stars perfectly. Frame times must be right.
/// </summary>
internal sealed class LensCalibrator
{
    private readonly double _lat, _lon;
    private readonly ParallelOptions _parallel;
    private readonly CancellationToken _ct;
    private readonly IProgress<string>? _progress;
    private List<CatalogueStar>? _catalogue;

    public LensCalibrator(double latitude, double longitude, int maxThreads, CancellationToken ct,
        IProgress<string>? progress = null, TaskScheduler? scheduler = null)
    {
        _lat = latitude; _lon = longitude; _ct = ct; _progress = progress;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads), CancellationToken = ct };
        if (scheduler is not null) _parallel.TaskScheduler = scheduler;
    }

    private sealed record Seen(CatalogueStar Star, double Alt, double Az, double E, double N, double U);
    private sealed record Match(int Frame, Seen Sky, DetectedStar Det, double Dx, double Dy);
    private sealed record Candidate(double Score, double Alt0, double Az0, double Roll, double F, LensProjection P, int Parity, double Cx, double Cy);

    // ───────────────────────────── public entry points ─────────────────────────────

    public CalibrationResult Calibrate(IReadOnlyList<CalibrationFrame> frames)
    {
        if (frames.Count == 0) return Fail("No frames to calibrate from.", frames);
        int w = frames[0].Width, h = frames[0].Height;
        if (frames.Any(f => f.Width != w || f.Height != h)) return Fail("Frames are not all the same size.", frames);

        var detections = frames.Sum(f => f.Stars.Count);
        if (detections < 30 * frames.Count)
            return Fail($"Too few stars to work with ({detections / Math.Max(1, frames.Count)} per frame) - cloud, moonlight or twilight.", frames);

        _catalogue ??= BrightStars.Load();
        var bright = frames.Select(f => Sky(f.Utc, 3.5)).ToList();
        var faint = frames.Select(f => Sky(f.Utc, 4.5)).ToList();

        // Three frames as far apart in time as the set allows.
        var order = Enumerable.Range(0, frames.Count).OrderBy(i => frames[i].Utc).ToList();
        var search = new[] { order[0], order[order.Count / 2], order[^1] }.Distinct().ToArray();

        double diag = Math.Sqrt((double)w * w + (double)h * h);
        double fMin = 0.12 * w, fMax = 1.2 * w, rMax = 0.62 * diag;

        _progress?.Report("Searching for the camera's orientation");
        var result = Attempt(frames, bright, faint, search, new() { (w / 2.0, h / 2.0) }, tolR: 0.017 * w, angStep: 3, fStep: 1.03, fMin, fMax, rMax);
        if (!result.Success && !_ct.IsCancellationRequested)
        {
            _progress?.Report("Widening the search");
            var grid = new List<(double, double)>();
            for (var i = -1; i <= 1; i++)
            for (var j = -1; j <= 1; j++)
                grid.Add((w / 2.0 + i * 0.03 * w, h / 2.0 + j * 0.03 * w));
            var wide = Attempt(frames, bright, faint, search, grid, tolR: 0.0125 * w, angStep: 2, fStep: 1.02, fMin, fMax, rMax);
            if (wide.Success || wide.Stars > result.Stars) result = wide;
        }
        return result;
    }

    /// <summary>
    /// How well an existing calibration still fits new frames - the nightly check that the
    /// camera has not been knocked. Returns the matched fraction of what the model predicts
    /// should be visible and detected, plus the fit.
    /// </summary>
    public (int matched, double rms) Check(LensModel lens, IReadOnlyList<CalibrationFrame> frames)
    {
        _catalogue ??= BrightStars.Load();
        var faint = frames.Select(f => Sky(f.Utc, 4.5)).ToList();
        var m = MatchAll(lens, frames, faint, Enumerable.Range(0, frames.Count), 5);
        return (m.Count, m.Count == 0 ? 99 : Math.Sqrt(m.Average(x => x.Dx * x.Dx + x.Dy * x.Dy)));
    }

    // ───────────────────────────── the attempt ─────────────────────────────

    private CalibrationResult Attempt(IReadOnlyList<CalibrationFrame> frames, List<List<Seen>> bright, List<List<Seen>> faint,
        int[] search, List<(double x, double y)> centres, double tolR, double angStep, double fStep, double fMin, double fMax, double rMax)
    {
        var pool = new List<Candidate>();
        foreach (var (cx, cy) in centres)
        {
            _ct.ThrowIfCancellationRequested();
            pool.AddRange(Search(frames, bright, search, cx, cy, fMin, fMax, rMax, tolR, angStep, fStep).Take(30));
        }
        if (pool.Count == 0) return Fail("No arrangement of the catalogue matched the stars at all.", frames);

        _progress?.Report("Checking the best candidates");
        var verified = new ConcurrentBag<(int n, double rms, LensModel lens)>();
        Parallel.ForEach(pool.OrderByDescending(c => c.Score).Take(40), _parallel, c =>
        {
            var start = new LensModel
            {
                Projection = c.P, AxisAlt = c.Alt0, AxisAz = c.Az0, Roll = c.Roll, F = c.F, Parity = c.Parity,
                Cx = c.Cx, Cy = c.Cy, Width = frames[0].Width, Height = frames[0].Height
            };
            verified.Add(Verify(start, frames, bright, faint, tolR * 1.2));
        });
        var ranked = verified.OrderByDescending(v => v.n).ToList();
        var best = ranked[0];

        // A rival explains the image with a different set of stars. One that matches mostly the
        // same catalogue stars to the same detections is this answer again - near the zenith,
        // parameter sets degrees apart can describe the same image.
        var support = MatchAll(best.lens, frames, faint, Enumerable.Range(0, frames.Count), 5);
        var mine = support.Select(m => (m.Frame, m.Sky.Star.Hr, m.Det.X, m.Det.Y)).ToHashSet();
        var rival = ranked.Skip(1).Where(v =>
        {
            var theirs = MatchAll(v.lens, frames, faint, Enumerable.Range(0, frames.Count), 5);
            return theirs.Count > 0 && theirs.Count(m => mine.Contains((m.Frame, m.Sky.Star.Hr, m.Det.X, m.Det.Y))) < theirs.Count / 2;
        }).Select(v => v.n).DefaultIfEmpty(0).First();

        var w = frames[0].Width; var h = frames[0].Height;
        var needed = Math.Max(50, 8 * frames.Count);
        var rmsLimit = 2.0 * Math.Max(1, Math.Min(w, h) / 1080.0);
        best.lens.Normalise();

        if (best.n < needed)
            return Fail($"Only {best.n} stars could be matched (needs {needed}) - too cloudy, or too little open sky in view.", frames, best);
        if (best.rms > rmsLimit)
            return Fail($"The best fit is too loose ({best.rms:0.0} px).", frames, best);
        if (rival >= best.n / 2)
            return Fail("Two different solutions fit about equally well, so neither can be trusted.", frames, best);

        return new CalibrationResult(true, best.lens, best.n, best.rms, frames.Count,
            $"{best.n} stars matched across {frames.Count} frames, {best.rms:0.0} px");
    }

    private static CalibrationResult Fail(string why, IReadOnlyList<CalibrationFrame> frames,
        (int n, double rms, LensModel lens)? best = null) =>
        new(false, best?.lens, best?.n ?? 0, best?.rms ?? 0, frames.Count, why);

    // ───────────────────────────── catalogue positions ─────────────────────────────

    private List<Seen> Sky(DateTime utc, double maxMag)
    {
        var list = new List<Seen>();
        foreach (var c in _catalogue!)
        {
            if (c.Mag > maxMag) continue;
            var (ra, dec) = SkyMath.PrecessFromJ2000(c.RaJ2000, c.DecJ2000, utc);
            var (alt, az) = SkyMath.ToAltAz(ra, dec, utc, _lat, _lon);
            if (alt < 2) continue;
            alt += SkyMath.Refraction(alt);
            var (e, n, u) = SkyMath.Enu(alt, az);
            list.Add(new Seen(c, alt, az, e, n, u));
        }
        return list;
    }

    // ───────────────────────────── search ─────────────────────────────

    private List<Candidate> Search(IReadOnlyList<CalibrationFrame> frames, List<List<Seen>> skies, int[] use,
        double cx, double cy, double fMin, double fMax, double rMax, double tolR, double angStep, double fStep)
    {
        var projections = Enum.GetValues<LensProjection>();
        var polar = new Dictionary<(int f, int par), (double[] r, double[] a)>();
        foreach (var f in use)
        foreach (var par in new[] { 1, -1 })
        {
            var arr = frames[f].Stars.Take(220)
                .Select(s => (r: Math.Sqrt((s.X - cx) * (s.X - cx) + (s.Y - cy) * (s.Y - cy)), a: Math.Atan2(par * (s.Y - cy), s.X - cx)))
                .OrderBy(t => t.r).ToArray();
            polar[(f, par)] = (arr.Select(t => t.r).ToArray(), arr.Select(t => t.a).ToArray());
        }

        var fGrid = new List<double>();
        for (var f = fMin; f <= fMax; f *= fStep) fGrid.Add(f);

        var alts = new List<double>();
        for (var a = 0.0; a <= 90.0; a += angStep) alts.Add(a);

        var results = new ConcurrentBag<Candidate>();
        Parallel.ForEach(alts, _parallel, alt0 =>
        {
            var votes = new int[360];
            var local = new List<Candidate>();
            var ph = new Dictionary<int, double[]>();
            var rad = new Dictionary<(int f, LensProjection p), double[]>();
            // Near the zenith azimuth steps crowd together: step so the axis moves ~angStep on the sky.
            var azStep = Math.Min(90, angStep / Math.Max(0.05, Math.Cos(alt0 * SkyMath.D2R)));
            for (var az0 = 0.0; az0 < 360; az0 += azStep)
            {
                foreach (var f in use)
                {
                    var sk = skies[f]; var t = new double[sk.Count]; var p = new double[sk.Count];
                    for (var i = 0; i < sk.Count; i++) LensModel.Angles(alt0, az0, sk[i].E, sk[i].N, sk[i].U, out t[i], out p[i]);
                    ph[f] = p;
                    foreach (var proj in projections)
                    {
                        var rr = new double[sk.Count]; var lim = LensModel.MaxTheta(proj);
                        for (var i = 0; i < t.Length; i++) rr[i] = t[i] > lim ? -1 : LensModel.Radial(proj, t[i]);
                        rad[(f, proj)] = rr;
                    }
                }
                foreach (var proj in projections)
                foreach (var fpx in fGrid)
                foreach (var par in new[] { 1, -1 })
                {
                    Array.Clear(votes);
                    var pairs = 0;
                    foreach (var f in use)
                    {
                        var (dr, da) = polar[(f, par)];
                        var rr = rad[(f, proj)]; var p = ph[f];
                        for (var i = 0; i < rr.Length; i++)
                        {
                            if (rr[i] < 0) continue;
                            var r = fpx * rr[i];
                            if (r > rMax || r < 25) continue;
                            for (var j = Lower(dr, r - tolR); j < dr.Length && dr[j] < r + tolR; j++)
                            {
                                var d = SkyMath.Wrap360((p[i] - da[j]) * SkyMath.R2D);
                                votes[(int)d % 360]++;
                                pairs++;
                            }
                        }
                    }
                    int bestB = 0, bestV = 0;
                    for (var b = 0; b < 360; b++)
                    {
                        var v = votes[(b + 359) % 360] + votes[b] + votes[(b + 1) % 360];
                        if (v > bestV) { bestV = v; bestB = b; }
                    }
                    if (bestV < 12) continue;
                    var chance = Math.Max(1.0, pairs * 3.0 / 360);
                    local.Add(new Candidate((bestV - chance) / Math.Sqrt(chance), alt0, az0, bestB + 0.5, fpx, proj, par, cx, cy));
                }
            }
            foreach (var c in local.OrderByDescending(c => c.Score).Take(10)) results.Add(c);
        });
        return results.OrderByDescending(c => c.Score).ToList();
    }

    private static int Lower(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var m = (lo + hi) / 2; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    // ───────────────────────────── verify and refine ─────────────────────────────

    private (int n, double rms, LensModel lens) Verify(LensModel start, IReadOnlyList<CalibrationFrame> frames,
        List<List<Seen>> bright, List<List<Seen>> faint, double firstTol)
    {
        var lens = start.Clone();
        var all = Enumerable.Range(0, frames.Count).ToArray();
        var w = frames[0].Width;
        CentreByOffsetVote(lens, frames, bright, all, (int)Math.Max(40, firstTol * 2));
        CentreByOffsetVote(lens, frames, bright, all, (int)Math.Max(24, 0.0125 * w));
        var steps = new[] { (0.0073, false, bright), (0.0052, false, bright), (0.0042, true, faint), (0.0031, true, faint) };
        foreach (var (tolFrac, k1, mags) in steps)
        {
            var tol = Math.Max(tolFrac * w, 3);
            var m = MatchAll(lens, frames, mags, all, tol);
            if (m.Count < 8) return (m.Count, 99, lens);
            Refine(lens, m, k1, freeCentre: true, iters: 25);
            if (lens.F < 0.05 * w || lens.F > 3 * w || Math.Abs(lens.K1) > 0.5 || !lens.IsUsable) return (0, 99, lens);
        }
        var fin = MatchAll(lens, frames, faint, all, Math.Max(0.0026 * w, 3));
        var rms = fin.Count == 0 ? 99 : Math.Sqrt(fin.Average(x => x.Dx * x.Dx + x.Dy * x.Dy));
        return (fin.Count, rms, lens);
    }

    /// <summary>
    /// Every (catalogue star, detection) pair within reach votes for its offset; the true shift
    /// collects votes from every star at once while wrong pairings scatter.
    /// </summary>
    private static void CentreByOffsetVote(LensModel lens, IReadOnlyList<CalibrationFrame> frames, List<List<Seen>> skies, int[] use, int reach)
    {
        const int bin = 4; var n = 2 * reach / bin + 1;
        var h = new int[n, n];
        foreach (var f in use)
        {
            var top = frames[f].Stars.Take(150).ToList();
            foreach (var s in skies[f])
            {
                if (s.Star.Mag > 3.0 || !lens.TryProject(s.Alt, s.Az, out var x, out var y)) continue;
                foreach (var d in top)
                {
                    var dx = d.X - x; var dy = d.Y - y;
                    if (Math.Abs(dx) >= reach || Math.Abs(dy) >= reach) continue;
                    h[(int)((dx + reach) / bin), (int)((dy + reach) / bin)]++;
                }
            }
        }
        int bi = n / 2, bj = n / 2, bv = -1;
        for (var i = 1; i < n - 1; i++)
        for (var j = 1; j < n - 1; j++)
        {
            var v = 0;
            for (var a = -1; a <= 1; a++) for (var b = -1; b <= 1; b++) v += h[i + a, j + b];
            if (v > bv) { bv = v; bi = i; bj = j; }
        }
        lens.Cx += bi * bin + bin / 2.0 - reach;
        lens.Cy += bj * bin + bin / 2.0 - reach;
    }

    private static List<Match> MatchAll(LensModel lens, IReadOnlyList<CalibrationFrame> frames, List<List<Seen>> skies,
        IEnumerable<int> use, double tol)
    {
        var result = new List<Match>();
        foreach (var f in use)
        {
            var dets = frames[f].Stars;
            var used = new HashSet<int>();
            foreach (var s in skies[f].OrderBy(s => s.Star.Mag))
            {
                if (!lens.TryProject(s.Alt, s.Az, out var x, out var y)) continue;
                if (x < 0 || y < 0 || x >= frames[f].Width || y >= frames[f].Height) continue;
                var best = -1; var bd = tol * tol;
                for (var i = 0; i < dets.Count; i++)
                {
                    var dd = (dets[i].X - x) * (dets[i].X - x) + (dets[i].Y - y) * (dets[i].Y - y);
                    if (dd < bd && !used.Contains(i)) { bd = dd; best = i; }
                }
                if (best < 0) continue;
                used.Add(best);
                result.Add(new Match(f, s, dets[best], dets[best].X - x, dets[best].Y - y));
            }
        }
        return result;
    }

    /// <summary>Levenberg-Marquardt on the lens parameters over a fixed set of star pairs.</summary>
    private static void Refine(LensModel lens, List<Match> matches, bool freeK1, bool freeCentre, int iters)
    {
        var free = new List<int> { 0, 1, 2, 3 };
        if (freeK1) free.Add(4);
        if (freeCentre) { free.Add(5); free.Add(6); }
        var lambda = 1e-3;
        var cost = Cost(lens, matches);
        for (var it = 0; it < iters; it++)
        {
            var p0 = lens.Pack();
            int n = free.Count, m = matches.Count * 2;
            var jac = new double[m, n]; var r = Residuals(lens, matches);
            for (var k = 0; k < n; k++)
            {
                var p = (double[])p0.Clone();
                var step = free[k] switch { 3 => 0.5, 4 => 1e-4, 5 or 6 => 0.2, _ => 1e-3 };
                p[free[k]] += step; lens.Unpack(p);
                var r2 = Residuals(lens, matches);
                for (var i = 0; i < m; i++) jac[i, k] = (r2[i] - r[i]) / step;
            }
            lens.Unpack(p0);
            var jtj = new double[n, n]; var jtr = new double[n];
            for (var i = 0; i < m; i++)
            for (var a = 0; a < n; a++)
            {
                jtr[a] += jac[i, a] * r[i];
                for (var b = 0; b < n; b++) jtj[a, b] += jac[i, a] * jac[i, b];
            }
            var improved = false;
            for (var tries = 0; tries < 8 && !improved; tries++)
            {
                var a = (double[,])jtj.Clone();
                for (var d = 0; d < n; d++) a[d, d] *= 1 + lambda;
                var delta = Solve(a, jtr.Select(v => -v).ToArray());
                var p = (double[])p0.Clone();
                for (var k = 0; k < n; k++) p[free[k]] += delta[k];
                lens.Unpack(p);
                var c = Cost(lens, matches);
                if (c < cost && double.IsFinite(c)) { cost = c; lambda *= 0.3; improved = true; }
                else { lens.Unpack(p0); lambda *= 10; }
            }
            if (!improved) break;
        }
    }

    private static double[] Residuals(LensModel lens, List<Match> ms)
    {
        var r = new double[ms.Count * 2];
        for (var i = 0; i < ms.Count; i++)
        {
            lens.TryProject(ms[i].Sky.Alt, ms[i].Sky.Az, out var x, out var y);
            r[2 * i] = x - ms[i].Det.X; r[2 * i + 1] = y - ms[i].Det.Y;
        }
        return r;
    }

    private static double Cost(LensModel lens, List<Match> ms) => Residuals(lens, ms).Sum(v => v * v);

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length; var m = (double[,])a.Clone(); var x = (double[])b.Clone();
        for (var c = 0; c < n; c++)
        {
            var piv = c;
            for (var r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (piv != c)
            {
                for (var k = 0; k < n; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            if (Math.Abs(m[c, c]) < 1e-15) continue;
            for (var r = c + 1; r < n; r++)
            {
                var f = m[r, c] / m[c, c];
                for (var k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            var s = x[r];
            for (var k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = Math.Abs(m[r, r]) < 1e-15 ? 0 : s / m[r, r];
        }
        return x;
    }
}
