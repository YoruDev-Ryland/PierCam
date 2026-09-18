using System;
using System.Windows;
using PierCam.Models;

namespace PierCam.Ui;

/// <summary>
/// Puts the window back where it was, and refuses to put it somewhere you cannot reach.
///
/// Saved bounds are the *restore* bounds rather than the current ones, so a window closed while
/// maximised reopens maximised but un-maximises to a sensible size instead of staying full-screen
/// forever. The awkward case is a display that has gone away — an observatory PC gets driven over
/// Remote Desktop at one resolution and sat in front of at another — so the saved rectangle is
/// checked against the desktop that exists *now* and dropped if it would open the window off the
/// edge of it. A window you cannot drag back into view is worse than one in the wrong place.
/// </summary>
internal static class WindowPlacementService
{
    /// <summary>How much of the window must land on the desktop for it to count as reachable.</summary>
    private const double MinimumVisible = 120;

    public static void Restore(Window window, WindowPlacement saved, bool startMinimised)
    {
        if (saved.IsSet && IsReachable(saved))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = saved.Left;
            window.Top = saved.Top;
            window.Width = saved.Width;
            window.Height = saved.Height;
        }

        if (startMinimised) window.WindowState = WindowState.Minimized;
        else if (saved.Maximised) window.WindowState = WindowState.Maximized;
    }

    public static void Save(Window window, WindowPlacement into)
    {
        // RestoreBounds is the un-maximised rectangle. Left/Top/Width/Height would be the screen
        // itself if the window happened to be maximised when it closed, which would then be saved
        // as the size to un-maximise to.
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds is { Width: > 100, Height: > 100 } &&
            !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Top))
        {
            into.Left = bounds.Left;
            into.Top = bounds.Top;
            into.Width = bounds.Width;
            into.Height = bounds.Height;
        }

        // Minimised is deliberately not remembered as a state to reopen in — that is what the
        // "start minimised" option is for, and it should be a choice rather than an accident.
        into.Maximised = window.WindowState == WindowState.Maximized;
    }

    /// <summary>
    /// True when a usable part of the rectangle lands on the current virtual desktop. The virtual
    /// screen is already in device-independent units, which is what WPF positions windows in, so
    /// there is no DPI conversion to get wrong here.
    /// </summary>
    private static bool IsReachable(WindowPlacement p)
    {
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (desktop.Width <= 0 || desktop.Height <= 0) return false;

        var visible = Rect.Intersect(new Rect(p.Left, p.Top, p.Width, p.Height), desktop);
        return !visible.IsEmpty && visible.Width >= MinimumVisible && visible.Height >= MinimumVisible;
    }
}
