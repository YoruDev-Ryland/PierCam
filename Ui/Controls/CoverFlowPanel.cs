using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PierCam.Ui.Controls;

/// <summary>
/// Horizontal fanned carousel: a centred card at full size with the rest compressed and
/// receding to either side, like a splayed stack of plates.
///
/// Implemented as a plain <see cref="Panel"/> that arranges every child itself and assigns
/// each a transform, rather than as a virtualising list with per-item animations. For a
/// library of nights that is the cheaper choice by a wide margin: one animated double drives
/// the entire layout, there is exactly one storyboard on screen no matter how many cards
/// exist, and no per-item clocks are ever created.
/// </summary>
public class CoverFlowPanel : Panel
{
    public CoverFlowPanel()
    {
        // Leaving the library must not leave a clock running behind it.
        Unloaded += (_, _) => StopDriving();
    }

    /// <summary>
    /// Continuous scroll position in card units. Animating this re-arranges the panel, which
    /// is why it is marked AffectsArrange — one property drives the whole fan.
    /// </summary>
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(420.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(
        nameof(CardHeight), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(380.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Offset of the first neighbour, as a fraction of card width.</summary>
    public static readonly DependencyProperty SpreadProperty = DependencyProperty.Register(
        nameof(Spread), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(0.82, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>
    /// How quickly spacing compresses with distance. 1 would be a flat row; lower values tuck
    /// far cards behind nearer ones so the fan stays inside the window however many there are.
    ///
    /// Low enough that everything past the first neighbour packs into a tight stack — each card
    /// shows a sliver of the one behind it, like files stood on a shelf — while the focused card
    /// still stands clear of the pile. Spacing between successive tucked cards is roughly
    /// <c>Spread · (ad^c − (ad−1)^c)</c> of a card width, which at c = 0.3 falls from ~0.16 to
    /// ~0.07 as you go outwards, so the far end never spreads out again.
    /// </summary>
    public static readonly DependencyProperty CompressionProperty = DependencyProperty.Register(
        nameof(Compression), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(0.30, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Compression { get => (double)GetValue(CompressionProperty); set => SetValue(CompressionProperty, value); }

    /// <summary>Degrees of turn applied to the first neighbour, growing with distance.</summary>
    public static readonly DependencyProperty RotationPerCardProperty = DependencyProperty.Register(
        nameof(RotationPerCard), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(46.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Ceiling on the turn, so far cards do not collapse to a line.</summary>
    public static readonly DependencyProperty MaxRotationProperty = DependencyProperty.Register(
        nameof(MaxRotation), typeof(double), typeof(CoverFlowPanel),
        new FrameworkPropertyMetadata(66.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double RotationPerCard { get => (double)GetValue(RotationPerCardProperty); set => SetValue(RotationPerCardProperty, value); }
    public double MaxRotation { get => (double)GetValue(MaxRotationProperty); set => SetValue(MaxRotationProperty, value); }

    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }
    public double CardHeight { get => (double)GetValue(CardHeightProperty); set => SetValue(CardHeightProperty, value); }
    public double Spread { get => (double)GetValue(SpreadProperty); set => SetValue(SpreadProperty, value); }

    /// <summary>How many cards out from the focused one are still drawn, at all.</summary>
    private const double FadeSpan = 8.5;

    /// <summary>
    /// Shape of the fade across that span. 1 is a straight ramp; higher holds the near cards up
    /// for longer and then falls off sharply at the outside.
    /// </summary>
    private const double FadeCurve = 3.0;

    /// <summary>Raised when the settled centre card changes, so the host can update its readout.</summary>
    public event Action<int>? CenterChanged;

    private int _lastCenter = -1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var childSize = new Size(CardWidth, CardHeight);
        foreach (UIElement child in InternalChildren) child.Measure(childSize);

        var h = double.IsInfinity(availableSize.Height) ? CardHeight * 1.25 : availableSize.Height;
        var w = double.IsInfinity(availableSize.Width) ? CardWidth * 3 : availableSize.Width;
        return new Size(w, Math.Max(h, CardHeight * 1.12));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0) return finalSize;

        var cx = finalSize.Width / 2.0;
        var cy = finalSize.Height / 2.0;
        var step = CardWidth * Spread;
        var pos = Position;

        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];

            // Distance from the centre slot, in card units.
            var d = i - pos;
            var ad = Math.Abs(d);

            // Power-law spacing: the first neighbour sits a fixed fraction of a card away and
            // each one after that adds much less than the last, so the unfocused nights pack
            // into a shelf of overlapping edges and the fan never runs off the window no matter
            // how many are in the library.
            var x = step * Spacing(d);

            // Turn the card towards the viewer. WPF has no perspective projection for 2D
            // elements, so the rotation is built from its two visible consequences:
            //   · horizontal foreshortening, cos(theta), which does most of the work
            //   · a vertical shear, which tilts the near and far edges in opposite directions
            // Together these read convincingly as a plate swung on a vertical axis, and cost
            // one frozen TransformGroup instead of a Viewport3D per card.
            var theta = Math.Clamp(-d * RotationPerCard, -MaxRotation, MaxRotation);
            var rad = theta * Math.PI / 180.0;
            var foreshorten = Math.Max(0.12, Math.Cos(rad));
            var shear = Math.Sin(rad) * 13.0;

            // Files on a shelf are all the same size and all still there, so depth is gentle —
            // enough to give the stack order, not enough to shrink it away.
            var depth = 1.0 / (1.0 + ad * 0.13);

            // Fade is deliberately not a straight ramp. A linear one takes a visible bite out of
            // the card right beside the focused one, which is the one you most want to read, and
            // then still leaves the outermost slivers hanging about. This holds the near cards at
            // very nearly full strength and drops away ever harder towards the edge of the shelf:
            // each step out costs more opacity than the one before it.
            var t = Math.Min(1.0, ad / FadeSpan);
            var opacity = 1.0 - Math.Pow(t, FadeCurve);
            var lift = ad * 4.0;

            // Cards nearer the centre must paint on top of their neighbours.
            SetZIndex(child, 10000 - (int)(ad * 100));

            var rect = new Rect(cx - CardWidth / 2.0, cy - CardHeight / 2.0, CardWidth, CardHeight);
            child.Arrange(rect);
            child.Opacity = opacity;
            child.Visibility = opacity <= 0.001 ? Visibility.Hidden : Visibility.Visible;

            var midX = CardWidth / 2.0;
            var midY = CardHeight / 2.0;

            // Composed into one matrix and written into a transform the card keeps, rather than
            // built from a fresh TransformGroup each time. This runs for every card on every
            // frame of a scroll; the group version allocated five objects per card per frame,
            // which is a few thousand a second thrown at the collector purely to move a shelf.
            // Matrix is a struct, so the arithmetic below allocates nothing at all. The order of
            // operations is the order the group applied its children in.
            var m = Matrix.Identity;
            m.ScaleAt(depth, depth, midX, midY);
            m.ScaleAt(foreshorten, 1.0, midX, midY);
            // Matrix has no SkewAt, so the skew is bracketed by translations to put its centre
            // on the middle of the card, which is where SkewTransform's own centre point was.
            m.Translate(-midX, -midY);
            m.Skew(0, shear);
            m.Translate(midX, midY);
            m.Translate(x, lift);

            if (child.RenderTransform is not MatrixTransform transform || transform.IsFrozen)
            {
                transform = new MatrixTransform();
                child.RenderTransform = transform;
            }
            transform.Matrix = m;
        }

        var center = (int)Math.Round(pos);
        if (center != _lastCenter && center >= 0 && center < count)
        {
            _lastCenter = center;
            CenterChanged?.Invoke(center);
        }

        return finalSize;
    }

    // ── scrolling ───────────────────────────────────────────────────────────
    //
    // The fan chases a target rather than playing a fixed-length animation to each card.
    // A timed animation per notch cannot survive fast input: every new notch restarts the
    // clock from a standstill, so the movement is repeatedly stopped and begun again, which
    // is felt as stutter however short each individual glide is. Here the input only ever
    // moves the target — never the fan directly — and a spring carries whatever speed it had
    // into the next stretch, so a burst of notches reads as one long continuous movement.

    private double _target;
    private double _velocity;
    private bool _driving;
    private long _lastTick;

    /// <summary>
    /// Convergence rate of the chase, in radians per second. The damping term below is 2·ω,
    /// which is critical damping: the quickest settle that never overshoots. Overshoot here
    /// would read as the shelf bouncing, which is not what a shelf does.
    ///
    /// A spring approaches its target asymptotically, so this number decides how long the last
    /// visible sliver of movement drags on for, not how fast the bulk of the travel is. Too low
    /// and the focused card is still perceptibly creeping home long after the movement looked
    /// finished, which reads as the card snapping into place when it finally gets there — the
    /// crawl is what is noticed, not the arrival. Settling to within a quarter pixel takes about
    /// 9.7/ω, so this is a glide of roughly a third of a second however far it has to travel.
    /// </summary>
    private const double Omega = 30.0;

    /// <summary>
    /// Rounds off the bottom of the spacing curve, in cards.
    ///
    /// A plain <c>|d|^c</c> with c below 1 is vertical at the origin: its slope runs to infinity
    /// exactly where the focused card is. Position and screen position then stop being the same
    /// thing near focus — a card a thousandth of a card-unit from home is still 48px off centre,
    /// and the last sliver of a glide, however small you make it, lands as a visible jump. That
    /// is felt as the focused card snapping into place just before it would have settled.
    ///
    /// Softening it keeps the curve identical where the shelf is (it still passes through 1 at
    /// the first neighbour, and the gaps beyond it are within a couple of percent of the plain
    /// power law) while making it straight through the origin, so a sub-pixel position error is
    /// a sub-pixel screen error.
    /// </summary>
    private const double Softening = 0.35;

    /// <summary>Offset of card <paramref name="d"/> cards from focus, in units of one step.</summary>
    private double Spacing(double d)
    {
        var p = (Compression - 1.0) / 2.0;
        var s2 = Softening * Softening;
        return d * Math.Pow(d * d + s2, p) / Math.Pow(1.0 + s2, p);
    }

    /// <summary>
    /// Slope of <see cref="Spacing"/> at focus: how many steps of screen travel one card of
    /// position buys right at the centre. Converts a position tolerance into a screen tolerance.
    /// </summary>
    private double FocusGain()
    {
        var p = (Compression - 1.0) / 2.0;
        var s2 = Softening * Softening;
        return Math.Pow(s2 / (1.0 + s2), p);
    }

    private double MaxIndex => Math.Max(0, InternalChildren.Count - 1);

    /// <summary>The card the fan is heading for, which is what stepping should count from.</summary>
    public int TargetIndex => (int)Math.Round(Math.Clamp(_target, 0, MaxIndex));

    public int NearestIndex => (int)Math.Round(Position);

    /// <summary>
    /// Moves by a fractional number of cards. Wheel input arrives here unthrottled: how far the
    /// shelf moves is how far you scrolled, and a trackpad's small deltas nudge it a fraction of
    /// a card each instead of being dropped to keep the count down.
    /// </summary>
    public void ScrollBy(double cards, bool immediate = false) => GoTo(_target + cards, immediate);

    /// <summary>Glides to a card.</summary>
    public void AnimateTo(int index, bool immediate = false) => GoTo(index, immediate);

    private void GoTo(double target, bool immediate)
    {
        if (InternalChildren.Count == 0) return;

        // Nothing else may drive Position, or the local writes below are silently outranked.
        BeginAnimation(PositionProperty, null);
        _target = Math.Clamp(target, 0, MaxIndex);

        if (!immediate) { StartDriving(); return; }

        StopDriving();
        _velocity = 0;
        Position = _target;
    }

    /// <summary>
    /// Set PIERCAM_SCROLLTRACE=1 to have every frame of every glide written to
    /// %AppData%\PierCam\scroll-trace.txt. Scroll feel is the one thing a screenshot cannot
    /// show, and guessing at it from how the pixels change is how you end up tuning the wrong
    /// number. Off by default and costing nothing.
    /// </summary>
    private static readonly bool TraceScroll =
        Environment.GetEnvironmentVariable("PIERCAM_SCROLLTRACE") == "1";

    private System.Text.StringBuilder? _trace;
    private System.Diagnostics.Stopwatch? _traceClock;

    private void StartDriving()
    {
        if (_driving) return;
        _driving = true;
        _lastTick = 0;
        if (TraceScroll)
        {
            _trace = new System.Text.StringBuilder();
            _traceClock = System.Diagnostics.Stopwatch.StartNew();
        }
        CompositionTarget.Rendering += OnFrame;
    }

    private void WriteTrace()
    {
        if (_trace is null) return;
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PierCam");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "scroll-trace.txt"),
                _trace.ToString() + Environment.NewLine);
        }
        catch (Exception) { }
        _trace = null;
    }

    /// <summary>
    /// Ends the per-frame clock. Everything in this class is built so that a settled carousel
    /// costs nothing at all, which matters in an app that is left running for weeks.
    /// </summary>
    private void StopDriving()
    {
        if (!_driving) return;
        _driving = false;
        CompositionTarget.Rendering -= OnFrame;
        WriteTrace();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime.Ticks : DateTime.UtcNow.Ticks;
        var dt = _lastTick == 0 ? 1.0 / 60.0 : (now - _lastTick) / (double)TimeSpan.TicksPerSecond;
        _lastTick = now;

        dt = Math.Clamp(dt, 1.0 / 240.0, 1.0 / 15.0);

        // The closed-form solution of the critically damped spring, not a stepwise integration
        // of it. Stepping introduces an error that grows with ω·dt, and the frames here are not
        // evenly spaced — a debayer or a collection lands in the middle of the glide and stretches
        // that frame to double length. Under Euler those long frames quietly lengthened the whole
        // glide by half as much again, all of it added to the tail, which is the one part of the
        // movement anybody looks at. This form is exact at any frame length and cannot go
        // unstable, so the glide takes as long as it is tuned to take and no longer.
        var d = Position - _target;
        var decay = Math.Exp(-Omega * dt);
        var vw = _velocity + Omega * d;
        var next = _target + (d + vw * dt) * decay;
        _velocity = (_velocity - Omega * vw * dt) * decay;

        // Finish when what is left to travel is under a quarter of a pixel *on screen*, which is
        // not the same as a quarter pixel of position: the spacing curve magnifies position into
        // screen offset, by FocusGain right where the focused card is. Converting through it is
        // the whole point — a threshold picked in position units is a sub-pixel nudge or a
        // visible jump depending on a curve exponent somewhere else entirely.
        var pixelsPerCard = Math.Max(1.0, CardWidth * Spread * FocusGain());
        var epsilon = 0.25 / pixelsPerCard;
        _trace?.AppendLine(
            $"{_traceClock!.ElapsedMilliseconds,5} pos={Position,9:0.00000} next={next,9:0.00000} " +
            $"target={_target,7:0.000} vel={_velocity,9:0.000} dt={dt * 1000,5:0.0} " +
            $"offset={Spacing(Position - _target) * CardWidth * Spread,8:0.00}px");

        if (Math.Abs(_target - next) < epsilon && Math.Abs(_velocity) < epsilon * 30.0)
        {
            _trace?.AppendLine($"{_traceClock!.ElapsedMilliseconds,5} STOP jump={Math.Abs(Spacing(next - _target)) * CardWidth * Spread:0.000}px");
            Position = _target;
            _velocity = 0;
            StopDriving();
            return;
        }

        Position = next;
    }
}
