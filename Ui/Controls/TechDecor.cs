using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PierCam.Ui.Controls;

/// <summary>
/// The stencilled index a header tab carries ahead of its label ("01 LIVE"). An attached
/// property rather than a subclass so the tabs stay plain RadioButtons for the nav group and
/// for UI automation.
/// </summary>
public static class Nav
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.RegisterAttached(
        "Code", typeof(string), typeof(Nav), new PropertyMetadata(string.Empty));

    public static string GetCode(DependencyObject d) => (string)d.GetValue(CodeProperty);
    public static void SetCode(DependencyObject d, string value) => d.SetValue(CodeProperty, value);
}

/// <summary>
/// Marks an element the layout check should not treat as a visible mark.
///
/// For invisible hit targets that deliberately fill a plate corner to corner: they draw
/// nothing, so a chamfer crossing their bounds is not the clipped-content bug the check is
/// looking for. Their visible children are still checked on their own.
/// </summary>
public static class LayoutCheck
{
    public static readonly DependencyProperty IgnoreProperty = DependencyProperty.RegisterAttached(
        "Ignore", typeof(bool), typeof(LayoutCheck), new PropertyMetadata(false));

    public static bool GetIgnore(DependencyObject d) => (bool)d.GetValue(IgnoreProperty);
    public static void SetIgnore(DependencyObject d, bool value) => d.SetValue(IgnoreProperty, value);
}

/// <summary>
/// Corner registration marks — the L-brackets and tick rules that frame the viewport.
///
/// Purely decorative, and drawn in one OnRender pass with a single frozen pen, so framing
/// the live image costs nothing per frame.
/// </summary>
public class RegistrationMarks : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RegistrationMarks),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArmProperty = DependencyProperty.Register(
        nameof(Arm), typeof(double), typeof(RegistrationMarks),
        new FrameworkPropertyMetadata(26.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gap between the bracket and the corner, which is what makes it read as a crop mark.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(RegistrationMarks),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RegistrationMarks),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Adds the detached outer ticks seen on film leaders.</summary>
    public static readonly DependencyProperty ShowOuterTicksProperty = DependencyProperty.Register(
        nameof(ShowOuterTicks), typeof(bool), typeof(RegistrationMarks),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Arm { get => (double)GetValue(ArmProperty); set => SetValue(ArmProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool ShowOuterTicks { get => (bool)GetValue(ShowOuterTicksProperty); set => SetValue(ShowOuterTicksProperty, value); }

    public RegistrationMarks() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w < 4 || h < 4) return;

        var pen = new Pen(Stroke, Thickness);
        pen.Freeze();

        var g = Gap;
        var a = Arm;

        void L(double cx, double cy, double sx, double sy)
        {
            dc.DrawLine(pen, new Point(cx, cy), new Point(cx + sx * a, cy));
            dc.DrawLine(pen, new Point(cx, cy), new Point(cx, cy + sy * a));
        }

        L(g, g, 1, 1);
        L(w - g, g, -1, 1);
        L(g, h - g, 1, -1);
        L(w - g, h - g, -1, -1);

        if (!ShowOuterTicks) return;

        // Short detached ticks a little further in, offset from the brackets.
        var t = a * 0.45;
        var o = g + a * 1.9;
        dc.DrawLine(pen, new Point(0, o), new Point(t, o));
        dc.DrawLine(pen, new Point(w, o), new Point(w - t, o));
        dc.DrawLine(pen, new Point(0, h - o), new Point(t, h - o));
        dc.DrawLine(pen, new Point(w, h - o), new Point(w - t, h - o));
    }
}

/// <summary>A ruled tick strip — the measurement scale that edges technical panels.</summary>
public class TickRule : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TickRule),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(TickRule),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Every Nth tick is drawn full height.</summary>
    public static readonly DependencyProperty MajorEveryProperty = DependencyProperty.Register(
        nameof(MajorEvery), typeof(int), typeof(TickRule),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public int MajorEvery { get => (int)GetValue(MajorEveryProperty); set => SetValue(MajorEveryProperty, value); }

    public TickRule() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0 || Spacing <= 0) return;

        var pen = new Pen(Stroke, 1.0);
        pen.Freeze();

        var i = 0;
        for (var x = 0.5; x < w; x += Spacing, i++)
        {
            var major = MajorEvery > 0 && i % MajorEvery == 0;
            dc.DrawLine(pen, new Point(x, h), new Point(x, h - (major ? h : h * 0.45)));
        }
    }
}

/// <summary>Diagonal hazard stripes, as seen on the reference hardware panels.</summary>
public class HazardStripes : FrameworkElement
{
    public static readonly DependencyProperty StripeBrushProperty = DependencyProperty.Register(
        nameof(StripeBrush), typeof(Brush), typeof(HazardStripes),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StripeWidthProperty = DependencyProperty.Register(
        nameof(StripeWidth), typeof(double), typeof(HazardStripes),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StripeGapProperty = DependencyProperty.Register(
        nameof(StripeGap), typeof(double), typeof(HazardStripes),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush StripeBrush { get => (Brush)GetValue(StripeBrushProperty); set => SetValue(StripeBrushProperty, value); }
    public double StripeWidth { get => (double)GetValue(StripeWidthProperty); set => SetValue(StripeWidthProperty, value); }
    public double StripeGap { get => (double)GetValue(StripeGapProperty); set => SetValue(StripeGapProperty, value); }

    public HazardStripes() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        var pen = new Pen(StripeBrush, StripeWidth);
        pen.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        var step = StripeWidth + StripeGap;
        for (var x = -h; x < w + h; x += step)
            dc.DrawLine(pen, new Point(x, h), new Point(x + h, 0));
        dc.Pop();
    }
}

/// <summary>
/// Shared tiling noise texture.
///
/// A flat translucent fill looks like a tinted rectangle; the same fill with a few percent
/// of fine grain reads as frosted glass. One 128×128 tile is generated at startup, frozen
/// and reused by every panel, so the entire effect costs 16 KB and no per-frame work —
/// which is the whole reason this is done with a texture rather than a live blur.
/// </summary>
public static class Grain
{
    private static ImageBrush? _brush;

    public static ImageBrush Brush => _brush ??= Create();

    private static ImageBrush Create()
    {
        const int size = 128;
        var pixels = new byte[size * size];
        // Fixed seed: the grain should be identical every run so screenshots and themes
        // stay comparable between sessions.
        var rng = new Random(20260917);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)rng.Next(96, 160);

        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bmp.Freeze();

        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Monospace readout that animates between values by scrambling characters.
///
/// Used for counters and status words. The scramble is short and only touches the glyphs
/// that actually changed, so it draws attention to the change without becoming noise.
/// </summary>
public class ScrambleText : TextBlock
{
    private const string Pool = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789#%&/\\";
    private static readonly Random Rng = new();
    private string _target = string.Empty;
    private int _ticks;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ScrambleText),
        new FrameworkPropertyMetadata(string.Empty, OnValueChanged));

    public static readonly DependencyProperty ScrambleEnabledProperty = DependencyProperty.Register(
        nameof(ScrambleEnabled), typeof(bool), typeof(ScrambleText),
        new PropertyMetadata(true));

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool ScrambleEnabled { get => (bool)GetValue(ScrambleEnabledProperty); set => SetValue(ScrambleEnabledProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ScrambleText)d).Begin((string?)e.NewValue ?? string.Empty);

    private void Begin(string value)
    {
        _target = value;
        if (!ScrambleEnabled || !IsLoaded)
        {
            Text = value;
            return;
        }

        _ticks = 0;
        _timer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(28), System.Windows.Threading.DispatcherPriority.Render,
            (_, _) => Tick(), Dispatcher);
        _timer.Start();
    }

    private void Tick()
    {
        _ticks++;
        const int steps = 6;
        if (_ticks >= steps)
        {
            Text = _target;
            _timer?.Stop();
            return;
        }

        var revealed = (int)Math.Ceiling(_target.Length * (_ticks / (double)steps));
        Span<char> buf = stackalloc char[_target.Length];
        for (var i = 0; i < _target.Length; i++)
        {
            var ch = _target[i];
            buf[i] = i < revealed || ch == ' ' || ch == ':' || ch == '.'
                ? ch
                : Pool[Rng.Next(Pool.Length)];
        }
        Text = new string(buf);
    }
}
