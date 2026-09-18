using System;
using PierCam.Video;

namespace PierCam.Models;

/// <summary>
/// Works out what a night's settings will actually produce.
///
/// This exists because the interval choice is not obvious: one frame every 4 minutes over a
/// ten-hour night is only 150 frames, which at 30 fps is a five-second video. Showing the frame
/// count, the resulting video length and the file size next to the interval picker makes that
/// trade-off visible before you commit a night to it, rather than after.
/// </summary>
internal readonly record struct CapturePlan(
    TimeSpan NightLength,
    double CadenceSeconds,
    int Frames,
    TimeSpan VideoLength,
    long EstimatedVideoBytes,
    long EstimatedRawBytes)
{
    /// <summary>Sensor readout plus the SDK round trip between subs. Measured, not guessed at.</summary>
    private const double ReadoutOverheadSeconds = 1.0;

    /// <summary>
    /// Measured x264 bitrates in Mbps at 1080p, CRF 23, for real ASI662MC night frames.
    ///
    /// These are measurements, not estimates. A night sky is far *more* expensive to encode than
    /// intuition suggests: sensor noise is different in every frame, so the encoder can predict
    /// none of it and spends most of its bits there. That is also why denoising helps so much.
    /// </summary>
    private static double ReferenceMbps(DenoiseLevel denoise) => denoise switch
    {
        DenoiseLevel.Off => 30.8,
        DenoiseLevel.Light => 29.1,
        DenoiseLevel.Medium => 24.0,
        _ => 17.4
    };

    private const int ReferenceCrf = 23;

    /// <summary>
    /// CRF steps for the bitrate to halve. The usual rule of thumb is 6, but that assumes clean
    /// footage; on noise-dominated frames the curve is much steeper, and 2.1 matches measurement.
    /// </summary>
    private const double CrfHalvingSteps = 2.1;

    /// <summary>
    /// Downscaling saves more than the pixel count suggests, because the scaler averages noise
    /// away as well as detail. Fitted to a 720p measurement.
    /// </summary>
    internal const double ResolutionExponent = 1.5;

    private const double ReferencePixels = 1920.0 * 1080.0;

    public static CapturePlan Compute(TimeSpan nightLength, double exposureSeconds, int intervalSeconds,
        int fps, int crf, int outputWidth, int outputHeight, int sensorWidth, int sensorHeight,
        DenoiseLevel denoise = DenoiseLevel.Medium)
    {
        var minimum = exposureSeconds + ReadoutOverheadSeconds;
        var cadence = intervalSeconds > 0 ? Math.Max(intervalSeconds, minimum) : minimum;

        var frames = (int)Math.Max(0, Math.Floor(nightLength.TotalSeconds / cadence));
        var videoSeconds = fps > 0 ? frames / (double)fps : 0;

        var pixelRatio = outputWidth * (double)outputHeight / ReferencePixels;
        var bitrate = ReferenceMbps(denoise) * 1e6
                      * Math.Pow(2.0, (ReferenceCrf - crf) / CrfHalvingSteps)
                      * Math.Pow(pixelRatio, ResolutionExponent);
        var videoBytes = (long)(bitrate * videoSeconds / 8.0);

        // What the same frames would cost written out individually as 16-bit images, which is
        // what SharpCap does and why a night there runs to several gigabytes.
        var rawBytes = frames * (long)sensorWidth * sensorHeight * 2;

        return new CapturePlan(nightLength, cadence, frames,
            TimeSpan.FromSeconds(videoSeconds), videoBytes, rawBytes);
    }
}
