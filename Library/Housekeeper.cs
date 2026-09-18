using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Models;
using PierCam.Video;

namespace PierCam.Library;

/// <summary>What a sweep did, for the readout and the log.</summary>
internal sealed record HousekeepingResult(int Considered, int Downscaled, long BytesSaved, string? Error)
{
    public static HousekeepingResult Nothing => new(0, 0, 0, null);
}

/// <summary>
/// Reduces old timelapses to a smaller resolution on their own, so a library left alone for a
/// year does not quietly fill the disk.
///
/// Three rules keep this from being the feature that eats someone's data:
///
///   · It is off unless switched on. Re-encoding a finished night is destructive and cannot be
///     undone, so it is never a default.
///   · It never touches anything recent. The age is counted from when the night was recorded,
///     and the point of the feature is that you have stopped looking at it.
///   · It never runs while the camera is recording. Re-encoding uses every core, and a dropped
///     frame is a hole in tonight's video — tonight always outranks last month's disk space.
///
/// It also only ever runs once a day. Downscaling is idempotent — an already-small video is
/// skipped — but a sweep that ran on every launch would re-read the whole library each time.
/// </summary>
internal sealed class Housekeeper
{
    private readonly AppSettings _settings;
    private readonly Func<bool> _isRecording;

    public Housekeeper(AppSettings settings, Func<bool> isRecording)
    {
        _settings = settings;
        _isRecording = isRecording;
    }

    private HousekeepingSettings H => _settings.Housekeeping;

    /// <summary>Whether a sweep is due: enabled, not recording, and not already run today.</summary>
    public bool IsDue(DateTime now) =>
        H.AutoDownscale && !_isRecording() &&
        (H.LastRun is not { } last || last.Date < now.Date);

    /// <summary>
    /// The nights a sweep would act on: old enough, and still larger than the target. Public so
    /// the settings page can say what will happen before it happens.
    /// </summary>
    public IReadOnlyList<TimelapseItem> Due(IEnumerable<TimelapseItem> library, DateTime now)
    {
        var cutoff = now.AddDays(-Math.Max(1, H.AfterDays));
        return library.Where(i =>
                i.Manifest.StartedLocal < cutoff &&
                i.VideoExists &&
                (i.Manifest.Width > H.TargetWidth || i.Manifest.Height > H.TargetHeight))
            .ToList();
    }

    /// <summary>
    /// Runs a sweep. Returns what it did; the caller refreshes the library and records the run.
    /// A failure to find ffmpeg is reported rather than thrown — housekeeping must never be the
    /// reason the app falls over.
    /// </summary>
    public async Task<HousekeepingResult> RunAsync(IReadOnlyList<TimelapseItem> library,
        DateTime now, CancellationToken ct)
    {
        var due = Due(library, now);
        H.LastRun = now;
        if (due.Count == 0) return HousekeepingResult.Nothing;

        if (_isRecording())
            return new HousekeepingResult(due.Count, 0, 0, "skipped — a recording is running");

        var ffmpeg = FfmpegEncoder.Locate(_settings.FfmpegPath);
        if (ffmpeg is null)
            return new HousekeepingResult(due.Count, 0, 0, "ffmpeg was not found");

        var target = new DownscaleTarget($"{H.TargetWidth} × {H.TargetHeight}", H.TargetWidth, H.TargetHeight);
        long saved = 0;
        var done = 0;
        var errors = new List<string>();

        // One transcoder call per item, so each reports its own total — accumulate rather than
        // overwrite, or the result only ever shows what the last night saved.
        var progress = new Progress<TranscodeProgress>(p =>
        {
            if (p.Finished) saved += p.BytesSaved;
            else if (p.Error is not null) errors.Add($"{p.Title}: {p.Error}");
        });

        try
        {
            // One at a time, checking between each: a scheduled recording can start halfway
            // through a sweep, and it must win.
            foreach (var item in due)
            {
                if (ct.IsCancellationRequested || _isRecording()) break;
                await new VideoTranscoder(ffmpeg)
                    .RunAsync(new[] { item }, target, _settings.Video.DownscaleCrf, progress, ct);
                item.NotifyVideoChanged();
                done++;
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever finished stays done; the rest waits for the next sweep.
        }
        catch (Exception ex)
        {
            App.Log(ex, "Housekeeper");
            return new HousekeepingResult(due.Count, done, saved, ex.Message);
        }

        return new HousekeepingResult(due.Count, done,
            saved, errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
