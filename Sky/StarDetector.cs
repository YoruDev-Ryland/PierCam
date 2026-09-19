using System;
using System.Collections.Generic;
using System.Linq;

namespace PierCam.Sky;

internal readonly record struct DetectedStar(double X, double Y, double Flux, double Peak);

/// <summary>A rectangle of the frame to ignore - a burned-in timestamp, typically.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>
/// Finds point sources in an 8-bit luma frame.
///
/// Connected components above a local background, measured whole. Video compression and lens
/// aberration spread bright stars over dozens of pixels, so any test of "compactness" at a fixed
/// small radius throws away the brightest - and most useful - stars. Components near dark
/// regions are skipped: the edges of equipment silhouettes and cables produce glints that look
/// like stars and are not.
/// </summary>
internal static class StarDetector
{
    public static byte[] LumaFromRgb24(byte[] rgb, int width, int height)
    {
        var l = new byte[width * height];
        for (int i = 0, o = 0; i < l.Length; i++, o += 3)
            l[i] = (byte)((rgb[o] * 77 + rgb[o + 1] * 150 + rgb[o + 2] * 29) >> 8);
        return l;
    }

    /// <summary>
    /// The integer factor that brings a frame down to about 2.5 megapixels. Stars are found in
    /// the reduced frame and the calibration scaled back up: detection on a 12 MP sensor at full
    /// size would cost hundreds of megabytes and several seconds a frame for no gain in accuracy
    /// that a reticle could ever show.
    /// </summary>
    public static int ReductionFor(int w, int h) => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(w * (double)h / 2.6e6)));

    /// <summary>Box-averages the frame down by <paramref name="k"/> in each direction.</summary>
    public static byte[] Reduce(byte[] g, int w, int h, int k, out int rw, out int rh)
    {
        rw = w / k; rh = h / k;
        if (k <= 1) { rw = w; rh = h; return g; }
        var o = new byte[rw * rh]; var area = k * k;
        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
        {
            var s = 0;
            for (var dy = 0; dy < k; dy++)
            {
                var row = (y * k + dy) * w + x * k;
                for (var dx = 0; dx < k; dx++) s += g[row + dx];
            }
            o[y * rw + x] = (byte)(s / area);
        }
        return o;
    }

    public static List<DetectedStar> Find(byte[] g, int w, int h, IReadOnlyList<PixelRect>? ignore = null, int keep = 400)
    {
        // Size limits scale with resolution: a star covers more pixels on a bigger sensor.
        var scale = Math.Max(0.5, Math.Min(w, h) / 1080.0);
        var bgR = Math.Max(8, (int)Math.Round(20 * scale));
        var edgeR = Math.Max(5, (int)Math.Round(10 * scale));
        var maxArea = (int)(300 * scale * scale);

        var bg = BoxMean(g, w, h, bgR);
        var near = MinFilter(bg, w, h, edgeR);

        var res = new List<float>(w * h / 16);
        for (var i = 0; i < w * h; i += 16) if (bg[i] > 40) res.Add(Math.Abs(g[i] - bg[i]));
        if (res.Count < 100) return new List<DetectedStar>();
        res.Sort();
        var sigma = Math.Max(1.0, 1.4826 * res[res.Count / 2]);

        var hot = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (near[i] < 35) continue;
            if (ignore is not null && ignore.Any(r => r.Contains(x, y))) continue;
            hot[i] = g[i] - bg[i] > 3.5 * sigma;
        }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var stars = new List<DetectedStar>();
        for (var start = 0; start < w * h; start++)
        {
            if (!hot[start] || seen[start]) continue;
            double sx = 0, sy = 0, sf = 0, peak = 0; int area = 0, x0 = w, x1 = 0, y0 = h, y1 = 0;
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop(); int x = i % w, y = i / w;
                var f = g[i] - bg[i];
                sx += f * x; sy += f * y; sf += f; area++; peak = Math.Max(peak, f);
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                if (x > 0 && hot[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                if (x < w - 1 && hot[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && hot[i - w] && !seen[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                if (y < h - 1 && hot[i + w] && !seen[i + w]) { seen[i + w] = true; stack.Push(i + w); }
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (area < 3 || area > maxArea) continue;                                   // speckle, or a lit surface
            if (Math.Max(bw, bh) > 3.2 * Math.Min(bw, bh) && Math.Max(bw, bh) > 6) continue;   // a streak or an edge
            if (peak < 5.5 * sigma || sf <= 0) continue;
            stars.Add(new DetectedStar(sx / sf, sy / sf, sf, peak));
        }
        stars.Sort((a, b) => b.Flux.CompareTo(a.Flux));
        return stars.Count > keep ? stars.GetRange(0, keep) : stars;
    }

    /// <summary>
    /// Drops detections that stay put across frames well apart in time. Stars move; LEDs,
    /// highlights on equipment and burned-in text do not.
    /// </summary>
    public static void DropStatic(IList<(DateTime utc, List<DetectedStar> stars)> frames, double tolPx = 2.5)
    {
        var copies = frames.Select(f => f.stars.ToList()).ToList();
        for (var a = 0; a < frames.Count; a++)
        {
            frames[a].stars.RemoveAll(s =>
            {
                var hits = 0;
                for (var b = 0; b < frames.Count; b++)
                {
                    if (Math.Abs((frames[b].utc - frames[a].utc).TotalMinutes) < 45) continue;
                    foreach (var t in copies[b])
                        if (Math.Abs(t.X - s.X) < tolPx && Math.Abs(t.Y - s.Y) < tolPx) { hits++; break; }
                }
                return hits >= 2;
            });
        }
    }

    static float[] BoxMean(byte[] g, int w, int h, int r)
    {
        var ii = new double[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            double row = 0;
            for (var x = 0; x < w; x++)
            {
                row += g[y * w + x];
                ii[(y + 1) * (w + 1) + x + 1] = ii[y * (w + 1) + x + 1] + row;
            }
        }
        var bg = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            var s = ii[y1 * (w + 1) + x1] - ii[y0 * (w + 1) + x1] - ii[y1 * (w + 1) + x0] + ii[y0 * (w + 1) + x0];
            bg[y * w + x] = (float)(s / ((x1 - x0) * (y1 - y0)));
        }
        return bg;
    }

    static float[] MinFilter(float[] src, int w, int h, int r)
    {
        var tmp = new float[w * h]; var dst = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var m = float.MaxValue;
            for (var k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) m = Math.Min(m, src[y * w + k]);
            tmp[y * w + x] = m;
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var m = float.MaxValue;
            for (var k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) m = Math.Min(m, tmp[k * w + x]);
            dst[y * w + x] = m;
        }
        return dst;
    }
}
