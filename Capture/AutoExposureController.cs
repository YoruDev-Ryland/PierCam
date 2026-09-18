using System;
using PierCam.Models;

namespace PierCam.Capture;

/// <summary>The exposure and gain to use for the next frame.</summary>
internal readonly record struct ExposurePoint(double Seconds, int Gain)
{
    /// <summary>
    /// Total light gathered, in arbitrary units, so exposure and gain can be traded against
    /// each other on one axis. ZWO gain is in 0.1 dB steps, hence the /200 in the exponent.
    /// </summary>
    public double Sensitivity => Seconds * Math.Pow(10.0, Gain / 200.0);
}

/// <summary>
/// Bounds for one exposure ramp. Two profiles exist: the recording ramp, which moves slowly so
/// the finished video does not step in brightness, and the preview ramp, which is allowed to
/// move fast because nobody is going to watch the live view back frame by frame.
/// </summary>
internal readonly record struct ExposureLimits(
    double Target,
    double MinSeconds,
    double MaxSeconds,
    int MinGain,
    int MaxGain,
    double MaxStep,
    bool FastRecovery)
{
    /// <summary>Recording: never jumps, because a step shows in the finished video.</summary>
    public static ExposureLimits ForRecording(CameraSettings c) => new(
        c.AutoExposureTarget, c.AutoExposureMinSeconds, c.AutoExposureMaxSeconds,
        c.AutoExposureMinGain, c.AutoExposureMaxGain, c.AutoExposureMaxStep, FastRecovery: false);

    /// <summary>
    /// Preview: may jump. Getting a usable picture back quickly matters more than smoothness,
    /// and coming out of full daylight is twenty stops — gentle steps would take a minute.
    /// </summary>
    public static ExposureLimits ForPreview(CameraSettings c) => new(
        c.PreviewTarget, c.PreviewMinSeconds, c.PreviewMaxSeconds,
        c.PreviewMinGain, c.PreviewMaxGain, c.PreviewMaxStep, FastRecovery: true);
}

/// <summary>
/// Ramps exposure and gain to hold the sky at a target brightness.
///
/// Built for the twilight problem rather than for general auto-exposure: a fixed exposure that
/// is right at midnight is completely saturated by sunrise, and one that is right at midnight
/// shows nothing but black at noon. The controller moves by a bounded fraction each frame,
/// because during a recording a sudden exposure change shows up as a visible brightness step,
/// which is far more objectionable than being half a stop off for a few frames.
///
/// Exposure is preferred over gain — longer subs are cleaner — so gain is only raised once
/// exposure has reached its ceiling, and is lowered first on the way back down.
/// </summary>
internal sealed class AutoExposureController
{
    /// <summary>Last measured sky level as a fraction of full scale, for display.</summary>
    public double LastMeasured { get; private set; }

    /// <summary>True when the controller is pinned at a limit and still cannot reach target.</summary>
    public bool AtLimit { get; private set; }

    public void Reset() => LastMeasured = 0;

    /// <summary>
    /// Computes the settings for the next frame from the sky level just measured.
    /// </summary>
    /// <param name="measuredMedian">Sky median, 0..1 of full scale.</param>
    /// <param name="clippedFraction">Share of pixels at full scale, used to detect saturation.</param>
    /// <param name="current">Settings the measured frame was taken with.</param>
    /// <param name="limits">Which ramp profile to apply.</param>
    public ExposurePoint Next(double measuredMedian, double clippedFraction,
        ExposurePoint current, ExposureLimits limits)
    {
        LastMeasured = measuredMedian;

        var target = Math.Clamp(limits.Target, 0.01, 0.6);
        var minExp = Math.Max(0.00005, limits.MinSeconds);
        var maxExp = Math.Max(minExp, limits.MaxSeconds);
        var minGain = Math.Max(0, limits.MinGain);
        var maxGain = Math.Max(minGain, limits.MaxGain);
        var maxStep = Math.Clamp(limits.MaxStep, 0.01, 64.0);

        // A saturated frame reads a median of ~1.0, which badly understates how far over it is:
        // the true level could be many stops higher and the median cannot tell us. When most of
        // the frame is clipped, assume a large overshoot so the ramp keeps cutting hard every
        // frame instead of creeping down one percent at a time from a pinned reading.
        var measured = Math.Clamp(measuredMedian, 1e-5, 1.0);
        var badlyClipped = clippedFraction > 0.5;

        double ratio;
        if (badlyClipped) ratio = 1.0 / 8.0;
        else if (measuredMedian >= 0.98) ratio = 0.25;
        else ratio = target / measured;

        // The per-step clamp exists to keep tracking smooth. It does not apply to a frame we
        // can prove is massively overexposed, where the only question is how fast we can get
        // back to a picture — overshooting into darkness is recoverable, staying blind is not.
        var floor = badlyClipped && limits.FastRecovery ? 1.0 / 16.0 : 1.0 / (1.0 + maxStep);
        ratio = Math.Clamp(ratio, floor, 1.0 + maxStep);

        var desired = current.Sensitivity * ratio;
        if (Math.Abs(ratio - 1.0) < 0.005) { AtLimit = false; return current; }

        // Spend the change on exposure first, then on gain.
        var gainFactor = Math.Pow(10.0, current.Gain / 200.0);
        var wantedExposure = desired / gainFactor;

        double newExposure;
        var newGain = current.Gain;
        var brightening = ratio > 1.0;

        if (wantedExposure >= minExp && wantedExposure <= maxExp)
        {
            newExposure = wantedExposure;
        }
        else if (wantedExposure > maxExp)
        {
            // Need more light than exposure alone can give: cap exposure and raise gain.
            newExposure = maxExp;
            newGain = Math.Clamp(QuantiseGain(desired / maxExp, brightening), minGain, maxGain);
        }
        else
        {
            // Too much light: drop gain before shortening the exposure any further.
            newExposure = minExp;
            newGain = Math.Clamp(QuantiseGain(desired / minExp, brightening), minGain, maxGain);

            // If gain has bottomed out, put whatever is left back into exposure so we do not
            // sit at the floor when a longer sub would be cleaner.
            if (newGain == minGain)
                newExposure = Math.Clamp(desired / Math.Pow(10.0, newGain / 200.0), minExp, maxExp);
        }

        var result = new ExposurePoint(Math.Clamp(newExposure, minExp, maxExp),
            Math.Clamp(newGain, minGain, maxGain));

        AtLimit = Math.Abs(result.Sensitivity - desired) / desired > 0.2;
        return result;
    }

    /// <summary>
    /// Converts a linear sensitivity factor to ZWO's integer gain units (0.1 dB each), rounding
    /// towards the current setting.
    ///
    /// Rounding to nearest would let the quantisation push the frame-to-frame change past its
    /// cap — one gain unit is about 1.2%, so a 12% cap could land at 12.2%. Rounding down when
    /// brightening and up when darkening keeps the cap a genuine bound. One gain unit is far
    /// finer than any usable cap, so this cannot stall the ramp.
    /// </summary>
    private static int QuantiseGain(double linearFactor, bool brightening)
    {
        var exact = 200.0 * Math.Log10(Math.Max(linearFactor, 1e-6));
        return (int)(brightening ? Math.Floor(exact) : Math.Ceiling(exact));
    }
}
