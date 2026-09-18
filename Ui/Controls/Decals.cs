using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PierCam.Ui.Controls;

/// <summary>
/// The connector vocabulary: a run of straight segments joined at 45°, with a terminator at one
/// end. This is the single most characteristic mark in the reference language — a label tethered
/// to the thing it names by a line that only ever travels at 0°, 45° or 90°.
///
/// Every decal in this file renders in one OnRender pass with frozen pens and no effects, so
/// scattering dozens of them across the interface costs nothing to composite.
/// </summary>
public enum Terminator { None, Dot, Ring, Square, Tick }

public class Connector : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Connector),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Connector),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Length of the flat run before the diagonal, as a fraction of width.</summary>
    public static readonly DependencyProperty LeadProperty = DependencyProperty.Register(
        nameof(Lead), typeof(double), typeof(Connector),
        new FrameworkPropertyMetadata(0.45, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Mirrors the elbow so the diagonal rises instead of falls.</summary>
    public static readonly DependencyProperty RiseProperty = DependencyProperty.Register(
        nameof(Rise), typeof(bool), typeof(Connector),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Runs the line right-to-left, for decals on the left of what they label.</summary>
    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(Connector),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartCapProperty = DependencyProperty.Register(
        nameof(StartCap), typeof(Terminator), typeof(Connector),
        new FrameworkPropertyMetadata(Terminator.Dot, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndCapProperty = DependencyProperty.Register(
        nameof(EndCap), typeof(Terminator), typeof(Connector),
        new FrameworkPropertyMetadata(Terminator.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double Lead { get => (double)GetValue(LeadProperty); set => SetValue(LeadProperty, value); }
    public bool Rise { get => (bool)GetValue(RiseProperty); set => SetValue(RiseProperty, value); }
    public bool Mirror { get => (bool)GetValue(MirrorProperty); set => SetValue(MirrorProperty, value); }
    public Terminator StartCap { get => (Terminator)GetValue(StartCapProperty); set => SetValue(StartCapProperty, value); }
    public Terminator EndCap { get => (Terminator)GetValue(EndCapProperty); set => SetValue(EndCapProperty, value); }

    public Connector() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w < 4 || h < 2) return;

        var pen = new Pen(Stroke, Thickness);
        pen.Freeze();

        // The diagonal is always 45°, so its horizontal run equals the vertical drop.
        var drop = Math.Max(0, h - Thickness);
        var lead = Math.Clamp(Lead, 0, 1) * Math.Max(0, w - drop);

        var yStart = Rise ? h - Thickness / 2 : Thickness / 2;
        var yEnd = Rise ? Thickness / 2 : h - Thickness / 2;

        var a = new Point(0, yStart);
        var b = new Point(lead, yStart);
        var c = new Point(lead + drop, yEnd);
        var d = new Point(w, yEnd);

        if (Mirror)
        {
            a.X = w - a.X; b.X = w - b.X; c.X = w - c.X; d.X = w - d.X;
        }

        dc.DrawLine(pen, a, b);
        dc.DrawLine(pen, b, c);
        dc.DrawLine(pen, c, d);

        DrawCap(dc, StartCap, a, Stroke, Thickness);
        DrawCap(dc, EndCap, d, Stroke, Thickness);
    }

    internal static void DrawCap(DrawingContext dc, Terminator cap, Point at, Brush brush, double thickness)
    {
        switch (cap)
        {
            case Terminator.Dot:
                dc.DrawEllipse(brush, null, at, 2.6, 2.6);
                break;
            case Terminator.Ring:
            {
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                dc.DrawEllipse(null, pen, at, 3.4, 3.4);
                break;
            }
            case Terminator.Square:
                dc.DrawRectangle(brush, null, new Rect(at.X - 2.6, at.Y - 2.6, 5.2, 5.2));
                break;
            case Terminator.Tick:
            {
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                dc.DrawLine(pen, new Point(at.X, at.Y - 4), new Point(at.X, at.Y + 4));
                break;
            }
        }
    }
}

/// <summary>
/// A cluster of vertical bars of varying weight — the "barcode" that fills dead space on
/// technical panels. Deterministic from a seed so it never flickers between frames.
/// </summary>
public class BarCode : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BarCode),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(BarCode),
        new FrameworkPropertyMetadata(7, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(int), typeof(BarCode),
        new FrameworkPropertyMetadata(14, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public int Bars { get => (int)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }

    public BarCode() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0 || Bars <= 0) return;

        var rng = new Random(Seed);
        var x = 0.0;
        for (var i = 0; i < Bars && x < w; i++)
        {
            var bar = rng.Next(0, 3) == 0 ? 3.0 : 1.0;
            if (x + bar > w) break;
            dc.DrawRectangle(Fill, null, new Rect(x, 0, bar, h));
            x += bar + (rng.Next(0, 4) == 0 ? 4.0 : 2.0);
        }
    }
}

/// <summary>
/// A dotted or dashed arc. The long sweeping arc is the element that ties a HUD together —
/// it implies a much larger instrument continuing off-screen.
/// </summary>
public class DotArc : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(DotArc),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(DotArc),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Degrees, clockwise, zero pointing right.</summary>
    public static readonly DependencyProperty StartAngleProperty = DependencyProperty.Register(
        nameof(StartAngle), typeof(double), typeof(DotArc),
        new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        nameof(Sweep), typeof(double), typeof(DotArc),
        new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gap between dots in degrees. Zero draws a continuous arc.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(DotArc),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotRadiusProperty = DependencyProperty.Register(
        nameof(DotRadius), typeof(double), typeof(DotArc),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double StartAngle { get => (double)GetValue(StartAngleProperty); set => SetValue(StartAngleProperty, value); }
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double DotRadius { get => (double)GetValue(DotRadiusProperty); set => SetValue(DotRadiusProperty, value); }

    public DotArc() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 2 || h <= 2) return;

        var cx = w / 2.0;
        var cy = h / 2.0;
        var rx = w / 2.0 - DotRadius;
        var ry = h / 2.0 - DotRadius;

        if (Step <= 0.01)
        {
            var pen = new Pen(Stroke, Thickness);
            pen.Freeze();
            var start = Polar(cx, cy, rx, ry, StartAngle);
            var end = Polar(cx, cy, rx, ry, StartAngle + Sweep);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(rx, ry), 0, Math.Abs(Sweep) > 180,
                    Sweep >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            return;
        }

        var steps = (int)Math.Abs(Sweep / Step);
        for (var i = 0; i <= steps; i++)
        {
            var a = StartAngle + Math.Sign(Sweep) * i * Step;
            dc.DrawEllipse(Stroke, null, Polar(cx, cy, rx, ry, a), DotRadius, DotRadius);
        }
    }

    private static Point Polar(double cx, double cy, double rx, double ry, double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        return new Point(cx + rx * Math.Cos(r), cy + ry * Math.Sin(r));
    }
}

/// <summary>
/// A parallelogram tag — the slanted chip that labels a section. The slant is what keeps a row
/// of labels from reading as a row of buttons.
/// </summary>
public class SlantTag : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SlantTag),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Horizontal offset of the top edge relative to the bottom. Negative leans the other way.</summary>
    public static readonly DependencyProperty SlantProperty = DependencyProperty.Register(
        nameof(Slant), typeof(double), typeof(SlantTag),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Slant { get => (double)GetValue(SlantProperty); set => SetValue(SlantProperty, value); }

    public SlantTag() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        var s = Math.Clamp(Slant, -w / 2, w / 2);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(Math.Max(0, s), 0), true, true);
            ctx.LineTo(new Point(w, 0), true, false);
            ctx.LineTo(new Point(w - Math.Max(0, s), h), true, false);
            ctx.LineTo(new Point(0, h), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(Fill, null, g);
    }
}

/// <summary>
/// A stepped rule: a horizontal line that jogs once at 45° to a new height, ending in a
/// terminator. Used to underline headings and to tie a plate's title to its edge.
/// </summary>
public class StepRule : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(StepRule),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(StepRule),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndCapProperty = DependencyProperty.Register(
        nameof(EndCap), typeof(Terminator), typeof(StepRule),
        new FrameworkPropertyMetadata(Terminator.None, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the jog happens, as a fraction of the width.</summary>
    public static readonly DependencyProperty StepAtProperty = DependencyProperty.Register(
        nameof(StepAt), typeof(double), typeof(StepRule),
        new FrameworkPropertyMetadata(0.7, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Terminator EndCap { get => (Terminator)GetValue(EndCapProperty); set => SetValue(EndCapProperty, value); }
    public double StepAt { get => (double)GetValue(StepAtProperty); set => SetValue(StepAtProperty, value); }

    public StepRule() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w < 6 || h < 2) return;

        var pen = new Pen(Stroke, Thickness);
        pen.Freeze();

        var drop = Math.Max(0, h - Thickness);
        var at = Math.Clamp(StepAt, 0, 1) * Math.Max(0, w - drop);

        var yTop = Thickness / 2;
        var yBottom = h - Thickness / 2;

        dc.DrawLine(pen, new Point(0, yBottom), new Point(at, yBottom));
        dc.DrawLine(pen, new Point(at, yBottom), new Point(at + drop, yTop));
        dc.DrawLine(pen, new Point(at + drop, yTop), new Point(w, yTop));

        Connector.DrawCap(dc, EndCap, new Point(w, yTop), Stroke, Thickness);
    }
}

/// <summary>
/// A grid of small squares, some filled — the "data" block from the reference sheets. Purely
/// decorative texture, deterministic from its seed.
/// </summary>
public class DataGrid : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(DataGrid),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CellProperty = DependencyProperty.Register(
        nameof(Cell), typeof(double), typeof(DataGrid),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(DataGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Cell { get => (double)GetValue(CellProperty); set => SetValue(CellProperty, value); }
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }

    public DataGrid() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0 || Cell <= 1) return;

        var rng = new Random(Seed);
        var gap = Cell * 0.6;
        for (var y = 0.0; y + Cell <= h; y += Cell + gap)
        for (var x = 0.0; x + Cell <= w; x += Cell + gap)
        {
            var roll = rng.Next(0, 10);
            if (roll < 4) continue;
            if (roll < 8) dc.DrawRectangle(Fill, null, new Rect(x, y, Cell, Cell));
            else
            {
                var pen = new Pen(Fill, 1);
                pen.Freeze();
                dc.DrawRectangle(null, pen, new Rect(x + 0.5, y + 0.5, Cell - 1, Cell - 1));
            }
        }
    }
}
