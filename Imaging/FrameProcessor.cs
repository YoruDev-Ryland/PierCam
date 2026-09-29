using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PierCam.Camera;

namespace PierCam.Imaging;

/// <summary>
/// Turns a raw sensor frame into display-ready RGB24.
///
/// One instance per ROI/format. All working buffers are owned by the caller or allocated once,
/// so processing a frame allocates nothing — this is what keeps a multi-day live view flat
/// on memory.
/// </summary>
internal sealed class FrameProcessor
{
    // Colour index per sensel: 0 = red, 1 = green, 2 = blue. Indexed by (y&1)*2 + (x&1).
    private static readonly byte[] PatternRG = { 0, 1, 1, 2 };
    private static readonly byte[] PatternBG = { 2, 1, 1, 0 };
    private static readonly byte[] PatternGR = { 1, 0, 2, 1 };
    private static readonly byte[] PatternGB = { 1, 2, 0, 1 };

    private readonly byte[] _pattern;
    private readonly StretchLut _lut = new();
    private ushort[]? _widenScratch;

    /// <summary>The sensor's own size, before the frame is turned.</summary>
    public int SensorWidth { get; }
    public int SensorHeight { get; }

    /// <summary>The size of what comes out, which swaps for a quarter turn.</summary>
    public int Width { get; }
    public int Height { get; }

    public FrameOrientation Orientation { get; }
    public bool IsColor { get; }
    public AsiImgType ImageType { get; }
    public byte[] Pattern => _pattern;

    public int RgbBytes => Width * Height * 3;

    public FrameProcessor(int width, int height, bool isColor, AsiBayer bayer, AsiImgType imageType,
        FrameOrientation orientation = default)
    {
        SensorWidth = width;
        SensorHeight = height;
        Orientation = orientation;
        (Width, Height) = orientation.Apply(width, height);
        IsColor = isColor && imageType != AsiImgType.Y8;
        ImageType = imageType;
        _pattern = bayer switch
        {
            AsiBayer.RG => PatternRG,
            AsiBayer.BG => PatternBG,
            AsiBayer.GR => PatternGR,
            _ => PatternGB
        };
    }

    /// <summary>
    /// Where a source row starts in the output, and how far each source column steps.
    ///
    /// Every one of the eight orientations is affine in x along a source row — a quarter turn
    /// makes a row into a column, so the step becomes a whole output row rather than three bytes.
    /// Working it out once per row keeps the turn out of the inner loop entirely.
    /// </summary>
    private void RowMapping(int y, out int start, out int step)
    {
        var (x0, y0) = Orientation.Map(0, y, SensorWidth, SensorHeight);
        start = (y0 * Width + x0) * 3;
        if (SensorWidth < 2) { step = 3; return; }
        var (x1, y1) = Orientation.Map(1, y, SensorWidth, SensorHeight);
        step = (y1 * Width + x1) * 3 - start;
    }

    /// <summary>Debayers <paramref name="raw"/> into <paramref name="rgb"/> applying the given stretch.</summary>
    public void Process(ReadOnlySpan<byte> raw, byte[] rgb, StretchParams stretch)
    {
        if (rgb.Length < RgbBytes) throw new ArgumentException("RGB buffer too small.", nameof(rgb));
        _lut.Rebuild(stretch);

        switch (ImageType)
        {
            case AsiImgType.Rgb24:
                ProcessRgb24(raw, rgb);
                return;

            case AsiImgType.Raw16:
            {
                var src = MemoryMarshal.Cast<byte, ushort>(raw[..(SensorWidth * SensorHeight * 2)]);
                if (IsColor) Debayer(src, rgb); else ProcessMono(src, rgb);
                return;
            }

            default: // RAW8 / Y8 — widen into the 16-bit domain so one set of LUTs covers both.
            {
                var scratch = _widenScratch ??= new ushort[SensorWidth * SensorHeight];
                for (var i = 0; i < scratch.Length; i++) scratch[i] = (ushort)(raw[i] << 8);
                ReadOnlySpan<ushort> src = scratch;
                if (IsColor) Debayer(src, rgb); else ProcessMono(src, rgb);
                return;
            }
        }
    }

    private void ProcessRgb24(ReadOnlySpan<byte> raw, byte[] rgb)
    {
        // The SDK hands back BGR24; route it through the LUTs so the stretch still applies.
        var r = _lut.R; var g = _lut.G; var b = _lut.B;
        for (var y = 0; y < SensorHeight; y++)
        {
            RowMapping(y, out var o, out var step);
            var i = y * SensorWidth * 3;
            for (var x = 0; x < SensorWidth; x++, i += 3, o += step)
            {
                rgb[o + 0] = r[raw[i + 2] << 8];
                rgb[o + 1] = g[raw[i + 1] << 8];
                rgb[o + 2] = b[raw[i + 0] << 8];
            }
        }
    }

    private void ProcessMono(ReadOnlySpan<ushort> src, byte[] rgb)
    {
        var g = _lut.G;
        for (var y = 0; y < SensorHeight; y++)
        {
            RowMapping(y, out var o, out var step);
            var i = y * SensorWidth;
            for (var x = 0; x < SensorWidth; x++, i++, o += step)
            {
                var v = g[src[i]];
                rgb[o] = v; rgb[o + 1] = v; rgb[o + 2] = v;
            }
        }
    }

    private unsafe void Debayer(ReadOnlySpan<ushort> src, byte[] rgb)
    {
        int w = SensorWidth, h = SensorHeight;

        fixed (ushort* sPin = src)
        fixed (byte* dPin = rgb)
        fixed (byte* patPin = _pattern)
        fixed (byte* lrPin = _lut.R)
        fixed (byte* lgPin = _lut.G)
        fixed (byte* lbPin = _lut.B)
        {
            // Pointers cannot be captured by a lambda, but IntPtrs can. The `fixed` scope
            // outlives Parallel.For, so the pins stay valid for every worker.
            IntPtr s = (IntPtr)sPin, d = (IntPtr)dPin, pat = (IntPtr)patPin;
            IntPtr lr = (IntPtr)lrPin, lg = (IntPtr)lgPin, lb = (IntPtr)lbPin;

            Parallel.For(0, h, y =>
            {
                var sp = (ushort*)s;
                var dp = (byte*)d;
                var patp = (byte*)pat;
                var lrp = (byte*)lr;
                var lgp = (byte*)lg;
                var lbp = (byte*)lb;

                var rowBase = y * w;
                RowMapping(y, out var outOffset, out var outStep);
                var yOdd = (y & 1) * 2;
                var interiorRow = y > 0 && y < h - 1;

                for (var x = 0; x < w; x++, outOffset += outStep)
                {
                    int r, g, b;
                    int c = patp[yOdd + (x & 1)];

                    if (interiorRow && x > 0 && x < w - 1)
                    {
                        var i = rowBase + x;
                        int up = i - w, dn = i + w;
                        int v = sp[i];
                        switch (c)
                        {
                            case 0: // red sensel
                                r = v;
                                g = (sp[i - 1] + sp[i + 1] + sp[up] + sp[dn] + 2) >> 2;
                                b = (sp[up - 1] + sp[up + 1] + sp[dn - 1] + sp[dn + 1] + 2) >> 2;
                                break;
                            case 2: // blue sensel
                                b = v;
                                g = (sp[i - 1] + sp[i + 1] + sp[up] + sp[dn] + 2) >> 2;
                                r = (sp[up - 1] + sp[up + 1] + sp[dn - 1] + sp[dn + 1] + 2) >> 2;
                                break;
                            default: // green sensel
                                g = v;
                                var horiz = (sp[i - 1] + sp[i + 1] + 1) >> 1;
                                var vert = (sp[up] + sp[dn] + 1) >> 1;
                                if (patp[yOdd + ((x + 1) & 1)] == 0) { r = horiz; b = vert; }
                                else { b = horiz; r = vert; }
                                break;
                        }
                    }
                    else
                    {
                        SampleClamped(sp, w, h, x, y, c, patp, yOdd, out r, out g, out b);
                    }

                    dp[outOffset + 0] = lrp[r];
                    dp[outOffset + 1] = lgp[g];
                    dp[outOffset + 2] = lbp[b];
                }
            });
        }
    }

    /// <summary>Border pixels: the same interpolation, with coordinate clamping.</summary>
    private static unsafe void SampleClamped(ushort* s, int w, int h, int x, int y, int c,
        byte* pattern, int yOdd, out int r, out int g, out int b)
    {
        int At(int px, int py)
        {
            if (px < 0) px = 0; else if (px >= w) px = w - 1;
            if (py < 0) py = 0; else if (py >= h) py = h - 1;
            return s[py * w + px];
        }

        var v = At(x, y);
        switch (c)
        {
            case 0:
                r = v;
                g = (At(x - 1, y) + At(x + 1, y) + At(x, y - 1) + At(x, y + 1) + 2) >> 2;
                b = (At(x - 1, y - 1) + At(x + 1, y - 1) + At(x - 1, y + 1) + At(x + 1, y + 1) + 2) >> 2;
                break;
            case 2:
                b = v;
                g = (At(x - 1, y) + At(x + 1, y) + At(x, y - 1) + At(x, y + 1) + 2) >> 2;
                r = (At(x - 1, y - 1) + At(x + 1, y - 1) + At(x - 1, y + 1) + At(x + 1, y + 1) + 2) >> 2;
                break;
            default:
                g = v;
                var horiz = (At(x - 1, y) + At(x + 1, y) + 1) >> 1;
                var vert = (At(x, y - 1) + At(x, y + 1) + 1) >> 1;
                if (pattern[yOdd + ((x + 1) & 1)] == 0) { r = horiz; b = vert; }
                else { b = horiz; r = vert; }
                break;
        }
    }
}
