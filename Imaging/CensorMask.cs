using System;
using System.Collections.Generic;
using System.Linq;
using PierCam.Models;

namespace PierCam.Imaging;

/// <summary>
/// Blurs out parts of the frame — the owner's name taped to a pier, most often.
///
/// Three things make this usable rather than merely possible:
///
///   · The edge is faded, not cut. A hard-edged blur patch announces itself and looks like a
///     mistake; a feathered one reads as depth of field. The mask is drawn, then blurred, and the
///     blurred mask is what the composite fades along.
///   · It is rebuilt only when something changes — the polygon, the frame size, the turn — and
///     the per-frame work is confined to the bounding box of what is actually censored. A name on
///     a pier is a few hundred pixels across, so this costs a fraction of a millisecond a frame.
///   · The blur is strong enough to destroy text, not merely soften it. Three box passes make a
///     good gaussian, at a radius scaled to the region so a small patch is not left readable.
///
/// Points are stored against the sensor, not the turned frame, so rotating the camera moves the
/// mask with the thing it is hiding.
/// </summary>
internal sealed class CensorMask
{
    private byte[]? _alpha;          // feathered coverage, bounding box sized
    private byte[]? _scratch;        // the shrunken, blurred copy of the box
    private byte[]? _blurRow;
    private int _x0, _y0, _boxW, _boxH, _radius;
    private int _step, _smallW, _smallH;
    private int _forWidth, _forHeight;
    private string _forShape = string.Empty;

    /// <summary>Whether there is anything to draw at the moment.</summary>
    public bool HasWork => _alpha is not null && _boxW > 0 && _boxH > 0;

    /// <summary>
    /// Rebuilds for this frame size and turn if anything has changed. Cheap to call every frame;
    /// it compares a signature and returns.
    /// </summary>
    public void Update(IReadOnlyList<CensorRegion> regions, int width, int height, FrameOrientation orientation)
    {
        var shape = Signature(regions, orientation);
        if (shape == _forShape && width == _forWidth && height == _forHeight) return;
        _forShape = shape;
        _forWidth = width;
        _forHeight = height;
        Build(regions, width, height, orientation);
    }

    private static string Signature(IReadOnlyList<CensorRegion> regions, FrameOrientation o)
    {
        var parts = regions.Select(r => string.Join(";", r.Points.Select(p => $"{p.X:0.00000},{p.Y:0.00000}")));
        return $"{o.Mirror}:{o.Quarters}|{string.Join("|", parts)}";
    }

    private void Build(IReadOnlyList<CensorRegion> regions, int width, int height, FrameOrientation orientation)
    {
        _alpha = null;
        _boxW = _boxH = 0;

        // Into pixels of the turned frame.
        var polys = new List<(double X, double Y)[]>();
        foreach (var region in regions)
        {
            if (region.Points.Count < 3) continue;
            var pts = region.Points.Select(p =>
            {
                var (ux, uy) = orientation.MapUnit(p.X, p.Y);
                return (X: ux * (width - 1), Y: uy * (height - 1));
            }).ToArray();
            polys.Add(pts);
        }
        if (polys.Count == 0) return;

        // The feather has to live inside the mask, so the box is grown by it.
        var feather = Math.Max(6, (int)Math.Round(Math.Min(width, height) * 0.012));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in polys.SelectMany(p => p))
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        _x0 = Math.Max(0, (int)Math.Floor(minX) - feather * 2);
        _y0 = Math.Max(0, (int)Math.Floor(minY) - feather * 2);
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(maxX) + feather * 2);
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY) + feather * 2);
        _boxW = x1 - _x0 + 1;
        _boxH = y1 - _y0 + 1;
        if (_boxW <= 2 || _boxH <= 2) { _boxW = _boxH = 0; return; }

        // Fill the polygons, then blur the coverage itself: that fade is the soft edge.
        var mask = new byte[_boxW * _boxH];
        foreach (var poly in polys) Fill(mask, poly);
        BoxBlur(mask, _boxW, _boxH, feather, 2);
        _alpha = mask;

        // The blur is done on a shrunken copy and faded back in at full size. Blurring at full
        // resolution is work spent making detail that is then thrown away: the output is meant to
        // be unrecognisable, and a heavy blur of a small image is indistinguishable from a heavy
        // blur of a large one. It also makes the cost independent of the sensor, which matters
        // when this runs on every frame of a live view for days.
        _step = Math.Max(1, Math.Min(_boxW, _boxH) / 96);
        _smallW = Math.Max(1, _boxW / _step);
        _smallH = Math.Max(1, _boxH / _step);
        _radius = Math.Clamp(Math.Min(_smallW, _smallH) / 6, 2, 24);
        _scratch = new byte[_smallW * _smallH * 3];
        _blurRow = new byte[Math.Max(_smallW, _smallH) * 3];
    }

    /// <summary>Scanline fill with the even-odd rule, which handles a self-crossing outline sensibly.</summary>
    private void Fill(byte[] mask, (double X, double Y)[] poly)
    {
        var xs = new List<double>();
        for (var y = 0; y < _boxH; y++)
        {
            var sy = y + _y0 + 0.5;
            xs.Clear();
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                double y1 = poly[i].Y, y2 = poly[j].Y;
                if (y1 <= sy && y2 > sy || y2 <= sy && y1 > sy)
                    xs.Add(poly[i].X + (sy - y1) / (y2 - y1) * (poly[j].X - poly[i].X));
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                var from = Math.Max(_x0, (int)Math.Round(xs[k]));
                var to = Math.Min(_x0 + _boxW - 1, (int)Math.Round(xs[k + 1]));
                for (var x = from; x <= to; x++) mask[y * _boxW + (x - _x0)] = 255;
            }
        }
    }

    /// <summary>
    /// Blurs the region and fades the result in along the mask. Called on the capture thread with
    /// the frame it has just processed, so it allocates nothing.
    /// </summary>
    public void Apply(byte[] rgb, int width, int height)
    {
        if (_alpha is null || _scratch is null || _blurRow is null) return;
        if (_forWidth != width || _forHeight != height) return;

        // Shrink the box by averaging, blur that, then fade it back in at full size.
        for (var sy = 0; sy < _smallH; sy++)
        {
            for (var sx = 0; sx < _smallW; sx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                var yEnd = Math.Min(_boxH, (sy + 1) * _step);
                var xEnd = Math.Min(_boxW, (sx + 1) * _step);
                for (var y = sy * _step; y < yEnd; y++)
                {
                    var row = ((y + _y0) * width + _x0) * 3;
                    for (var x = sx * _step; x < xEnd; x++)
                    {
                        var o = row + x * 3;
                        r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; n++;
                    }
                }
                if (n == 0) n = 1;
                var d0 = (sy * _smallW + sx) * 3;
                _scratch[d0] = (byte)(r / n);
                _scratch[d0 + 1] = (byte)(g / n);
                _scratch[d0 + 2] = (byte)(b / n);
            }
        }

        BoxBlurRgb(_scratch, _smallW, _smallH, _radius, 3);

        for (var y = 0; y < _boxH; y++)
        {
            var dst = ((y + _y0) * width + _x0) * 3;
            var row = y * _boxW;
            var sy = Math.Min(_smallH - 1, y / _step);
            for (var x = 0; x < _boxW; x++)
            {
                int a = _alpha[row + x];
                if (a == 0) continue;
                var d = dst + x * 3;
                var s = (sy * _smallW + Math.Min(_smallW - 1, x / _step)) * 3;
                if (a == 255)
                {
                    rgb[d] = _scratch[s]; rgb[d + 1] = _scratch[s + 1]; rgb[d + 2] = _scratch[s + 2];
                    continue;
                }
                var inv = 255 - a;
                rgb[d] = (byte)((rgb[d] * inv + _scratch[s] * a) / 255);
                rgb[d + 1] = (byte)((rgb[d + 1] * inv + _scratch[s + 1] * a) / 255);
                rgb[d + 2] = (byte)((rgb[d + 2] * inv + _scratch[s + 2] * a) / 255);
            }
        }
    }

    /// <summary>Repeated box blur of a single channel — three passes is a gaussian to the eye.</summary>
    private static void BoxBlur(byte[] data, int w, int h, int radius, int passes)
    {
        if (radius < 1) return;
        var line = new byte[Math.Max(w, h)];
        for (var pass = 0; pass < passes; pass++)
        {
            for (var y = 0; y < h; y++) BlurLine(data, y * w, 1, w, radius, line);
            for (var x = 0; x < w; x++) BlurLine(data, x, w, h, radius, line);
        }
    }

    private static void BlurLine(byte[] data, int start, int stride, int count, int radius, byte[] line)
    {
        for (var i = 0; i < count; i++) line[i] = data[start + i * stride];
        var window = radius * 2 + 1;
        var sum = 0;
        for (var i = -radius; i <= radius; i++) sum += line[Math.Clamp(i, 0, count - 1)];
        for (var i = 0; i < count; i++)
        {
            data[start + i * stride] = (byte)(sum / window);
            sum += line[Math.Clamp(i + radius + 1, 0, count - 1)] - line[Math.Clamp(i - radius, 0, count - 1)];
        }
    }

    /// <summary>The same for interleaved RGB, which is what the frame is.</summary>
    private void BoxBlurRgb(byte[] data, int w, int h, int radius, int passes)
    {
        if (radius < 1) return;
        for (var pass = 0; pass < passes; pass++)
        {
            for (var y = 0; y < h; y++) BlurLineRgb(data, y * w * 3, 3, w, radius);
            for (var x = 0; x < w; x++) BlurLineRgb(data, x * 3, w * 3, h, radius);
        }
    }

    private void BlurLineRgb(byte[] data, int start, int stride, int count, int radius)
    {
        var line = _blurRow!;
        for (var i = 0; i < count; i++)
        {
            var s = start + i * stride;
            line[i * 3] = data[s]; line[i * 3 + 1] = data[s + 1]; line[i * 3 + 2] = data[s + 2];
        }

        var window = radius * 2 + 1;
        int r = 0, g = 0, b = 0;
        for (var i = -radius; i <= radius; i++)
        {
            var k = Math.Clamp(i, 0, count - 1) * 3;
            r += line[k]; g += line[k + 1]; b += line[k + 2];
        }
        for (var i = 0; i < count; i++)
        {
            var d = start + i * stride;
            data[d] = (byte)(r / window);
            data[d + 1] = (byte)(g / window);
            data[d + 2] = (byte)(b / window);

            var add = Math.Clamp(i + radius + 1, 0, count - 1) * 3;
            var drop = Math.Clamp(i - radius, 0, count - 1) * 3;
            r += line[add] - line[drop];
            g += line[add + 1] - line[drop + 1];
            b += line[add + 2] - line[drop + 2];
        }
    }
}
