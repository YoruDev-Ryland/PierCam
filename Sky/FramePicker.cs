using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PierCam.Sky;

/// <summary>
/// Chooses which frames of a recorded night to calibrate from.
///
/// Spreading the picks evenly across the dark hours is not enough: with the moon up, nearly every
/// frame is washed out. On 28 Sept 2026 the moon rose twenty minutes after astronomical dark,
/// eight of nine evenly spread frames were moonlit, and the night failed - where its first twenty
/// minutes alone solve in under a minute. So the night is surveyed first, and only frames whose
/// sky is about as dark as the darkest are used. Darkest, because moonlight and cloud both
/// brighten the sky; showing stars, because a frame taken with the roof shut is darkest of all.
/// When the dark stretch is short, the survey's few good frames are filled out with their
/// neighbours.
/// </summary>
internal static class FramePicker
{
    /// <param name="dark">Frame numbers taken with the sun well below the horizon, in order.</param>
    /// <param name="look">Reads one frame: its stars and its sky level, or null if it could not be read.</param>
    public static List<(int n, List<DetectedStar> stars)> Pick(IReadOnlyList<int> dark, int want,
        Func<int, (List<DetectedStar> stars, int sky)?> look, CancellationToken ct)
    {
        var seen = new Dictionary<int, (List<DetectedStar> stars, int sky)>();
        void Read(int n)
        {
            if (seen.ContainsKey(n)) return;
            ct.ThrowIfCancellationRequested();
            if (look(n) is { } r && r.stars.Count >= 40) seen[n] = r;
            else seen[n] = (new List<DetectedStar>(), int.MaxValue);
        }

        // The survey: three times as many frames as wanted, spread evenly.
        var survey = want * 3;
        foreach (var n in Enumerable.Range(0, survey)
                     .Select(i => dark[(int)Math.Round((dark.Count - 1) * (survey == 1 ? 0.5 : i / (double)(survey - 1)))])
                     .Distinct())
            Read(n);

        var usable = seen.Where(s => s.Value.sky != int.MaxValue).ToList();
        if (usable.Count == 0) return new();
        var darkest = usable.Min(s => s.Value.sky);
        var limit = darkest + Math.Max(10, darkest / 5);
        bool Good(int n) => seen[n].sky <= limit;

        // Too few good frames: the dark stretch fell between survey points. Its neighbours, out
        // to half the survey's spacing, are as likely to be good as it is.
        var good = seen.Keys.Where(Good).OrderBy(n => n).ToList();
        if (good.Count < want)
        {
            var reach = Math.Max(1, dark.Count / survey / 2);
            var index = dark.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i);
            foreach (var g in good.OrderBy(n => seen[n].sky).ToList())
            for (var step = 1; step <= reach && seen.Keys.Count(Good) < want; step++)
            foreach (var side in new[] { -1, 1 })
            {
                var i = index[g] + side * step;
                if (i >= 0 && i < dark.Count) Read(dark[i]);
            }
            good = seen.Keys.Where(Good).OrderBy(n => n).ToList();
        }

        // Spread across whatever good stretch there is: frames far apart in time constrain the
        // solve best, and let fixed lights be told from stars.
        var picks = good.Count <= want
            ? good
            : Enumerable.Range(0, want).Select(i => good[(int)Math.Round((good.Count - 1) * (want == 1 ? 0.5 : i / (double)(want - 1)))]).Distinct().ToList();
        return picks.Select(n => (n, seen[n].stars)).ToList();
    }

    /// <summary>
    /// How bright a frame's sky is. Not the median: equipment, the wall and the lens surround can
    /// fill most of the frame, so the middle value measures them. The sky is the brightest large
    /// part of a night frame, so the 80th percentile lands in it.
    /// </summary>
    public static int SkyLevel(byte[] g)
    {
        var hist = new int[256];
        foreach (var v in g) hist[v]++;
        for (int i = 0, sum = 0; i < 256; i++)
            if ((sum += hist[i]) * 5 >= g.Length * 4) return i;
        return 255;
    }
}
