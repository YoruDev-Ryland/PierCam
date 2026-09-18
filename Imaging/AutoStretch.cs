using System;
using System.Runtime.InteropServices;
using PierCam.Camera;

namespace PierCam.Imaging;

/// <summary>How the display/recording stretch is chosen for each frame.</summary>
internal enum StretchMode
{
    /// <summary>Recompute from scratch every frame. Great for live view, flickers in a timelapse.</summary>
    Auto,
    /// <summary>Track the sky slowly so twilight-to-dark transitions stay smooth and flicker-free.</summary>
    Smoothed,
    /// <summary>Whatever the user dialled in; never adapts.</summary>
    Manual
}

/// <summary>
/// Measures the sky background and derives a stretch from it.
///
/// Uses the standard robust statistics for astro frames — median for the background level and
/// MAD for the noise — then solves the midtone transfer function so the background lands on a
/// chosen display brightness. Per-channel medians additionally give us a free neutral white
/// balance: equalising the three backgrounds removes light-pollution colour cast.
/// </summary>
internal sealed class AutoStretchAnalyzer
{
    private const int Bins = 65536;

    private readonly int[] _histR = new int[Bins];
    private readonly int[] _histG = new int[Bins];
    private readonly int[] _histB = new int[Bins];
    private readonly int[] _diffHist = new int[Bins];

    private readonly int _width;
    private readonly int _height;
    private readonly bool _isColor;
    private readonly AsiImgType _imgType;
    private readonly byte[] _pattern;

    /// <summary>Display level the sky background is mapped to. Higher = brighter, noisier.</summary>
    public double TargetBackground { get; set; } = 0.22;

    /// <summary>How many noise sigmas below the sky level the black point sits.</summary>
    public double ShadowClip { get; set; } = 2.8;

    /// <summary>
    /// Minimum gap between black point and sky level, as a fraction of the sky level.
    ///
    /// On a bright, light-polluted sky the read noise is tiny compared to the sky signal, so a
    /// purely noise-derived black point sits almost on top of the background and leaves the image
    /// looking milky. This floor guarantees some actual contrast.
    /// </summary>
    public double ShadowDepth { get; set; } = 0.30;

    /// <summary>Equalise per-channel backgrounds to neutralise light-pollution colour cast.</summary>
    public bool NeutraliseBackground { get; set; } = true;

    /// <summary>
    /// Sky median from the last <see cref="Analyze"/>, as a fraction of full scale. This is the
    /// measurement auto-exposure drives from, so it is deliberately the raw sensor level and not
    /// anything the stretch has touched.
    /// </summary>
    public double LastSkyLevel { get; private set; }

    /// <summary>Fraction of sampled pixels at or near full scale on the last frame.</summary>
    public double LastClippedFraction { get; private set; }

    public AutoStretchAnalyzer(FrameProcessor fp)
    {
        _width = fp.Width;
        _height = fp.Height;
        _isColor = fp.IsColor;
        _imgType = fp.ImageType;
        _pattern = fp.Pattern;
    }

    public StretchParams Analyze(ReadOnlySpan<byte> raw)
    {
        Array.Clear(_histR);
        Array.Clear(_histG);
        Array.Clear(_histB);
        Array.Clear(_diffHist);

        if (_imgType == AsiImgType.Raw16)
            Accumulate(MemoryMarshal.Cast<byte, ushort>(raw[..(_width * _height * 2)]));
        else
            AccumulateBytes(raw);

        var medG = Percentile(_histG, 0.5);
        var medR = _isColor ? Percentile(_histR, 0.5) : medG;
        var medB = _isColor ? Percentile(_histB, 0.5) : medG;
        var sigma = NoiseSigma();

        LastSkyLevel = medG / (Bins - 1.0);
        LastClippedFraction = ClippedFraction();

        var minMed = Math.Min(medG, Math.Min(medR, medB));
        // Take whichever pulls the black point further down: the noise floor, or the contrast
        // floor. On a dark site the first wins; under light pollution the second does.
        var drop = Math.Max(ShadowClip * sigma, ShadowDepth * minMed);
        var black = Math.Clamp((minMed - drop) / (Bins - 1.0), 0.0, 0.98);

        const double white = 1.0;
        var bgG = (medG / (Bins - 1.0) - black) / (white - black);
        var midtone = StretchLut.SolveMidtone(Math.Clamp(bgG, 1e-6, 0.99), TargetBackground);

        var p = new StretchParams { Black = black, White = white, Midtone = midtone };

        if (_isColor && NeutraliseBackground)
        {
            // Scale R and B so their backgrounds sit on top of green's.
            var gLevel = medG / (Bins - 1.0) - black;
            var rLevel = medR / (Bins - 1.0) - black;
            var bLevel = medB / (Bins - 1.0) - black;
            p.RedGain = SafeGain(gLevel, rLevel);
            p.BlueGain = SafeGain(gLevel, bLevel);
        }
        return p;
    }

    private static double SafeGain(double reference, double channel)
    {
        if (channel <= 1e-6 || reference <= 1e-6) return 1.0;
        return Math.Clamp(reference / channel, 0.4, 3.0);
    }

    // Sample every other 2x2 Bayer block: ~130k blocks at 1080p, plenty for robust statistics
    // and cheap enough to run on every frame without touching the capture cadence.
    private const int BlockStep = 2;

    private void Accumulate(ReadOnlySpan<ushort> src)
    {
        if (!_isColor)
        {
            for (var y = 0; y < _height; y += BlockStep)
            {
                var row = y * _width;
                for (var x = 0; x < _width; x += BlockStep)
                {
                    _histG[src[row + x]]++;
                    if (x + 1 < _width) _diffHist[Math.Abs(src[row + x] - src[row + x + 1])]++;
                }
            }
            return;
        }

        var pat = _pattern;
        for (var by = 0; by + 1 < _height; by += 2 * BlockStep)
        {
            var row0 = by * _width;
            var row1 = row0 + _width;
            for (var bx = 0; bx + 1 < _width; bx += 2 * BlockStep)
            {
                Bump(pat[0], src[row0 + bx]);
                Bump(pat[1], src[row0 + bx + 1]);
                Bump(pat[2], src[row1 + bx]);
                Bump(pat[3], src[row1 + bx + 1]);

                // Two sensels of the same colour, two pixels apart: their difference is noise
                // plus a negligible amount of real gradient, which is what we want to measure.
                if (bx + 3 < _width) _diffHist[Math.Abs(src[row0 + bx + 1] - src[row0 + bx + 3])]++;
            }
        }
    }

    private void AccumulateBytes(ReadOnlySpan<byte> src)
    {
        if (!_isColor)
        {
            for (var y = 0; y < _height; y += BlockStep)
            {
                var row = y * _width;
                for (var x = 0; x < _width; x += BlockStep)
                {
                    _histG[src[row + x] << 8]++;
                    if (x + 1 < _width) _diffHist[Math.Abs(src[row + x] - src[row + x + 1]) << 8]++;
                }
            }
            return;
        }

        var pat = _pattern;
        for (var by = 0; by + 1 < _height; by += 2 * BlockStep)
        {
            var row0 = by * _width;
            var row1 = row0 + _width;
            for (var bx = 0; bx + 1 < _width; bx += 2 * BlockStep)
            {
                Bump(pat[0], (ushort)(src[row0 + bx] << 8));
                Bump(pat[1], (ushort)(src[row0 + bx + 1] << 8));
                Bump(pat[2], (ushort)(src[row1 + bx] << 8));
                Bump(pat[3], (ushort)(src[row1 + bx + 1] << 8));

                if (bx + 3 < _width)
                    _diffHist[Math.Abs(src[row0 + bx + 1] - src[row0 + bx + 3]) << 8]++;
            }
        }
    }

    private void Bump(byte colour, ushort v)
    {
        switch (colour)
        {
            case 0: _histR[v]++; break;
            case 2: _histB[v]++; break;
            default: _histG[v]++; break;
        }
    }

    /// <summary>
    /// Share of sampled pixels within 1% of full scale. A frame that is mostly saturated reports
    /// a median of ~1.0 which understates how far over-exposed it is, so auto-exposure uses this
    /// to recognise the case and keep stepping down.
    /// </summary>
    private double ClippedFraction()
    {
        long clipped = 0, total = 0;
        var threshold = (int)(Bins * 0.99);
        foreach (var hist in new[] { _histR, _histG, _histB })
        {
            for (var i = 0; i < Bins; i++)
            {
                total += hist[i];
                if (i >= threshold) clipped += hist[i];
            }
        }
        return total == 0 ? 0 : clipped / (double)total;
    }

    private static double Percentile(int[] hist, double fraction)
    {
        long total = 0;
        for (var i = 0; i < hist.Length; i++) total += hist[i];
        if (total == 0) return 0;

        var target = (long)(total * fraction);
        long running = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            running += hist[i];
            if (running >= target) return i;
        }
        return hist.Length - 1;
    }

    /// <summary>
    /// Per-pixel noise sigma, from the median absolute difference between neighbouring same-colour
    /// pixels.
    ///
    /// Taking the MAD of the frame's own histogram would measure the *scene* — a dome edge, a
    /// horizon, a lit building all inflate it enormously — and the black point derived from it
    /// would be meaningless. Differencing nearby pixels cancels everything that varies slowly and
    /// leaves only noise. Dividing by 0.6745 converts MAD to sigma, and by sqrt(2) undoes the
    /// variance doubling that differencing two samples introduces.
    /// </summary>
    private double NoiseSigma()
    {
        var madDiff = Percentile(_diffHist, 0.5);
        return Math.Max(madDiff * (1.0 / 0.6745) / Math.Sqrt(2.0), 1.0);
    }
}

/// <summary>
/// Low-pass filter over successive auto-stretch results.
///
/// A timelapse stretched independently per frame flickers badly, because the measured background
/// wobbles frame to frame. Averaging the stretch over a long window keeps genuine sky changes
/// (twilight, moonrise, dawn) while rejecting that wobble.
/// </summary>
internal sealed class SmoothedStretch
{
    private StretchParams? _state;

    /// <summary>Roughly how many frames the filter averages over.</summary>
    public double WindowFrames { get; set; } = 20.0;

    public void Reset() => _state = null;

    public StretchParams Update(StretchParams measured)
    {
        if (_state is null)
        {
            _state = measured.Clone();
            return _state.Clone();
        }

        var a = 1.0 / Math.Max(1.0, WindowFrames);
        _state.Black = Lerp(_state.Black, measured.Black, a);
        _state.White = Lerp(_state.White, measured.White, a);
        _state.RedGain = Lerp(_state.RedGain, measured.RedGain, a);
        _state.GreenGain = Lerp(_state.GreenGain, measured.GreenGain, a);
        _state.BlueGain = Lerp(_state.BlueGain, measured.BlueGain, a);

        // Midtone spans orders of magnitude on dark frames, so average it geometrically;
        // a linear average would be dominated by the brightest frames of the night.
        _state.Midtone = Math.Exp(Lerp(Math.Log(Math.Max(_state.Midtone, 1e-6)),
                                       Math.Log(Math.Max(measured.Midtone, 1e-6)), a));
        return _state.Clone();
    }

    private static double Lerp(double from, double to, double a) => from + (to - from) * a;
}
