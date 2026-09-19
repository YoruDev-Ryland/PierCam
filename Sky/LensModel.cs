using System;
using System.Text.Json.Serialization;

namespace PierCam.Sky;

/// <summary>
/// How a lens maps angle off its axis to radius on the sensor. The fisheye families cover
/// virtually every all-sky and pier camera lens sold; rectilinear covers ordinary wide-angle
/// lenses, which cannot reach 90 degrees off axis at all.
/// </summary>
internal enum LensProjection { Equidistant, Equisolid, Stereographic, Orthographic, Rectilinear }

/// <summary>
/// A fixed camera's view of the sky: which pixel any altitude/azimuth lands on.
///
/// The camera looks along (AxisAlt, AxisAz), turned by Roll about that axis. Angle off axis maps
/// to radius through the projection, times (1 + K1 theta^2) for whatever distortion the ideal
/// projection misses, about the principal point (Cx, Cy). Parity is -1 for a mirrored image.
/// Width and Height are the frame size the numbers belong to.
///
/// Every field is finite by construction - this is persisted in settings.json, and a single NaN
/// there once made every settings save in the app fail.
/// </summary>
internal sealed class LensModel
{
    public LensProjection Projection { get; set; }
    public double AxisAlt { get; set; }
    public double AxisAz { get; set; }
    public double Roll { get; set; }
    public double F { get; set; }
    public double K1 { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public int Parity { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }

    public static double MaxTheta(LensProjection p) => p switch
    {
        LensProjection.Orthographic => 88 * SkyMath.D2R,
        LensProjection.Rectilinear => 80 * SkyMath.D2R,
        _ => 115 * SkyMath.D2R,
    };

    public static double Radial(LensProjection p, double theta) => p switch
    {
        LensProjection.Equidistant => theta,
        LensProjection.Equisolid => 2 * Math.Sin(theta / 2),
        LensProjection.Stereographic => 2 * Math.Tan(theta / 2),
        LensProjection.Orthographic => Math.Sin(theta),
        _ => Math.Tan(theta),
    };

    /// <summary>
    /// Off-axis angle and position angle of a world direction for a camera with no roll.
    /// The in-plane axis is horizontal and square to the viewing azimuth, which keeps this
    /// continuous for every axis direction including straight up.
    /// </summary>
    public static void Angles(double axisAlt, double axisAz, double e, double n, double u, out double theta, out double phi)
    {
        double a = axisAlt * SkyMath.D2R, z = axisAz * SkyMath.D2R;
        double ze = Math.Cos(a) * Math.Sin(z), zn = Math.Cos(a) * Math.Cos(z), zu = Math.Sin(a);
        double xe = Math.Cos(z), xn = -Math.Sin(z);
        double ye = zu * Math.Sin(z), yn = zu * Math.Cos(z), yu = -ze * Math.Sin(z) - zn * Math.Cos(z);
        var cz = e * ze + n * zn + u * zu;
        var cx = e * xe + n * xn;
        var cy = e * ye + n * yn + u * yu;
        theta = Math.Atan2(Math.Sqrt(cx * cx + cy * cy), cz);
        phi = Math.Atan2(cy, cx);
    }

    /// <summary>
    /// Where (alt, az) lands on the sensor. False when the direction is outside what this lens
    /// model can see at all; the point may still fall outside the frame when true.
    /// </summary>
    public bool TryProject(double altDeg, double azDeg, out double x, out double y)
    {
        var (e, n, u) = SkyMath.Enu(altDeg, azDeg);
        Angles(AxisAlt, AxisAz, e, n, u, out var th, out var phi);
        var ok = th <= MaxTheta(Projection);
        var r = F * Radial(Projection, Math.Min(th, MaxTheta(Projection))) * (1 + K1 * th * th);
        var ang = phi - Roll * SkyMath.D2R;
        x = Cx + r * Math.Cos(ang);
        y = Cy + Parity * r * Math.Sin(ang);
        return ok;
    }

    /// <summary>
    /// The same calibration for a frame of a different size - a downscaled recording, or a
    /// binned sensor. Null when the aspect ratio differs, which means a different crop of the
    /// sensor and not a rescale of the same image.
    /// </summary>
    public LensModel? ScaledTo(int width, int height)
    {
        if (width == Width && height == Height) return this;
        if (Width <= 0 || Height <= 0 || width <= 0 || height <= 0) return null;
        if (Math.Abs((double)width / height - (double)Width / Height) > 0.01) return null;
        var s = (double)width / Width;
        var m = Clone();
        m.F *= s; m.Cx = (Cx + 0.5) * s - 0.5; m.Cy = (Cy + 0.5) * s - 0.5;
        m.Width = width; m.Height = height;
        return m;
    }

    [JsonIgnore]
    public bool IsUsable =>
        double.IsFinite(AxisAlt) && double.IsFinite(AxisAz) && double.IsFinite(Roll) && double.IsFinite(F) &&
        double.IsFinite(K1) && double.IsFinite(Cx) && double.IsFinite(Cy) && F > 1 && Width > 0 && Height > 0;

    public LensModel Clone() => (LensModel)MemberwiseClone();

    internal double[] Pack() => new[] { AxisAlt, AxisAz, Roll, F, K1, Cx, Cy };

    internal void Unpack(double[] p)
    {
        AxisAlt = p[0]; AxisAz = p[1]; Roll = p[2]; F = p[3]; K1 = p[4]; Cx = p[5]; Cy = p[6];
    }

    /// <summary>Puts the angles back in their usual ranges after optimisation wanders past them.</summary>
    internal void Normalise()
    {
        if (AxisAlt > 90) { AxisAlt = 180 - AxisAlt; AxisAz += 180; Roll += 180; }
        AxisAz = SkyMath.Wrap360(AxisAz);
        Roll = SkyMath.Wrap360(Roll);
    }

    public override string ToString() =>
        $"{Projection} f {F:0.0}px/rad k1 {K1:+0.0000;-0.0000}, axis alt {AxisAlt:0.0} az {AxisAz:0.0} roll {Roll:0.0}, " +
        $"centre ({Cx:0.0},{Cy:0.0}) of {Width}x{Height}{(Parity < 0 ? ", mirrored" : "")}";
}
