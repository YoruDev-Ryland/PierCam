using System;
using System.Collections.Generic;

namespace PierCam.Video;

/// <summary>
/// Burns a timestamp straight into the RGB24 frame buffer.
///
/// Deliberately not ffmpeg's drawtext filter: that needs a font file and a libfreetype build,
/// and a missing font would fail the whole night's recording. A built-in 5x7 bitmap covering the
/// handful of glyphs a timestamp needs has no failure mode.
/// </summary>
internal static class TextOverlay
{
    private const int GlyphW = 5;
    private const int GlyphH = 7;

    // Each glyph is 7 rows of 5 bits, MSB (0b10000) leftmost.
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111 },
        ['3'] = new byte[] { 0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 },
        ['6'] = new byte[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100 },
        [':'] = new byte[] { 0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000 },
        ['-'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 },
        ['.'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100 },
        ['/'] = new byte[] { 0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000 },
        ['+'] = new byte[] { 0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['s'] = new byte[] { 0b00000, 0b00000, 0b01111, 0b10000, 0b01110, 0b00001, 0b11110 },
        ['°'] = new byte[] { 0b01100, 0b10010, 0b01100, 0b00000, 0b00000, 0b00000, 0b00000 },
        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
    };

    public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>
    /// Draws <paramref name="text"/> into an RGB24 buffer. Unknown characters are skipped rather
    /// than throwing — a stray glyph must never take down an all-night recording.
    /// </summary>
    public static void Draw(byte[] rgb, int width, int height, string text,
        Corner corner = Corner.BottomLeft, int scale = 3, int margin = 16, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text) || scale < 1) return;

        var advance = (GlyphW + 1) * scale;
        var textW = text.Length * advance - scale;
        var textH = GlyphH * scale;

        var x0 = corner is Corner.TopLeft or Corner.BottomLeft ? margin : width - margin - textW;
        var y0 = corner is Corner.TopLeft or Corner.TopRight ? margin : height - margin - textH;
        if (x0 < 0 || y0 < 0 || x0 + textW > width || y0 + textH > height) return;

        // Dark plate behind the text so it stays readable over a bright moonlit sky.
        if (shadow) Darken(rgb, width, height, x0 - scale * 2, y0 - scale, textW + scale * 4, textH + scale * 2);

        var penX = x0;
        foreach (var ch in text)
        {
            if (Glyphs.TryGetValue(ch, out var glyph)) DrawGlyph(rgb, width, height, glyph, penX, y0, scale);
            penX += advance;
        }
    }

    private static void DrawGlyph(byte[] rgb, int width, int height, byte[] glyph, int x0, int y0, int scale)
    {
        for (var gy = 0; gy < GlyphH; gy++)
        {
            var row = glyph[gy];
            if (row == 0) continue;
            for (var gx = 0; gx < GlyphW; gx++)
            {
                if ((row & (1 << (GlyphW - 1 - gx))) == 0) continue;
                for (var sy = 0; sy < scale; sy++)
                {
                    var py = y0 + gy * scale + sy;
                    if ((uint)py >= (uint)height) continue;
                    var rowOff = py * width * 3;
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var px = x0 + gx * scale + sx;
                        if ((uint)px >= (uint)width) continue;
                        var o = rowOff + px * 3;
                        rgb[o] = 235; rgb[o + 1] = 235; rgb[o + 2] = 235;
                    }
                }
            }
        }
    }

    private static void Darken(byte[] rgb, int width, int height, int x, int y, int w, int h)
    {
        var xEnd = Math.Min(width, x + w);
        var yEnd = Math.Min(height, y + h);
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        for (var py = y; py < yEnd; py++)
        {
            var rowOff = py * width * 3;
            for (var px = x; px < xEnd; px++)
            {
                var o = rowOff + px * 3;
                rgb[o] = (byte)(rgb[o] >> 2);
                rgb[o + 1] = (byte)(rgb[o + 1] >> 2);
                rgb[o + 2] = (byte)(rgb[o + 2] >> 2);
            }
        }
    }
}
