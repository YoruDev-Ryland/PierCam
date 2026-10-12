using System;

namespace PierCam.Sky;

/// <summary>
/// Where the planets and the Moon are, well enough to label them on an all-sky camera.
///
/// Planets: the Keplerian elements and rates of JPL's "Approximate Positions of the Planets"
/// (Standish, valid 1800-2050), good to an arcminute or so for the bright planets - a fraction of
/// a pixel at an all-sky scale. Moon: the Astronomical Almanac's low-precision series, about 0.3°,
/// with its parallax taken off for the observer, because from the ground the Moon sits up to a
/// degree lower than from the Earth's centre.
/// </summary>
internal static class SolarSystem
{
    public enum Body { Mercury, Venus, Mars, Jupiter, Saturn, Uranus, Neptune }

    // a, e, I, L, long. perihelion, long. node - and their rates per Julian century from J2000.
    private static readonly double[,] Elements =
    {
        { 0.38709927, 0.20563593, 7.00497902, 252.25032350, 77.45779628, 48.33076593 },
        { 0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718, 76.67984255 },
        { 1.52371034, 0.09339410, 1.84969142, -4.55343205, -23.94362959, 49.55953891 },
        { 5.20288700, 0.04838624, 1.30439695, 34.39644051, 14.72847983, 100.47390909 },
        { 9.53667594, 0.05386179, 2.48599187, 49.95424423, 92.59887831, 113.66242448 },
        { 19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630, 74.01692503 },
        { 30.06992276, 0.00859048, 1.77004347, -55.12002969, 44.96476227, 131.78422574 },
    };

    private static readonly double[,] Rates =
    {
        { 0.00000037, 0.00001906, -0.00594749, 149472.67411175, 0.16047689, -0.12534081 },
        { 0.00000390, -0.00004107, -0.00078890, 58517.81538729, 0.00268329, -0.27769418 },
        { 0.00001847, 0.00007882, -0.00813131, 19140.30268499, 0.44441088, -0.29257343 },
        { -0.00011607, -0.00013253, -0.00183714, 3034.74612775, 0.21252668, 0.20469106 },
        { -0.00125060, -0.00050991, 0.00193609, 1222.49362201, -0.41897216, -0.28867794 },
        { -0.00196176, -0.00004397, -0.00242939, 428.48202785, 0.40805281, 0.04240589 },
        { 0.00026291, 0.00005105, 0.00035372, 218.45945325, -0.32241464, -0.00508664 },
    };

    private static readonly double[] EarthElements = { 1.00000261, 0.01671123, -0.00001531, 100.46457166, 102.93768193, 0.0 };
    private static readonly double[] EarthRates = { 0.00000562, -0.00004392, -0.01294668, 35999.37244981, 0.32327364, 0.0 };

    private const double D2R = Math.PI / 180, R2D = 180 / Math.PI;
    private const double ObliquityJ2000 = 23.43928;

    private static double Centuries(DateTime utc) => (SkyMath.JulianDate(utc) - 2451545.0) / 36525.0;

    /// <summary>Geocentric RA/Dec of a planet, J2000 degrees (precess with <see cref="SkyMath.PrecessFromJ2000"/>).</summary>
    public static (double ra, double dec) Planet(Body body, DateTime utc)
    {
        var t = Centuries(utc);
        var i = (int)body;
        var p = Heliocentric(t, k => Elements[i, k], k => Rates[i, k]);
        var e = Heliocentric(t, k => EarthElements[k], k => EarthRates[k]);
        return EclipticToEquatorial(p.x - e.x, p.y - e.y, p.z - e.z, ObliquityJ2000);
    }

    /// <summary>
    /// Topocentric altitude/azimuth of the Moon's centre, refraction not applied. Computed in
    /// coordinates of date, so no precession is needed afterwards.
    /// </summary>
    public static (double alt, double az) MoonAltAz(DateTime utc, double latDeg, double lonDeg)
    {
        var t = Centuries(utc);
        double S(double a, double b) => Math.Sin((a + b * t) * D2R);
        double C(double a, double b) => Math.Cos((a + b * t) * D2R);

        var lambda = 218.32 + 481267.881 * t + 6.29 * S(135.0, 477198.87) - 1.27 * S(259.3, -413335.36)
                     + 0.66 * S(235.7, 890534.22) + 0.21 * S(269.9, 954397.74) - 0.19 * S(357.5, 35999.05)
                     - 0.11 * S(186.5, 966404.03);
        var beta = 5.13 * S(93.3, 483202.02) + 0.28 * S(228.2, 960400.89) - 0.28 * S(318.3, 6003.15)
                   - 0.17 * S(217.6, -407332.21);
        var parallax = 0.9508 + 0.0518 * C(135.0, 477198.87) + 0.0095 * C(259.3, -413335.36)
                       + 0.0078 * C(235.7, 890534.22) + 0.0028 * C(269.9, 954397.74);

        var x = Math.Cos(beta * D2R) * Math.Cos(lambda * D2R);
        var y = Math.Cos(beta * D2R) * Math.Sin(lambda * D2R);
        var z = Math.Sin(beta * D2R);
        var (ra, dec) = EclipticToEquatorial(x, y, z, ObliquityJ2000 - 0.013 * t);
        var (alt, az) = SkyMath.ToAltAz(ra, dec, utc, latDeg, lonDeg);
        // Seen from the surface rather than the centre: lower by the parallax, most at the horizon.
        return (alt - parallax * Math.Cos(alt * D2R), az);
    }

    private static (double x, double y, double z) Heliocentric(double t, Func<int, double> el, Func<int, double> rate)
    {
        double V(int k) => el(k) + rate(k) * t;
        double a = V(0), e = V(1), inc = V(2) * D2R, l = V(3), peri = V(4), node = V(5);
        var w = (peri - node) * D2R;
        var m = SkyMath.Wrap360(l - peri) * D2R;
        if (m > Math.PI) m -= 2 * Math.PI;

        var ecc = m + e * Math.Sin(m);                         // Kepler's equation, by Newton
        for (var k = 0; k < 8; k++)
        {
            var d = (ecc - e * Math.Sin(ecc) - m) / (1 - e * Math.Cos(ecc));
            ecc -= d;
            if (Math.Abs(d) < 1e-10) break;
        }

        double xp = a * (Math.Cos(ecc) - e), yp = a * Math.Sqrt(1 - e * e) * Math.Sin(ecc);
        double cw = Math.Cos(w), sw = Math.Sin(w), cn = Math.Cos(node * D2R), sn = Math.Sin(node * D2R), ci = Math.Cos(inc), si = Math.Sin(inc);
        return ((cw * cn - sw * sn * ci) * xp + (-sw * cn - cw * sn * ci) * yp,
                (cw * sn + sw * cn * ci) * xp + (-sw * sn + cw * cn * ci) * yp,
                sw * si * xp + cw * si * yp);
    }

    private static (double ra, double dec) EclipticToEquatorial(double x, double y, double z, double obliquityDeg)
    {
        var eps = obliquityDeg * D2R;
        var xq = x;
        var yq = y * Math.Cos(eps) - z * Math.Sin(eps);
        var zq = y * Math.Sin(eps) + z * Math.Cos(eps);
        return (SkyMath.Wrap360(Math.Atan2(yq, xq) * R2D), Math.Atan2(zq, Math.Sqrt(xq * xq + yq * yq)) * R2D);
    }
}
