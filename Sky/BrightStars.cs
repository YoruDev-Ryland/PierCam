using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PierCam.Sky;

internal readonly record struct CatalogueStar(int Hr, double RaJ2000, double DecJ2000, double Mag);

/// <summary>
/// The ~1,600 stars brighter than magnitude 5, from the Yale Bright Star Catalogue, embedded in
/// the executable (44 KB). Loaded only when a calibration actually runs, and not held otherwise.
/// </summary>
internal static class BrightStars
{
    public static List<CatalogueStar> Load()
    {
        var list = new List<CatalogueStar>(1700);
        using var stream = typeof(BrightStars).Assembly.GetManifestResourceStream("PierCam.Sky.bright-stars.csv")
                           ?? throw new InvalidOperationException("bright-star catalogue missing from the build");
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(',');
            if (p.Length < 4) continue;
            list.Add(new CatalogueStar(
                int.Parse(p[0], CultureInfo.InvariantCulture),
                double.Parse(p[1], CultureInfo.InvariantCulture),
                double.Parse(p[2], CultureInfo.InvariantCulture),
                double.Parse(p[3], CultureInfo.InvariantCulture)));
        }
        return list;
    }
}
