using System;

namespace PierCam.Sky;

/// <summary>
/// The small amount of positional astronomy the target marker needs: sidereal time, precession
/// and the equatorial-to-horizontal conversion. Accurate to well under an arcminute over this
/// century, which is a small fraction of a pixel on any all-sky lens.
/// </summary>
internal static class SkyMath
{
    public const double D2R = Math.PI / 180, R2D = 180 / Math.PI;

    public static double JulianDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToOADate() + 2415018.5;

    /// <summary>Greenwich mean sidereal time, degrees.</summary>
    public static double Gmst(DateTime utc)
    {
        var jd = JulianDate(utc); var t = (jd - 2451545.0) / 36525;
        var g = 280.46061837 + 360.98564736629 * (jd - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000;
        return Wrap360(g);
    }

    /// <summary>IAU 1976 precession, J2000 to the mean equinox of <paramref name="utc"/>. Degrees.</summary>
    public static (double ra, double dec) PrecessFromJ2000(double ra0, double dec0, DateTime utc)
    {
        var t = (JulianDate(utc) - 2451545.0) / 36525;
        var zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) / 3600 * D2R;
        var z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) / 3600 * D2R;
        var th = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) / 3600 * D2R;
        double a = ra0 * D2R, d = dec0 * D2R;
        var A = Math.Cos(d) * Math.Sin(a + zeta);
        var B = Math.Cos(th) * Math.Cos(d) * Math.Cos(a + zeta) - Math.Sin(th) * Math.Sin(d);
        var C = Math.Sin(th) * Math.Cos(d) * Math.Cos(a + zeta) + Math.Cos(th) * Math.Sin(d);
        return (Wrap360((Math.Atan2(A, B) + z) * R2D), Math.Asin(Math.Clamp(C, -1, 1)) * R2D);
    }

    /// <summary>
    /// Equatorial coordinates of date to horizontal: true altitude (no refraction) and azimuth
    /// from north through east, degrees.
    /// </summary>
    public static (double alt, double az) ToAltAz(double raDeg, double decDeg, DateTime utc, double latDeg, double lonDeg)
    {
        var h = (Gmst(utc) + lonDeg - raDeg) * D2R;
        double d = decDeg * D2R, p = latDeg * D2R;
        var e = -Math.Cos(d) * Math.Sin(h);
        var n = Math.Sin(d) * Math.Cos(p) - Math.Cos(d) * Math.Cos(h) * Math.Sin(p);
        var u = Math.Sin(d) * Math.Sin(p) + Math.Cos(d) * Math.Cos(h) * Math.Cos(p);
        return (Math.Asin(Math.Clamp(u, -1, 1)) * R2D, Wrap360(Math.Atan2(e, n) * R2D));
    }

    /// <summary>Atmospheric refraction for a true altitude, degrees (Saemundsson). Zero well below the horizon.</summary>
    public static double Refraction(double altTrue) =>
        altTrue < -2 ? 0 : 1.02 / Math.Tan((altTrue + 10.3 / (altTrue + 5.11)) * D2R) / 60;

    /// <summary>Unit vector of a horizontal direction in east-north-up coordinates.</summary>
    public static (double e, double n, double u) Enu(double altDeg, double azDeg)
    {
        double a = altDeg * D2R, z = azDeg * D2R;
        return (Math.Cos(a) * Math.Sin(z), Math.Cos(a) * Math.Cos(z), Math.Sin(a));
    }

    public static double Wrap360(double deg) => ((deg % 360) + 360) % 360;
}
