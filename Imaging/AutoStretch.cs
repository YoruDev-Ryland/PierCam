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
        // The sensor's own size: this reads the raw Bayer frame, which is never turned — the
        // turn happens on the way out, and a rotated width here would walk off the end of it.
        _width = fp.SensorWidth;
        _height = fp.SensorHeight;
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
            // Make the sky neutral, and the dark floor with it. Measured on the frame's own sky
            // and dark parts (see Levels); the frame-wide medians above stay as they were for the
            // brightness, which is what auto-exposure and the stretch have always been tuned on.
            var c = _imgType == AsiImgType.Raw16
                ? Levels(MemoryMarshal.Cast<byte, ushort>(raw[..(_width * _height * 2)]), sigma)
                : Levels8(raw, sigma);
            static double N(double v) => v / (Bins - 1.0);
            if (c.HasFloor)
            {
                // Two levels, two unknowns per channel: the gain carries red's and blue's
                // sky-above-floor onto green's, and the lift puts their floor where green's is.
                var gSpan = N(c.SkyG) - N(c.FloorG);
                p.RedGain = SafeGain(gSpan, N(c.SkyR) - N(c.FloorR));
                p.BlueGain = SafeGain(gSpan, N(c.SkyB) - N(c.FloorB));
                p.RedLift = (N(c.FloorG) - black) - (N(c.FloorR) - black) * p.RedGain;
                p.BlueLift = (N(c.FloorG) - black) - (N(c.FloorB) - black) * p.BlueGain;
            }
            else
            {
                // Nothing dark to anchor to: scale R and B so the background sits on green's.
                p.RedGain = SafeGain(N(c.SkyG) - black, N(c.SkyR) - black);
                p.BlueGain = SafeGain(N(c.SkyG) - black, N(c.SkyB) - black);
            }
        }
        return p;
    }

    // ── the sky's own colour, and the dark floor's ────────────────────────────────────────
    //
    // Neutralising used the whole frame's medians, which is the sky only while the sky fills the
    // frame. An all-sky lens on a square sensor leaves most of the frame to the dark surround,
    // the wall and the equipment, and those set the median: on an ASI676MC the correction
    // balanced the surround - already neutral - and left a moonless sky visibly green (stretched,
    // sky ~93/110/84 against a surround of ~33/32/37). Under the moon or in twilight the sky is
    // bright and near neutral anyway, which is why only the dark hours showed it.
    //
    // Measuring the sky alone fixed the sky, but a gain per channel can only make one level
    // neutral: the boost that greyed the sky pushed the dark walls and surround to magenta
    // (~44/34/53). So both are measured, and two levels pinned: blocks brighter than halfway
    // between the frame's 10th and 90th percentile block brightness are sky, the rest are the
    // floor. A frame with no such contrast - fog, a closed roof, a sky that fills the sensor - is
    // measured whole, as before. Sampled more sparsely than the main pass: a median needs far
    // fewer samples than the noise estimate does.

    private readonly record struct ColourLevels(double SkyR, double SkyG, double SkyB,
        double FloorR, double FloorG, double FloorB, bool HasFloor);

    private const int SkyStep = 4;
    private readonly int[] _lumHist = new int[Bins];
    private readonly int[] _skyR = new int[Bins];
    private readonly int[] _skyG = new int[Bins];
    private readonly int[] _skyB = new int[Bins];
    private readonly int[] _floorR = new int[Bins];
    private readonly int[] _floorG = new int[Bins];
    private readonly int[] _floorB = new int[Bins];

    private ColourLevels Levels(ReadOnlySpan<ushort> src, double sigma)
    {
        Array.Clear(_lumHist);
        for (var by = 0; by + 1 < _height; by += 2 * SkyStep)
        for (var bx = 0; bx + 1 < _width; bx += 2 * SkyStep)
        {
            int i = by * _width + bx, j = i + _width;
            _lumHist[(src[i] + src[i + 1] + src[j] + src[j + 1]) >> 2]++;
        }
        var cut = SkyCut(sigma);

        ClearLevels();
        var pat = _pattern;
        for (var by = 0; by + 1 < _height; by += 2 * SkyStep)
        for (var bx = 0; bx + 1 < _width; bx += 2 * SkyStep)
        {
            int i = by * _width + bx, j = i + _width;
            var sky = (src[i] + src[i + 1] + src[j] + src[j + 1]) >> 2 >= cut;
            BumpLevel(pat[0], src[i], sky); BumpLevel(pat[1], src[i + 1], sky);
            BumpLevel(pat[2], src[j], sky); BumpLevel(pat[3], src[j + 1], sky);
        }
        return Medians(cut > 0);
    }

    private ColourLevels Levels8(ReadOnlySpan<byte> src, double sigma)
    {
        Array.Clear(_lumHist);
        for (var by = 0; by + 1 < _height; by += 2 * SkyStep)
        for (var bx = 0; bx + 1 < _width; bx += 2 * SkyStep)
        {
            int i = by * _width + bx, j = i + _width;
            _lumHist[(src[i] + src[i + 1] + src[j] + src[j + 1]) << 6]++;
        }
        var cut = SkyCut(sigma);

        ClearLevels();
        var pat = _pattern;
        for (var by = 0; by + 1 < _height; by += 2 * SkyStep)
        for (var bx = 0; bx + 1 < _width; bx += 2 * SkyStep)
        {
            int i = by * _width + bx, j = i + _width;
            var sky = (src[i] + src[i + 1] + src[j] + src[j + 1]) << 6 >= cut;
            BumpLevel(pat[0], (ushort)(src[i] << 8), sky); BumpLevel(pat[1], (ushort)(src[i + 1] << 8), sky);
            BumpLevel(pat[2], (ushort)(src[j] << 8), sky); BumpLevel(pat[3], (ushort)(src[j + 1] << 8), sky);
        }
        return Medians(cut > 0);
    }

    private ColourLevels Medians(bool split) => new(
        Percentile(_skyR, 0.5), Percentile(_skyG, 0.5), Percentile(_skyB, 0.5),
        Percentile(_floorR, 0.5), Percentile(_floorG, 0.5), Percentile(_floorB, 0.5), split);

    /// <summary>The block brightness above which a block counts as sky; 0 measures the whole frame.</summary>
    private int SkyCut(double sigma)
    {
        var lo = Percentile(_lumHist, 0.10);
        var hi = Percentile(_lumHist, 0.90);
        return hi - lo < 4 * sigma ? 0 : (int)(lo + 0.5 * (hi - lo));
    }

    private void ClearLevels()
    {
        Array.Clear(_skyR); Array.Clear(_skyG); Array.Clear(_skyB);
        Array.Clear(_floorR); Array.Clear(_floorG); Array.Clear(_floorB);
    }

    private void BumpLevel(byte colour, ushort v, bool sky)
    {
        switch (colour)
        {
            case 0: (sky ? _skyR : _floorR)[v]++; break;
            case 2: (sky ? _skyB : _floorB)[v]++; break;
            default: (sky ? _skyG : _floorG)[v]++; break;
        }
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
        _state.RedLift = Lerp(_state.RedLift, measured.RedLift, a);
        _state.BlueLift = Lerp(_state.BlueLift, measured.BlueLift, a);

        // Midtone spans orders of magnitude on dark frames, so average it geometrically;
        // a linear average would be dominated by the brightest frames of the night.
        _state.Midtone = Math.Exp(Lerp(Math.Log(Math.Max(_state.Midtone, 1e-6)),
                                       Math.Log(Math.Max(measured.Midtone, 1e-6)), a));
        return _state.Clone();
    }

    private static double Lerp(double from, double to, double a) => from + (to - from) * a;
}
