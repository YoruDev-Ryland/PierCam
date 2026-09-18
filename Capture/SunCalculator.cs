using System;

namespace PierCam.Capture;

/// <summary>Twilight definitions, by how far the sun sits below the horizon.</summary>
internal enum TwilightKind
{
    /// <summary>Sun at the horizon. Sunset / sunrise.</summary>
    Sunset = 0,
    /// <summary>-6°. Bright enough to read outside.</summary>
    Civil = 1,
    /// <summary>-12°. Horizon still discernible at sea.</summary>
    Nautical = 2,
    /// <summary>-18°. Full darkness — no solar contribution to the sky.</summary>
    Astronomical = 3
}

/// <summary>A dark window, in local time.</summary>
internal readonly record struct DarkWindow(DateTime Start, DateTime End)
{
    public TimeSpan Length => End - Start;
    public bool Contains(DateTime t) => t >= Start && t < End;
}

/// <summary>
/// Solar position, and from it the times the sky actually gets dark.
///
/// Implements the NOAA solar position algorithm, which is accurate to well under a minute for
/// any date this side of a century — far more than a timelapse needs.
///
/// Twilight times are found by sampling the sun's altitude and bisecting the crossing rather
/// than by the usual closed-form hour-angle formula. That form quietly fails whenever the sun
/// never reaches the requested depth — high latitudes in summer, where acos() of an
/// out-of-range value returns NaN and the caller schedules a night that never comes. Sampling
/// simply finds no crossing and says so, which the scheduler can act on.
/// </summary>
internal static class SunCalculator
{
    public static double DegreesBelowHorizon(TwilightKind kind) => kind switch
    {
        TwilightKind.Civil => -6.0,
        TwilightKind.Nautical => -12.0,
        TwilightKind.Astronomical => -18.0,
        _ => -0.833   // Sunset: includes refraction and the sun's semi-diameter.
    };

    /// <summary>Sun altitude in degrees above the horizon, for a UTC instant.</summary>
    public static double Altitude(DateTime utc, double latitude, double longitude)
    {
        var jd = JulianDay(utc);
        var t = (jd - 2451545.0) / 36525.0;

        var l0 = Norm360(280.46646 + t * (36000.76983 + t * 0.0003032));
        var m = Norm360(357.52911 + t * (35999.05029 - 0.0001537 * t));
        var e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);

        var mRad = Rad(m);
        var c = Math.Sin(mRad) * (1.914602 - t * (0.004817 + 0.000014 * t))
                + Math.Sin(2 * mRad) * (0.019993 - 0.000101 * t)
                + Math.Sin(3 * mRad) * 0.000289;

        var trueLong = l0 + c;
        var omega = 125.04 - 1934.136 * t;
        var lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(Rad(omega));

        var epsilon0 = 23.0 + (26.0 + 21.448 / 60.0) / 60.0
                       - t * (46.815 + t * (0.00059 - t * 0.001813)) / 3600.0;
        var epsilon = epsilon0 + 0.00256 * Math.Cos(Rad(omega));

        var declination = Math.Asin(Math.Sin(Rad(epsilon)) * Math.Sin(Rad(lambda)));

        // Equation of time, in minutes.
        var y = Math.Tan(Rad(epsilon) / 2.0);
        y *= y;
        var eot = 4.0 * Deg(
            y * Math.Sin(2 * Rad(l0))
            - 2 * e * Math.Sin(mRad)
            + 4 * e * y * Math.Sin(mRad) * Math.Cos(2 * Rad(l0))
            - 0.5 * y * y * Math.Sin(4 * Rad(l0))
            - 1.25 * e * e * Math.Sin(2 * mRad));

        var minutesUtc = utc.TimeOfDay.TotalMinutes;
        var trueSolarMinutes = minutesUtc + eot + 4.0 * longitude;
        var hourAngle = trueSolarMinutes / 4.0 - 180.0;

        var latRad = Rad(latitude);
        var sinAlt = Math.Sin(latRad) * Math.Sin(declination)
                     + Math.Cos(latRad) * Math.Cos(declination) * Math.Cos(Rad(hourAngle));

        return Deg(Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)));
    }

    /// <summary>
    /// The dark window for the night that *begins* on <paramref name="localDate"/>.
    ///
    /// Searches from local noon that day to local noon the next, so a window crossing midnight
    /// is found as one interval rather than two fragments. Returns null when the sun never gets
    /// that far down — the caller should then fall back to fixed times rather than assume
    /// darkness.
    /// </summary>
    public static DarkWindow? NightOf(DateTime localDate, double latitude, double longitude,
        TwilightKind kind)
    {
        var threshold = DegreesBelowHorizon(kind);
        var from = localDate.Date.AddHours(12);
        var to = from.AddDays(1);

        // Polar night: already dark at the start of the search, so there is no dusk to find.
        // Without this the scan finds no descending crossing and reports "never dark", which is
        // precisely backwards — it is dark for the entire twenty-four hours.
        var startAlt = Altitude(from.ToUniversalTime(), latitude, longitude);
        var dusk = startAlt <= threshold
            ? from
            : FindCrossing(from, to, latitude, longitude, threshold, descending: true);
        if (dusk is null) return null;

        // Likewise no dawn means it stays dark to the end of the search window.
        var dawn = FindCrossing(dusk.Value, to, latitude, longitude, threshold, descending: false)
                   ?? to;

        return new DarkWindow(dusk.Value, dawn);
    }

    /// <summary>Which night a moment belongs to: after local noon it is tonight, before it is last night.</summary>
    public static DateTime NightDateFor(DateTime local) =>
        local.TimeOfDay.TotalHours >= 12 ? local.Date : local.Date.AddDays(-1);

    /// <summary>
    /// Finds where the altitude crosses <paramref name="threshold"/>, by coarse scan then
    /// bisection. Five-minute steps cannot miss a crossing: the sun's altitude changes by at
    /// most a few tenths of a degree in that time at any latitude.
    /// </summary>
    private static DateTime? FindCrossing(DateTime fromLocal, DateTime toLocal,
        double latitude, double longitude, double threshold, bool descending)
    {
        var step = TimeSpan.FromMinutes(5);
        var prev = fromLocal;
        var prevAlt = Altitude(prev.ToUniversalTime(), latitude, longitude);

        for (var t = fromLocal + step; t <= toLocal; t += step)
        {
            var alt = Altitude(t.ToUniversalTime(), latitude, longitude);
            var crossed = descending
                ? prevAlt > threshold && alt <= threshold
                : prevAlt < threshold && alt >= threshold;

            if (crossed) return Bisect(prev, t, latitude, longitude, threshold, descending);

            prev = t;
            prevAlt = alt;
        }
        return null;
    }

    private static DateTime Bisect(DateTime lo, DateTime hi, double latitude, double longitude,
        double threshold, bool descending)
    {
        // ~20 halvings takes a 5-minute bracket below a second.
        for (var i = 0; i < 20; i++)
        {
            var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
            var alt = Altitude(mid.ToUniversalTime(), latitude, longitude);
            var stillBefore = descending ? alt > threshold : alt < threshold;
            if (stillBefore) lo = mid; else hi = mid;
        }
        return lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
    }

    private static double JulianDay(DateTime utc)
    {
        int year = utc.Year, month = utc.Month;
        double day = utc.Day + utc.TimeOfDay.TotalDays;

        if (month <= 2) { year -= 1; month += 12; }

        var a = year / 100;
        var b = 2 - a + a / 4;

        return Math.Floor(365.25 * (year + 4716))
               + Math.Floor(30.6001 * (month + 1))
               + day + b - 1524.5;
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    private static double Norm360(double deg)
    {
        deg %= 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }
}
