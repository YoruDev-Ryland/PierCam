using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PierCam.Ui;

/// <summary>
/// Shared motion vocabulary.
///
/// Two rules keep the animation cheap enough to run on a machine that is also debayering
/// frames. First, only ever animate Opacity and RenderTransform — both are composited on
/// the render thread and never touch layout. Second, every animation gets an explicit
/// duration and FillBehavior.Stop where possible, so no clock is left running after the
/// motion finishes. Animating Width, Margin or anything layout-affecting is what makes WPF
/// UIs stutter, and none of it happens here.
/// </summary>
public static class Motion
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(160));
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(280));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(520));

    public static IEasingFunction EaseOut => new CubicEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction EaseInOut => new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>Overshoot easing for elements that should feel mechanical rather than soft.</summary>
    public static IEasingFunction Snap => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    /// <summary>
    /// Fades and slides an element in. Used with an increasing delay across a group so panels
    /// assemble in sequence rather than all appearing at once.
    ///
    /// Every clock on the element is cleared before the new one starts. Without that, the second
    /// entrance inherits whatever the first one left holding: the animation from the previous
    /// visit is still filling the property, so the freshly assigned start values are masked and
    /// the element enters from wherever it happened to be rather than from the beginning. The
    /// visible symptom is an entrance that looks right once and then progressively wrong — which
    /// is exactly what a page you can navigate back to does.
    /// </summary>
    public static void Enter(UIElement element, int delayMs = 0, double fromY = 14, double fromX = 0,
        Duration? duration = null)
    {
        if (element is null) return;

        var tt = EnsureTranslate(element);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var length = duration ?? Normal;

        element.BeginAnimation(UIElement.OpacityProperty, null);
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.BeginAnimation(TranslateTransform.YProperty, null);

        element.Opacity = 0;
        tt.X = fromX;
        tt.Y = fromY;

        var fade = new DoubleAnimation(0, 1, length)
        {
            BeginTime = begin,
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        var slideY = new DoubleAnimation(fromY, 0, length) { BeginTime = begin, EasingFunction = EaseOut };
        var slideX = new DoubleAnimation(fromX, 0, length) { BeginTime = begin, EasingFunction = EaseOut };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, slideY);
        if (Math.Abs(fromX) > 0.01) tt.BeginAnimation(TranslateTransform.XProperty, slideX);
    }

    /// <summary>Staggers <see cref="Enter"/> across a set of elements.</summary>
    public static void EnterAll(int stepMs, double fromY, params UIElement?[] elements)
    {
        var delay = 0;
        foreach (var e in elements)
        {
            if (e is null) continue;
            Enter(e, delay, fromY);
            delay += stepMs;
        }
    }

    /// <summary>
    /// Eases a scale transform to a new factor, both axes together. Used where something has to
    /// get out of the way without re-laying-out — a RenderTransform composites on the render
    /// thread, so the thing being shrunk keeps working and stays hit-testable at its new size.
    /// </summary>
    public static void ScaleTo(ScaleTransform transform, double to, Duration? duration = null)
    {
        var anim = new DoubleAnimation(to, duration ?? Normal)
        {
            EasingFunction = EaseInOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Eases a translate transform's Y to a new offset.</summary>
    public static void SlideTo(TranslateTransform transform, double toY, Duration? duration = null)
    {
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(toY, duration ?? Normal)
        {
            EasingFunction = EaseInOut,
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    /// <summary>Cross-fades an element to a new opacity without disturbing layout.</summary>
    public static void FadeTo(UIElement element, double to, Duration? duration = null, Action? onDone = null)
    {
        var anim = new DoubleAnimation(to, duration ?? Fast)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        if (onDone is not null) anim.Completed += (_, _) => onDone();
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Fades a panel out, swaps it for another, and fades that in.</summary>
    public static void Swap(UIElement outgoing, UIElement incoming)
    {
        FadeTo(outgoing, 0, Fast, () =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 0;
            Enter(incoming, 0, 10);
        });
    }

    /// <summary>A single attention pulse — one shot, no looping clock left behind.</summary>
    public static void Pulse(UIElement element, double peak = 1.06)
    {
        var st = EnsureScale(element);
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = peak,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            AutoReverse = true,
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen) return existing;

        if (element.RenderTransform is TransformGroup g)
        {
            foreach (var t in g.Children)
                if (t is TranslateTransform tt && !tt.IsFrozen) return tt;
        }

        var translate = new TranslateTransform();
        element.RenderTransform = translate;
        return translate;
    }

    private static ScaleTransform EnsureScale(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform existing && !existing.IsFrozen) return existing;

        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        return scale;
    }
}
