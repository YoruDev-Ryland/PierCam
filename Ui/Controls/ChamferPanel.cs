using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PierCam.Ui.Controls;

/// <summary>An edge of a plate.</summary>
public enum Edge { None, Top, Right, Bottom, Left }

[Flags]
public enum Corner
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,
    All = TopLeft | TopRight | BottomRight | BottomLeft,
    Top = TopLeft | TopRight,
    Bottom = BottomLeft | BottomRight,
    Left = TopLeft | BottomLeft,
    Right = TopRight | BottomRight,
    /// <summary>The diagonal pair used most often — reads as "machined part".</summary>
    Diagonal = TopLeft | BottomRight,
    AntiDiagonal = TopRight | BottomLeft
}

/// <summary>
/// The shape of one edge: an inset before a point, a 45° transition, and an inset after it.
///
/// Written in XAML as <c>"i0>i1@at"</c>. <c>at</c> is measured from the plate's left (top and
/// bottom edges) or from its top (left and right edges); a negative <c>at</c> counts from the far
/// end, which is what you want for a feature that must hug the right or bottom regardless of how
/// wide the plate ends up. <c>"12"</c> alone is a constant inset; empty is a straight edge.
///
/// Two plates mate when one edge's profile is the other's mirror: <c>"90>0@150"</c> on the
/// right edge of a plate and <c>"0>90@150"</c> on the left edge of the plate beside it (drawn
/// 90px into the first plate's column by a negative margin) produce one continuous seam that
/// jogs at 45°. That mirror relationship is the whole point — it is how a cut in one plate
/// becomes a tab on its neighbour instead of a hole.
/// </summary>
public readonly record struct EdgeProfile(double Inset0, double Inset1, double At, bool FromEnd)
{
    public static EdgeProfile Straight => default;

    public bool HasJog => Math.Abs(Inset1 - Inset0) > 0.01;

    /// <summary>Length of the 45° transition along the edge.</summary>
    public double Run => Math.Abs(Inset1 - Inset0);

    public static EdgeProfile Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Straight;
        text = text.Trim();

        var atIdx = text.IndexOf('@');
        var body = atIdx >= 0 ? text[..atIdx] : text;
        var atText = atIdx >= 0 ? text[(atIdx + 1)..] : "0";

        var gt = body.IndexOf('>');
        var i0 = Num(gt >= 0 ? body[..gt] : body);
        var i1 = gt >= 0 ? Num(body[(gt + 1)..]) : i0;
        var at = Num(atText);

        return new EdgeProfile(Math.Max(0, i0), Math.Max(0, i1), Math.Abs(at), at < 0);

        static double Num(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Resolves the jog start in absolute edge coordinates, clamped clear of both ends.</summary>
    public double JogStart(double length, double marginStart, double marginEnd)
    {
        var raw = FromEnd ? length - At - Run : At;
        var lo = marginStart;
        var hi = Math.Max(lo, length - Run - marginEnd);
        return Math.Clamp(raw, lo, hi);
    }
}

/// <summary>
/// A plate with a machined outline: per-corner cuts, per-edge 45° jogs, and an optional keyway,
/// drawn in a single OnRender pass.
///
/// The whole visual language depends on plates that fit together. A plate that butts against
/// another keeps square corners on the shared edge and only cuts the exposed ones; where a seam
/// jogs, both plates carry mirrored profiles so the seam is one continuous line. Rendering is
/// one geometry plus at most two extra fills, no effects, so a screen full of these costs
/// essentially nothing to composite.
/// </summary>
public class ChamferPanel : ContentControl
{
    static ChamferPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ChamferPanel),
            new FrameworkPropertyMetadata(typeof(ChamferPanel)));
    }

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(ChamferPanel),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    // ── corners ─────────────────────────────────────────────────────────────

    public static readonly DependencyProperty ChamferProperty = Reg(nameof(Chamfer), 12.0);
    public static readonly DependencyProperty CornersProperty = Reg(nameof(Corners), Corner.Diagonal);

    /// <summary>
    /// Independent cut length per corner, clockwise from top-left: (TL, TR, BR, BL). One long
    /// diagonal against three square corners reads as a part cut to fit something; four equal
    /// nibbles read as a box with the corners taken off. All-zero falls back to Corners/Chamfer.
    /// </summary>
    public static readonly DependencyProperty CutsProperty = Reg(nameof(Cuts), default(Thickness));

    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Corner Corners { get => (Corner)GetValue(CornersProperty); set => SetValue(CornersProperty, value); }
    public Thickness Cuts { get => (Thickness)GetValue(CutsProperty); set => SetValue(CutsProperty, value); }

    // ── edges ───────────────────────────────────────────────────────────────

    /// <summary>Edge profiles, see <see cref="EdgeProfile"/>. Empty means straight.</summary>
    public static readonly DependencyProperty TopEdgeProperty = Reg(nameof(TopEdge), string.Empty);
    public static readonly DependencyProperty RightEdgeProperty = Reg(nameof(RightEdge), string.Empty);
    public static readonly DependencyProperty BottomEdgeProperty = Reg(nameof(BottomEdge), string.Empty);
    public static readonly DependencyProperty LeftEdgeProperty = Reg(nameof(LeftEdge), string.Empty);

    public string TopEdge { get => (string)GetValue(TopEdgeProperty); set => SetValue(TopEdgeProperty, value); }
    public string RightEdge { get => (string)GetValue(RightEdgeProperty); set => SetValue(RightEdgeProperty, value); }
    public string BottomEdge { get => (string)GetValue(BottomEdgeProperty); set => SetValue(BottomEdgeProperty, value); }
    public string LeftEdge { get => (string)GetValue(LeftEdgeProperty); set => SetValue(LeftEdgeProperty, value); }

    /// <summary>Keyway notch: a rectangular slot cut into one straight edge.</summary>
    public static readonly DependencyProperty NotchProperty = Reg(nameof(Notch), Edge.None);
    public static readonly DependencyProperty NotchWidthProperty = Reg(nameof(NotchWidth), 58.0);
    public static readonly DependencyProperty NotchDepthProperty = Reg(nameof(NotchDepth), 10.0);
    public static readonly DependencyProperty NotchPositionProperty = Reg(nameof(NotchPosition), 0.5);

    public Edge Notch { get => (Edge)GetValue(NotchProperty); set => SetValue(NotchProperty, value); }
    public double NotchWidth { get => (double)GetValue(NotchWidthProperty); set => SetValue(NotchWidthProperty, value); }
    public double NotchDepth { get => (double)GetValue(NotchDepthProperty); set => SetValue(NotchDepthProperty, value); }
    public double NotchPosition { get => (double)GetValue(NotchPositionProperty); set => SetValue(NotchPositionProperty, value); }

    // ── surface ─────────────────────────────────────────────────────────────

    public static readonly DependencyProperty StrokeProperty = Reg<Brush?>(nameof(Stroke), null);
    public static readonly DependencyProperty StrokeThicknessProperty = Reg(nameof(StrokeThickness), 1.0);
    public static readonly DependencyProperty SheenProperty = Reg<Brush?>(nameof(Sheen), null);
    public static readonly DependencyProperty GrainProperty = Reg<Brush?>(nameof(Grain), null);
    public static readonly DependencyProperty GrainOpacityProperty = Reg(nameof(GrainOpacity), 0.05);

    /// <summary>Clips content to the outline. Off by default; costs a clip layer.</summary>
    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(
        nameof(ClipContent), typeof(bool), typeof(ChamferPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush? Sheen { get => (Brush?)GetValue(SheenProperty); set => SetValue(SheenProperty, value); }
    public Brush? Grain { get => (Brush?)GetValue(GrainProperty); set => SetValue(GrainProperty, value); }
    public double GrainOpacity { get => (double)GetValue(GrainOpacityProperty); set => SetValue(GrainOpacityProperty, value); }
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    // ── outline ─────────────────────────────────────────────────────────────

    /// <summary>The current outline in local coordinates. Used by the layout self-check.</summary>
    public Geometry Outline => BuildOutline(RenderSize, 0);

    private Geometry BuildOutline(Size size, double inset) => BuildGeometry(size, inset,
        Cuts, Corners, Chamfer,
        EdgeProfile.Parse(TopEdge), EdgeProfile.Parse(RightEdge),
        EdgeProfile.Parse(BottomEdge), EdgeProfile.Parse(LeftEdge),
        Notch, NotchWidth, NotchDepth, NotchPosition);

    /// <summary>
    /// Walks the perimeter clockwise, one edge at a time, then trims each corner by its cut.
    ///
    /// Each edge is generated in its natural axis direction (left→right or top→bottom) from
    /// its profile, and reversed where the walk runs the other way. Jogs are clamped clear of
    /// the corners by at least the corner's cut, so a corner is always met by a straight run and
    /// the cut can be applied as a simple trim of the two segments that meet there.
    /// </summary>
    public static Geometry BuildGeometry(Size size, double inset,
        Thickness cuts, Corner corners, double chamfer,
        EdgeProfile top, EdgeProfile right, EdgeProfile bottom, EdgeProfile left,
        Edge notch = Edge.None, double notchWidth = 0, double notchDepth = 0, double notchPos = 0.5)
    {
        var x0 = inset;
        var y0 = inset;
        var x1 = Math.Max(inset, size.Width - inset);
        var y1 = Math.Max(inset, size.Height - inset);
        var w = x1 - x0;
        var h = y1 - y0;

        // Never let a cut eat more than half of the shorter side, or the shape inverts.
        var limit = Math.Max(0, Math.Min(w, h) / 2.0);
        var c = Math.Clamp(chamfer, 0, limit);
        var explicitCuts = cuts.Left > 0 || cuts.Top > 0 || cuts.Right > 0 || cuts.Bottom > 0;
        var tl = Math.Min(limit, explicitCuts ? cuts.Left : corners.HasFlag(Corner.TopLeft) ? c : 0);
        var tr = Math.Min(limit, explicitCuts ? cuts.Top : corners.HasFlag(Corner.TopRight) ? c : 0);
        var br = Math.Min(limit, explicitCuts ? cuts.Right : corners.HasFlag(Corner.BottomRight) ? c : 0);
        var bl = Math.Min(limit, explicitCuts ? cuts.Bottom : corners.HasFlag(Corner.BottomLeft) ? c : 0);

        // Corner points, from the insets each edge has at its two ends.
        var pTL = new Point(x0 + left.Inset0, y0 + top.Inset0);
        var pTR = new Point(x1 - right.Inset0, y0 + top.Inset1);
        var pBR = new Point(x1 - right.Inset1, y1 - bottom.Inset1);
        var pBL = new Point(x0 + left.Inset1, y1 - bottom.Inset0);

        // Each edge as a polyline in its natural direction.
        var topPts = EdgePolyline(top, pTL.X, pTR.X, vertical: false, y0, +1, tl, tr,
            notch == Edge.Top, notchWidth, notchDepth, notchPos);
        var rightPts = EdgePolyline(right, pTR.Y, pBR.Y, vertical: true, x1, -1, tr, br,
            notch == Edge.Right, notchWidth, notchDepth, notchPos);
        var bottomPts = EdgePolyline(bottom, pBL.X, pBR.X, vertical: false, y1, -1, bl, br,
            notch == Edge.Bottom, notchWidth, notchDepth, notchPos);
        var leftPts = EdgePolyline(left, pTL.Y, pBL.Y, vertical: true, x0, +1, tl, bl,
            notch == Edge.Left, notchWidth, notchDepth, notchPos);

        bottomPts.Reverse();   // walk right → left
        leftPts.Reverse();     // walk bottom → top

        // Trim corners. Every edge meets its corner with an axis-aligned segment, so the cut is
        // a shift of the end point along that segment.
        TrimEnd(topPts, tr, new Vector(-1, 0));      TrimStart(rightPts, tr, new Vector(0, 1));
        TrimEnd(rightPts, br, new Vector(0, -1));    TrimStart(bottomPts, br, new Vector(-1, 0));
        TrimEnd(bottomPts, bl, new Vector(1, 0));    TrimStart(leftPts, bl, new Vector(0, -1));
        TrimEnd(leftPts, tl, new Vector(0, 1));      TrimStart(topPts, tl, new Vector(1, 0));

        var pts = new List<Point>(topPts.Count + rightPts.Count + bottomPts.Count + leftPts.Count);
        pts.AddRange(topPts);
        pts.AddRange(rightPts);
        pts.AddRange(bottomPts);
        pts.AddRange(leftPts);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
            for (var i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// Points of one edge between two corner coordinates, in ascending order along its axis.
    /// </summary>
    /// <param name="vertical">True for the left/right edges (axis coordinate is Y).</param>
    /// <param name="baseCoord">The edge's fixed coordinate: y for top/bottom, x for left/right.</param>
    /// <param name="inward">+1 when insets move toward +axis (top, left), −1 otherwise (right, bottom).</param>
    private static List<Point> EdgePolyline(EdgeProfile p, double u0, double u1,
        bool vertical, double baseCoord, int inward,
        double cutStart, double cutEnd,
        bool notch, double notchWidth, double notchDepth, double notchPos)
    {
        Point At(double u, double i) => vertical
            ? new Point(baseCoord + inward * i, u)
            : new Point(u, baseCoord + inward * i);

        var pts = new List<Point>(8);
        var length = u1 - u0;

        if (p.HasJog && length > p.Run + cutStart + cutEnd + 8)
        {
            // Profile positions are measured from this edge's start corner. A jog is kept clear
            // of a cut corner so the corner is still met by a straight run; where there is no
            // cut it may sit right at the corner, which is what a mating triangle needs.
            var marginStart = cutStart > 0 ? cutStart + 4 : 0;
            var marginEnd = cutEnd > 0 ? cutEnd + 4 : 0;
            var jog = u0 + p.JogStart(length, marginStart, marginEnd);

            pts.Add(At(u0, p.Inset0));
            pts.Add(At(jog, p.Inset0));
            pts.Add(At(jog + p.Run, p.Inset1));
            pts.Add(At(u1, p.Inset1));
            return pts;
        }

        pts.Add(At(u0, p.Inset0));

        if (notch && notchWidth > 0 && notchDepth > 0 && length > notchWidth + cutStart + cutEnd + 8)
        {
            var span = length - cutStart - cutEnd - 8;
            var start = u0 + cutStart + 4 + (span - notchWidth) * Math.Clamp(notchPos, 0, 1);
            var end = start + notchWidth;
            var deep = p.Inset0 + notchDepth;
            pts.Add(At(start, p.Inset0));
            pts.Add(At(start, deep));
            pts.Add(At(end, deep));
            pts.Add(At(end, p.Inset0));
        }

        pts.Add(At(u1, p.Inset0));
        return pts;
    }

    private static void TrimStart(List<Point> pts, double cut, Vector direction)
    {
        if (cut <= 0 || pts.Count == 0) return;
        pts[0] = pts[0] + direction * cut;
    }

    private static void TrimEnd(List<Point> pts, double cut, Vector direction)
    {
        if (cut <= 0 || pts.Count == 0) return;
        pts[^1] = pts[^1] + direction * cut;
    }

    // ── render ──────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var thickness = Stroke is null ? 0 : StrokeThickness;

        // Fill on the full outline, stroke on a half-pixel inset so the line lands on the
        // device pixel rather than straddling two of them.
        var fillGeo = BuildOutline(size, 0);
        if (Background is not null) dc.DrawGeometry(Background, null, fillGeo);

        if (Grain is not null && GrainOpacity > 0)
        {
            dc.PushOpacity(GrainOpacity);
            dc.PushClip(fillGeo);
            dc.DrawRectangle(Grain, null, new Rect(size));
            dc.Pop();
            dc.Pop();
        }

        if (Sheen is not null)
        {
            dc.PushClip(fillGeo);
            dc.DrawRectangle(Sheen, null, new Rect(size));
            dc.Pop();
        }

        if (thickness > 0)
        {
            var pen = new Pen(Stroke, thickness);
            pen.Freeze();
            dc.DrawGeometry(null, pen, BuildOutline(size, thickness / 2.0));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        Clip = ClipContent ? BuildOutline(finalSize, 0) : null;
        return result;
    }
}
