using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Models;

namespace PierCam.Library;

/// <summary>A resolution a timelapse can be reduced to.</summary>
internal sealed record DownscaleTarget(string Label, int Width, int Height)
{
    public override string ToString() => Label;

    /// <summary>Share of the original 1080p pixel count, used for the size estimate.</summary>
    public double PixelFractionOf(int sourceWidth, int sourceHeight) =>
        sourceWidth <= 0 || sourceHeight <= 0
            ? 1.0
            : Width * (double)Height / (sourceWidth * (double)sourceHeight);
}

internal sealed class TranscodeProgress
{
    public int ItemIndex { get; init; }
    public int ItemCount { get; init; }
    public string Title { get; init; } = string.Empty;
    public double Fraction { get; init; }
    public bool Finished { get; init; }
    public string? Error { get; init; }
    public long BytesSaved { get; init; }
}

/// <summary>
/// Re-encodes finished timelapses at a lower resolution, in place.
///
/// This is a second pass over already-compressed video, so it is not free of generation loss —
/// but downscaling averages several source pixels into one, which hides most of it and removes
/// a lot of the sensor noise that made the original expensive in the first place. The saving is
/// much larger than the pixel ratio alone suggests for exactly that reason.
/// </summary>
internal sealed class VideoTranscoder
{
    private readonly string _ffmpegPath;

    public VideoTranscoder(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>
    /// Percentages are measured file sizes from a real 1080p night, not pixel ratios — a quarter
    /// of the pixels comes out at about an eighth of the size, because downscaling averages away
    /// most of the sensor noise that was costing the bitrate.
    /// </summary>
    public static readonly DownscaleTarget[] Targets =
    {
        new("1280 × 720 — HD, about 30% the size", 1280, 720),
        new("960 × 540 — quarter resolution, about 13%", 960, 540),
        new("854 × 480 — 480p, about 9%", 854, 480),
        new("640 × 360 — about 4%", 640, 360),
    };

    /// <summary>
    /// Downscales each item in turn. Reports progress per item, and never destroys the original
    /// until the replacement has been written and verified.
    /// </summary>
    public async Task RunAsync(IReadOnlyList<TimelapseItem> items, DownscaleTarget target,
        int crf, IProgress<TranscodeProgress> progress, CancellationToken ct)
    {
        long totalSaved = 0;

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var m = item.Manifest;

            void Report(double fraction, string? error = null) => progress.Report(new TranscodeProgress
            {
                ItemIndex = i,
                ItemCount = items.Count,
                Title = item.Title,
                Fraction = fraction,
                Error = error,
                BytesSaved = totalSaved
            });

            Report(0);

            if (!File.Exists(m.VideoPath))
            {
                Report(1, "video file missing");
                continue;
            }

            if (m.Width <= target.Width && m.Height <= target.Height)
            {
                Report(1, $"already {m.Width}×{m.Height}");
                continue;
            }

            var before = new FileInfo(m.VideoPath).Length;
            var tempPath = Path.Combine(m.FolderPath, "timelapse.downscale.mp4");

            try
            {
                var ok = await RunFfmpegAsync(m.VideoPath, tempPath, target, crf, m.FrameCount,
                    f => Report(f), ct).ConfigureAwait(false);

                if (!ok || !File.Exists(tempPath) || new FileInfo(tempPath).Length < 1024)
                {
                    TryDelete(tempPath);
                    Report(1, "encode failed — original kept");
                    continue;
                }

                // Only now is it safe to lose the original.
                File.Move(tempPath, m.VideoPath, overwrite: true);

                var after = new FileInfo(m.VideoPath).Length;
                totalSaved += Math.Max(0, before - after);

                if (m.OriginalWidth == 0)
                {
                    m.OriginalWidth = m.Width;
                    m.OriginalHeight = m.Height;
                }
                m.Width = target.Width;
                m.Height = target.Height;
                m.VideoBytes = after;
                m.Save(m.FolderPath);

                Report(1);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
            {
                TryDelete(tempPath);
                Report(1, ex.Message);
            }
        }

        progress.Report(new TranscodeProgress
        {
            ItemIndex = items.Count,
            ItemCount = items.Count,
            Fraction = 1,
            Finished = true,
            BytesSaved = totalSaved
        });
    }

    private async Task<bool> RunFfmpegAsync(string input, string output, DownscaleTarget target,
        int crf, int expectedFrames, Action<double> onProgress, CancellationToken ct)
    {
        // -progress pipe:1 gives machine-readable status on stdout, which is far more reliable
        // to parse than the human-facing stderr line.
        var args = $"-hide_banner -nostdin -loglevel error -y -i \"{input}\" " +
                   $"-vf \"scale={target.Width}:{target.Height}:flags=lanczos\" " +
                   $"-c:v libx264 -preset medium -crf {crf} -pix_fmt yuv420p " +
                   $"-an -movflags +faststart -progress pipe:1 -nostats \"{output}\"";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(_ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || expectedFrames <= 0) return;
            if (!e.Data.StartsWith("frame=", StringComparison.Ordinal)) return;
            if (int.TryParse(e.Data.AsSpan(6), out var frame))
                onProgress(Math.Clamp(frame / (double)expectedFrames, 0, 1));
        };

        if (!proc.Start()) return false;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return proc.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
