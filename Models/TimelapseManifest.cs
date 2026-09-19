using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PierCam.Models;

internal enum SessionStatus { Recording, Complete, Interrupted, Failed }

/// <summary>
/// The session.json written beside each timelapse video.
///
/// The library is rebuilt purely by scanning for these, so there is no database to corrupt and
/// you can move, copy or delete a session folder with Explorer and the app just agrees.
/// </summary>
internal sealed class TimelapseManifest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }

    public DateTime StartedLocal { get; set; }
    public DateTime? EndedLocal { get; set; }

    public int FrameCount { get; set; }
    public int DroppedFrames { get; set; }

    /// <summary>Minutes the session spent paused — typically with the roof shut.</summary>
    public int HeldMinutes { get; set; }
    public int Fps { get; set; } = 30;
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Resolution before any downscale pass; 0 means the video is still as recorded.</summary>
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }

    [JsonIgnore] public bool WasDownscaled => OriginalWidth > 0 && OriginalWidth != Width;

    public double ExposureSeconds { get; set; }
    public int Gain { get; set; }
    public int IntervalSeconds { get; set; }
    public string CameraName { get; set; } = string.Empty;
    public string? CameraSerial { get; set; }
    public double? SensorTempStartC { get; set; }
    public double? SensorTempEndC { get; set; }

    public string VideoFile { get; set; } = "timelapse.mp4";
    public string PosterFile { get; set; } = "poster.jpg";
    public SessionStatus Status { get; set; } = SessionStatus.Recording;
    public string? Message { get; set; }

    /// <summary>Set once the session finishes, so the library does not stat the file every refresh.</summary>
    public long VideoBytes { get; set; }

    /// <summary>What the same frames would have cost as SharpCap-style PNGs.</summary>
    public long EstimatedRawBytes { get; set; }

    [JsonIgnore] public string FolderPath { get; set; } = string.Empty;
    [JsonIgnore] public string VideoPath => Path.Combine(FolderPath, VideoFile);
    [JsonIgnore] public string PosterPath => Path.Combine(FolderPath, PosterFile);

    /// <summary>Real-world time the timelapse covers.</summary>
    [JsonIgnore]
    public TimeSpan CapturedSpan => (EndedLocal ?? DateTime.Now) - StartedLocal;

    /// <summary>Length of the finished video.</summary>
    [JsonIgnore]
    public TimeSpan VideoDuration => Fps > 0 ? TimeSpan.FromSeconds(FrameCount / (double)Fps) : TimeSpan.Zero;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // This is written while a night is recording, and Save has nothing around it to catch a
        // failure. A non-finite exposure or temperature must not be able to throw out of the
        // capture path, so it is written as "NaN" rather than refused.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public const string FileName = "session.json";

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
        FolderPath = folder;
    }

    public static TimelapseManifest? TryLoad(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<TimelapseManifest>(File.ReadAllText(path), Json);
            if (m is null) return null;
            m.FolderPath = folder;
            return m;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
