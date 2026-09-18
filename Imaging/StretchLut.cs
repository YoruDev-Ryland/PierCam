using System;

namespace PierCam.Imaging;

/// <summary>
/// Display stretch settings. Black/white are normalised sensor levels (0..1); midtone is the
/// MTF balance point. Colour gains are applied after black/white clipping.
/// </summary>
internal sealed class StretchParams
{
    public double Black { get; set; }
    public double White { get; set; } = 1.0;
    public double Midtone { get; set; } = 0.5;
    public double RedGain { get; set; } = 1.0;
    public double GreenGain { get; set; } = 1.0;
    public double BlueGain { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;

    public StretchParams Clone() => (StretchParams)MemberwiseClone();

    public bool Matches(StretchParams o) =>
        Black == o.Black && White == o.White && Midtone == o.Midtone &&
        RedGain == o.RedGain && GreenGain == o.GreenGain && BlueGain == o.BlueGain;
}

/// <summary>
/// Precomputed 16-bit -> 8-bit transfer tables, one per colour channel.
///
/// Folding black point, white point, midtone transfer and white balance into three 64 KB tables
/// means the per-pixel work in the debayer is a single array index. Tables are rebuilt only when
/// the stretch actually changes, so a steady-state live view does no maths beyond the lookup.
/// </summary>
internal sealed class StretchLut
{
    public const int Size = 65536;

    public byte[] R { get; } = new byte[Size];
    public byte[] G { get; } = new byte[Size];
    public byte[] B { get; } = new byte[Size];

    private readonly StretchParams _current = new() { White = double.NaN };

    /// <summary>Midtone transfer function. m is the balance point, x the normalised input.</summary>
    public static double Mtf(double m, double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;
        if (m <= 0.0) return 1.0;
        if (m >= 1.0) return 0.0;
        if (Math.Abs(m - 0.5) < 1e-9) return x;
        var denom = (2.0 * m - 1.0) * x - m;
        if (Math.Abs(denom) < 1e-12) return x;
        return (m - 1.0) * x / denom;
    }

    /// <summary>
    /// Solves for the midtone balance that maps <paramref name="x"/> to <paramref name="target"/>.
    /// This is what turns a measured sky background into a chosen display brightness.
    /// </summary>
    public static double SolveMidtone(double x, double target)
    {
        if (x <= 0.0) return 0.5;
        if (x >= 1.0) return 0.5;
        var denom = x - 2.0 * target * x + target;
        if (Math.Abs(denom) < 1e-12) return 0.5;
        return Math.Clamp(x * (1.0 - target) / denom, 0.001, 0.999);
    }

    public void Rebuild(StretchParams p)
    {
        if (_current.Matches(p)) return;

        var black = Math.Clamp(p.Black, 0.0, 0.999);
        var white = Math.Clamp(p.White, black + 1e-4, 1.0);
        var span = white - black;
        var mid = Math.Clamp(p.Midtone, 0.001, 0.999);

        Fill(R, black, span, mid, p.RedGain);
        Fill(G, black, span, mid, p.GreenGain);
        Fill(B, black, span, mid, p.BlueGain);

        _current.Black = p.Black;
        _current.White = p.White;
        _current.Midtone = p.Midtone;
        _current.RedGain = p.RedGain;
        _current.GreenGain = p.GreenGain;
        _current.BlueGain = p.BlueGain;
    }

    private static void Fill(byte[] table, double black, double span, double mid, double gain)
    {
        const double inv = 1.0 / (Size - 1);
        for (var i = 0; i < Size; i++)
        {
            var x = (i * inv - black) / span;
            if (x <= 0.0) { table[i] = 0; continue; }
            if (gain != 1.0) x *= gain;
            if (x >= 1.0) { table[i] = 255; continue; }
            var y = Mtf(mid, x);
            table[i] = (byte)Math.Clamp((int)(y * 255.0 + 0.5), 0, 255);
        }
    }
}
