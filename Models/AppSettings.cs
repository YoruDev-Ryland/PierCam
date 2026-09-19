using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PierCam.Capture;
using PierCam.Imaging;
using PierCam.Video;

namespace PierCam.Models;

internal sealed class CameraSettings
{
    /// <summary>Exposure used for scheduled captures.</summary>
    public double ExposureSeconds { get; set; } = 15.0;
    public int Gain { get; set; } = 300;
    public int Offset { get; set; } = 15;
    public int WhiteBalanceRed { get; set; } = 55;
    public int WhiteBalanceBlue { get; set; } = 75;

    /// <summary>Starting point for the live view when no session is running.</summary>
    public double PreviewExposureSeconds { get; set; } = 2.0;
    public int PreviewGain { get; set; } = 400;

    /// <summary>
    /// Let the live view find its own exposure.
    ///
    /// On by default, and it needs to be: an all-sky camera under an open roof goes from full
    /// daylight to a moonless sky, a range of many thousands to one. Any fixed preview exposure
    /// is blown out for half of that and black for the other half. With this off, a preview that
    /// looks like flat grey almost always means the frame is saturated.
    /// </summary>
    public bool PreviewAutoExposure { get; set; } = true;

    /// <summary>
    /// Preview ramp bounds. Deliberately much wider and faster than the recording ramp: the
    /// live view has to cope with daylight, and nobody watches the preview back frame by frame,
    /// so it may jump rather than glide.
    /// </summary>
    public double PreviewTarget { get; set; } = 0.22;
    public double PreviewMinSeconds { get; set; } = 0.0002;
    public double PreviewMaxSeconds { get; set; } = 8.0;
    public int PreviewMinGain { get; set; }
    public int PreviewMaxGain { get; set; } = 450;

    /// <summary>Largest change per adjustment. Recovers from full saturation in a few steps.</summary>
    public double PreviewMaxStep { get; set; } = 0.9;

    /// <summary>
    /// Cap on live-view frame rate.
    ///
    /// Once auto-exposure shortens the sub to a few milliseconds the camera will happily deliver
    /// 40 fps, and debayering 1080p that often burns CPU for a view of a sky that changes over
    /// minutes. Ten a second is more than enough to look live.
    /// </summary>
    public double PreviewMaxFps { get; set; } = 10.0;

    /// <summary>
    /// Ramp exposure and gain to follow the sky through twilight.
    ///
    /// A fixed exposure is correct for the dark hours but saturates completely once dawn starts:
    /// measured on the 15 Sept capture, frames after ~06:30 were 100% clipped. Turn this on for
    /// dusk-to-dawn runs; leave it off if you want every frame taken under identical settings.
    /// </summary>
    public bool AutoExposure { get; set; }

    /// <summary>Target median sky level, as a fraction of full scale. Headroom for stars above it.</summary>
    public double AutoExposureTarget { get; set; } = 0.15;

    public double AutoExposureMinSeconds { get; set; } = 0.05;
    public double AutoExposureMaxSeconds { get; set; } = 15.0;
    public int AutoExposureMinGain { get; set; }
    public int AutoExposureMaxGain { get; set; } = 400;

    /// <summary>
    /// Largest fractional change to exposure×gain allowed between consecutive frames. Small
    /// values keep twilight transitions smooth; large ones chase the light but make the video
    /// step in brightness.
    /// </summary>
    public double AutoExposureMaxStep { get; set; } = 0.12;

    /// <summary>0 = use the full sensor.</summary>
    public int RoiWidth { get; set; }
    public int RoiHeight { get; set; }
    public int Binning { get; set; } = 1;
}

internal sealed class StretchSettings
{
    public StretchMode Mode { get; set; } = StretchMode.Smoothed;
    public double TargetBackground { get; set; } = 0.18;
    public double ShadowClip { get; set; } = 2.8;

    /// <summary>How far below the sky the black point sits, as a fraction of the sky level.</summary>
    public double ShadowDepth { get; set; } = 0.30;
    public bool NeutraliseBackground { get; set; } = true;
    public double SmoothingFrames { get; set; } = 20;

    // Used when Mode == Manual.
    public double ManualBlack { get; set; }
    public double ManualMidtone { get; set; } = 0.02;
    public double ManualWhite { get; set; } = 1.0;
}

internal sealed class VideoSettings
{
    /// <summary>Playback rate of the finished timelapse.</summary>
    public int Fps { get; set; } = 30;

    /// <summary>
    /// x264 constant quality. Lower is better. 26 is the default because measurement on real
    /// frames showed CRF 20 costs nine times as much for no visible gain on a noisy night sky.
    /// </summary>
    public int Crf { get; set; } = 26;

    /// <summary>
    /// Denoising before encoding. Medium roughly halves the file and, on noisy sub-exposures,
    /// looks better than the original rather than worse.
    /// </summary>
    public DenoiseLevel Denoise { get; set; } = DenoiseLevel.Medium;

    public string Preset { get; set; } = "medium";
    public int OutputWidth { get; set; } = 1920;
    public int OutputHeight { get; set; } = 1080;
    public bool BurnTimestamp { get; set; } = true;
    public int TimestampScale { get; set; } = 3;

    /// <summary>Remembered choice in the library's downscale tool.</summary>
    public int DownscaleWidth { get; set; } = 960;
    public int DownscaleHeight { get; set; } = 540;

    /// <summary>Quality for the downscale pass. Slightly tighter than capture: fewer pixels hide more.</summary>
    public int DownscaleCrf { get; set; } = 24;
}

internal sealed class SessionSettings
{
    /// <summary>
    /// Seconds between the start of one capture and the next. 0 means continuous — expose,
    /// read out, expose again.
    ///
    /// One minute over a ten-hour night is 600 frames, or 20 seconds of video at 30 fps.
    /// Note that a gap between exposures makes the sky step rather than flow: at 15 s exposures
    /// on a 60 s interval the shutter is open a quarter of the time, so star motion arrives in
    /// visible jumps. Continuous is smoother; the interval is about pacing, not disk space.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    public ScheduleMode Schedule { get; set; } = ScheduleMode.Manual;

    /// <summary>Local time of day to begin, when Schedule is Nightly.</summary>
    public TimeSpan StartTime { get; set; } = new(20, 30, 0);
    public TimeSpan EndTime { get; set; } = new(5, 30, 0);
    public bool RepeatNightly { get; set; } = true;

    /// <summary>Also keep every raw frame on disk. Off by default — this is the 9 GB-a-night option.</summary>
    public bool KeepRawFrames { get; set; }

    /// <summary>Stop the session if free disk space drops below this.</summary>
    public double MinFreeDiskGb { get; set; } = 5.0;

    /// <summary>How dark it must be before a scheduled session starts.</summary>
    public TwilightKind Twilight { get; set; } = TwilightKind.Astronomical;

    /// <summary>
    /// Only record while the observatory roof is open.
    ///
    /// With this on, a night where the roof never opens produces no video at all, and a roof
    /// that opens at 02:00 starts the session then. A roof that shuts mid-session holds it
    /// rather than ending it, so the night stays a single video with a gap in it.
    /// </summary>
    public bool RequireRoofOpen { get; set; }

    /// <summary>Path to the roof status file. At SFRO this is on the site share, per building.</summary>
    public string RoofStatusPath { get; set; } = string.Empty;

    public int RoofPollSeconds { get; set; } = 30;

    /// <summary>
    /// Age past which the status file is no longer believed at all.
    ///
    /// Deliberately long. SFRO writes these files when a roof *changes*, not on a heartbeat —
    /// all fourteen buildings share a timestamp to the second and only the one that moved
    /// carries a later one. A roof that sits open all night therefore has an hours-old file,
    /// and a short threshold here would refuse to record precisely on the good nights. This is
    /// the "something is genuinely broken" horizon, not a freshness check.
    /// </summary>
    public int RoofAbandonMinutes { get; set; } = 720;

    /// <summary>
    /// Whether an unreadable or stale roof file should permit recording. Off by default: a
    /// dead share most often means nobody knows where the roof is, and filming a closed roof
    /// all night is worse than missing a night.
    /// </summary>
    public bool RecordWhenRoofUnknown { get; set; }

    /// <summary>
    /// How long to keep filming after the roof stops permitting it.
    ///
    /// The roof file is written once the roof has *finished* moving, so stopping the instant it
    /// reads CLOSED cuts the night off just after the interesting part and the closure never
    /// makes it into the video. Running on for a few minutes catches the roof actually shutting.
    /// Set to 0 to stop the moment the gate shuts.
    /// </summary>
    public int RoofLingerMinutes { get; set; } = 5;
}

/// <summary>
/// Whether PierCam launches with Windows, and how.
///
/// The registry Run key is the record of truth, not this: someone can remove the entry from Task
/// Manager's startup tab at any time and never come back here. <see cref="Startup.IsRegistered"/>
/// reads the key, and the checkbox is set from that rather than from the saved flag.
/// </summary>
internal sealed class StartupSettings
{
    public bool RunAtLogin { get; set; }

    /// <summary>Start minimised, for an unattended machine that boots into the observatory.</summary>
    public bool StartMinimised { get; set; }
}

/// <summary>
/// Where the window was last time. Saved as the *restore* bounds, so a maximised window reopens
/// maximised but un-maximises to somewhere sensible rather than filling the screen forever.
/// </summary>
internal sealed class WindowPlacement
{
    // Zero means "never saved", not NaN. NaN was the obvious sentinel and a bad one: a settings
    // object carrying it cannot be serialised at all. On a fresh install nothing has closed yet
    // to fill these in, so every settings save threw until the first clean exit wrote real
    // bounds - which is why restarting appeared to fix it. A saved window is always wider and
    // taller than 100, so the size alone says whether this was ever set.
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximised { get; set; }

    [JsonIgnore]
    public bool IsSet => Width > 100 && Height > 100;
}

/// <summary>
/// Tidying that happens on its own. Off by default: it rewrites finished videos, and nothing
/// should quietly re-encode a night's work because a default said so.
/// </summary>
internal sealed class HousekeepingSettings
{
    public bool AutoDownscale { get; set; }

    /// <summary>Age past which a timelapse is downscaled, in days.</summary>
    public int AfterDays { get; set; } = 30;

    public int TargetWidth { get; set; } = 1280;
    public int TargetHeight { get; set; } = 720;

    /// <summary>Last time the sweep ran, so it runs once a day rather than on every launch.</summary>
    public DateTime? LastRun { get; set; }
}

/// <summary>
/// The marker showing where the telescope is pointing. Off by default: it needs N.I.N.A. and a
/// calibration, and a feature that has neither must not appear to do anything.
/// </summary>
internal sealed class TargetMarkerSettings
{
    public bool Enabled { get; set; }

    /// <summary>Draw it into the recorded video too, not only the live view.</summary>
    public bool BurnIntoRecordings { get; set; }

    /// <summary>
    /// With burn-in on, record the night twice: the normal video stays clean and a second copy
    /// carries the marker. Both are encoded from the same frames as they are captured.
    /// </summary>
    public bool KeepUnmarkedOriginal { get; set; }

    /// <summary>Where N.I.N.A.'s Advanced API plugin listens. Only the address is configurable.</summary>
    public string NinaApiUrl { get; set; } = "http://localhost:1888";

    public LensCalibrationRecord? Calibration { get; set; }
}

/// <summary>What the automatic calibration found, and where it came from.</summary>
internal sealed class LensCalibrationRecord
{
    public PierCam.Sky.LensModel Lens { get; set; } = new();
    public DateTime CalibratedUtc { get; set; }

    /// <summary>What it was calibrated from, for the status line: "Fri 18 Sep night" or "tonight's sky".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>A second night the result was checked against, if there was one.</summary>
    public string? ConfirmedOn { get; set; }

    /// <summary>The camera it belongs to. Another camera, or this one on another frame size, needs its own.</summary>
    public string CameraSerial { get; set; } = string.Empty;
    public int Stars { get; set; }
    public double RmsPx { get; set; }
}

internal enum LibraryView
{
    /// <summary>Fanned horizontal stack, one night in focus.</summary>
    Carousel,
    /// <summary>Everything at once, wrapped.</summary>
    Grid
}

internal enum ScheduleMode
{
    /// <summary>Only records when you press Record.</summary>
    Manual,
    /// <summary>Records between StartTime and EndTime, every night.</summary>
    Nightly,
    /// <summary>Records for real darkness at your site, which shifts with the seasons on its own.</summary>
    Astronomical
}

/// <summary>Where the observatory is. Needed only for twilight times.</summary>
internal sealed class SiteSettings
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double ElevationMetres { get; set; }

    /// <summary>Free-text, purely for display.</summary>
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsSet => Math.Abs(Latitude) > 0.0001 || Math.Abs(Longitude) > 0.0001;
}

internal sealed class AppSettings
{
    public string LibraryRoot { get; set; } = DefaultLibraryRoot();
    public string? FfmpegPath { get; set; }
    public int? LastCameraId { get; set; }

    /// <summary>Palette id — see ThemeManager.Themes. Unknown values fall back to "dark".</summary>
    public string ThemeId { get; set; } = "dark";

    /// <summary>Superseded by <see cref="ThemeId"/>; still read once so old configs migrate.</summary>
    public bool NightMode { get; set; }

    /// <summary>Carousel or grid in the library.</summary>
    public LibraryView LibraryView { get; set; } = LibraryView.Carousel;

    /// <summary>
    /// Master switch for decorative motion. Turning it off leaves every function intact and
    /// is the first thing to try if a slower machine feels sluggish.
    /// </summary>
    public bool Animations { get; set; } = true;

    /// <summary>Frame decorations over the live image — registration marks and scanline.</summary>
    public bool ViewportChrome { get; set; } = true;

    public CameraSettings Camera { get; set; } = new();
    public StretchSettings Stretch { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
    public SiteSettings Site { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public WindowPlacement Placement { get; set; } = new();
    public HousekeepingSettings Housekeeping { get; set; } = new();
    public TargetMarkerSettings TargetMarker { get; set; } = new();

    public static string DefaultLibraryRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PierCam");

    [JsonIgnore]
    public string TimelapseRoot => Path.Combine(LibraryRoot, "Timelapses");

    // ---- persistence ---------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // By default a single NaN or infinity anywhere in the object makes Serialize throw, and
        // every later save throws with it. Written as "NaN" and read back instead, one bad number
        // costs one setting rather than the ability to save any setting at all.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PierCam", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Json);
                if (loaded is not null)
                {
                    // Migrate configs written before themes were named.
                    if (loaded.NightMode && loaded.ThemeId == "dark") loaded.ThemeId = "night";
                    loaded.NightMode = false;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file must never stop the app from opening.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Write-then-rename so a crash mid-save cannot leave a truncated settings file.
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
