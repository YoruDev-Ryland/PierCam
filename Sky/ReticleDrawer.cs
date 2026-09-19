using System;
using PierCam.Video;

namespace PierCam.Sky;

/// <summary>Where to draw the marker on a frame, and what to call it.</summary>
internal readonly record struct MarkerPlacement(double X, double Y, string? Label);

/// <summary>
/// Draws the target marker straight into an RGB24 frame: a ring with four sight ticks outside it,
/// a thin dark outline so it reads over bright sky and dark equipment alike, and the target's
/// name beside it. No glow, no animation - it is a mark on the frame, not something moving over
/// the live image. A few thousand pixel writes, so it costs nothing per frame.
/// </summary>
internal static class ReticleDrawer
{
    private static readonly (double dx, double dy)[] Ticks = { (0, -1), (0, 1), (-1, 0), (1, 0) };

    public static void Draw(byte[] rgb, int w, int h, MarkerPlacement p, byte r, byte g, byte b)
    {
        var s = Math.Max(0.7, Math.Min(w, h) / 1080.0);
        var radius = 20 * s;
        var stroke = Math.Max(1.4, 2 * s);

        Ring(rgb, w, h, p.X, p.Y, radius, stroke + 2.2 * s, 0, 0, 0, 0.55);        // outline
        Ring(rgb, w, h, p.X, p.Y, radius, stroke, r, g, b, 1.0);

        double inner = radius + 5 * s, outer = radius + 13 * s;
        foreach (var (dx, dy) in Ticks)
        {
            Segment(rgb, w, h, p.X + dx * inner, p.Y + dy * inner, p.X + dx * outer, p.Y + dy * outer, stroke + 2.2 * s, 0, 0, 0, 0.55);
            Segment(rgb, w, h, p.X + dx * inner, p.Y + dy * inner, p.X + dx * outer, p.Y + dy * outer, stroke, r, g, b, 1.0);
        }

        // The label arrives already upper-cased and trimmed (see NinaPointing), so nothing here
        // allocates per frame.
        if (!string.IsNullOrWhiteSpace(p.Label))
        {
            var scale = Math.Max(1, (int)Math.Round(2 * s));
            var text = p.Label;
            var (tw, th) = TextOverlay.Measure(text, scale);
            var gap = (int)(radius + 16 * s);
            var lx = (int)(p.X + gap); var ly = (int)(p.Y - gap - th / 2.0);
            if (lx + tw > w - 8) lx = (int)(p.X - gap - tw);            // no room right: go left
            if (ly < 6) ly = (int)(p.Y + gap - th / 2.0);                // no room above: hang below
            TextOverlay.DrawAt(rgb, w, h, text, lx, ly, scale, r, g, b);
        }
    }

    /// <summary>Anti-aliased ring: coverage from the distance to the circle, blended in.</summary>
    private static void Ring(byte[] rgb, int w, int h, double cx, double cy, double radius, double stroke,
        byte r, byte g, byte b, double alpha)
    {
        var reach = radius + stroke;
        int x0 = Math.Max(0, (int)(cx - reach - 1)), x1 = Math.Min(w - 1, (int)(cx + reach + 1));
        int y0 = Math.Max(0, (int)(cy - reach - 1)), y1 = Math.Min(h - 1, (int)(cy + reach + 1));
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var d = Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius);
            var cover = Math.Clamp(stroke / 2 + 0.5 - d, 0, 1);
            if (cover > 0) Blend(rgb, w, x, y, r, g, b, cover * alpha);
        }
    }

    private static void Segment(byte[] rgb, int w, int h, double ax, double ay, double bx, double by, double stroke,
        byte r, byte g, byte b, double alpha)
    {
        int x0 = Math.Max(0, (int)(Math.Min(ax, bx) - stroke - 1)), x1 = Math.Min(w - 1, (int)(Math.Max(ax, bx) + stroke + 1));
        int y0 = Math.Max(0, (int)(Math.Min(ay, by) - stroke - 1)), y1 = Math.Min(h - 1, (int)(Math.Max(ay, by) + stroke + 1));
        double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var t = len2 == 0 ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
            var dx = x - (ax + t * vx); var dy = y - (ay + t * vy);
            var cover = Math.Clamp(stroke / 2 + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
            if (cover > 0) Blend(rgb, w, x, y, r, g, b, cover * alpha);
        }
    }

    private static void Blend(byte[] rgb, int w, int x, int y, byte r, byte g, byte b, double a)
    {
        var o = (y * w + x) * 3;
        rgb[o] = (byte)(rgb[o] + (r - rgb[o]) * a);
        rgb[o + 1] = (byte)(rgb[o + 1] + (g - rgb[o + 1]) * a);
        rgb[o + 2] = (byte)(rgb[o + 2] + (b - rgb[o + 2]) * a);
    }
}
