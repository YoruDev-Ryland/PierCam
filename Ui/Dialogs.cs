using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PierCam.Ui.Controls;

namespace PierCam.Ui;

/// <summary>
/// The app's own dialogs.
///
/// WPF's MessageBox is a system window: it carries the Windows title bar, the system fonts and
/// the system colours, and there is no way to style it. Turning up in the middle of an interface
/// built entirely out of chamfered plates, it reads as something that escaped from another
/// program — which is exactly the wrong impression to give at the moment you are asking someone
/// to approve rewriting their videos. These are ordinary windows wearing the same plate.
/// </summary>
internal static class Dialogs
{
    /// <summary>A yes/no decision. Returns true only for an explicit confirmation.</summary>
    public static bool Confirm(Window owner, string title, string message,
        string confirmText = "CONFIRM", string cancelText = "CANCEL", bool danger = false)
    {
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, 20),
            Foreground = Brush("TextDim")
        };

        var result = false;
        var dialog = Shell(owner, title, body, out var buttons);

        var cancel = MakeButton(cancelText, null);
        cancel.IsCancel = true;
        var confirm = MakeButton(confirmText, danger ? "BtnDanger" : "BtnPrimary");
        confirm.IsDefault = true;
        confirm.Click += (_, _) => { result = true; dialog.DialogResult = true; };

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        dialog.ShowDialog();
        return result;
    }

    /// <summary>
    /// The about box, and the only place in the app that asks for anything. PierCam is free with
    /// nothing held back, so the tip jar is somewhere you have to go looking — a banner over an
    /// app that runs unattended for days would wear out its welcome by the second night.
    /// </summary>
    public static void About(Window owner, string version, string supportLabel, Action openSupport)
    {
        var stack = new StackPanel { MaxWidth = 430 };

        // The header's lockup at dialog size: tag, wordmark, build.
        var lockup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        lockup.Children.Add(new SlantTag
        {
            Width = 22,
            Height = 34,
            Slant = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 15, 0)
        });

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var mark = new TextBlock { Text = "PIERCAM", FontSize = 27 };
        if (Resource("Display") is Style display) mark.Style = display;
        words.Children.Add(mark);

        var build = new TextBlock { Text = $"VERSION {version}", Margin = new Thickness(0, 3, 0, 0) };
        if (Resource("Label") is Style label) build.Style = label;
        words.Children.Add(build);

        lockup.Children.Add(words);
        stack.Children.Add(lockup);

        stack.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = Brush("HairlineSoft"),
            Margin = new Thickness(0, 18, 0, 16)
        });

        stack.Children.Add(Paragraph(
            "Dusk-to-dawn timelapse for the pier camera. It follows the roof, writes straight " +
            "to video instead of a quarter of a million stills, and keeps a whole night in one file."));

        stack.Children.Add(Paragraph(
            "PierCam is free, and there is nothing in it you have to pay to unlock. If it has " +
            "earned its keep, the tip jar is open — but the app does not care either way."));

        var link = new TextBlock { Text = supportLabel, Margin = new Thickness(0, 0, 0, 20) };
        if (Resource("LinkText") is Style linkStyle) link.Style = linkStyle;
        link.MouseLeftButtonUp += (_, _) => openSupport();
        stack.Children.Add(link);

        var dialog = Shell(owner, "About PierCam", stack, out var buttons);

        var close = MakeButton("CLOSE", null);
        close.IsCancel = true;
        var kofi = MakeButton("OPEN KO-FI", "BtnPrimary");
        kofi.IsDefault = true;
        kofi.Click += (_, _) => { openSupport(); dialog.DialogResult = true; };

        buttons.Children.Add(close);
        buttons.Children.Add(kofi);

        dialog.ShowDialog();

        static TextBlock Paragraph(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Margin = new Thickness(0, 0, 0, 14),
                Foreground = Brush("TextDim")
            };
            return block;
        }
    }

    /// <summary>Something the user only needs to acknowledge.</summary>
    public static void Alert(Window owner, string title, string message)
    {
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, 20),
            Foreground = Brush("TextDim")
        };

        var dialog = Shell(owner, title, body, out var buttons);
        var ok = MakeButton("OK", "BtnPrimary");
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(ok);
        dialog.ShowDialog();
    }

    /// <summary>A one-line text prompt. WPF has no built-in equivalent and this is all we need.</summary>
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 380, Margin = new Thickness(0, 6, 0, 20) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush("TextDim"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        stack.Children.Add(box);

        var dialog = Shell(owner, title, stack, out var buttons);

        var cancel = MakeButton("CANCEL", null);
        cancel.IsCancel = true;
        var save = MakeButton("SAVE", "BtnPrimary");
        save.IsDefault = true;
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    // ── the plate ───────────────────────────────────────────────────────────

    private static Window Shell(Window owner, string title, UIElement body, out StackPanel buttons)
    {
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(heading, Dock.Top);

        var tag = new SlantTag { Width = 14, Height = 11, Slant = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        DockPanel.SetDock(tag, Dock.Left);
        heading.Children.Add(tag);
        heading.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("Text")
        });

        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var stack = new StackPanel();
        stack.Children.Add(heading);
        stack.Children.Add(body);
        stack.Children.Add(buttons);

        var plate = new ChamferPanel
        {
            Content = stack,
            Padding = new Thickness(22, 18, 22, 18),
            Cuts = new Thickness(26, 0, 26, 0)
        };
        if (Resource("Plate") is Style plateStyle) plate.Style = plateStyle;

        // The plate style fills with translucent glass, which is right on a page that has
        // something behind it to be frosted by. On a transparent dialog window it just shows the
        // library through the text. Dialogs get the solid panel fill and a stronger edge.
        plate.Background = Brush("Panel") ?? Brushes.Gainsboro;
        plate.Stroke = Brush("HairlineStrong");
        plate.StrokeThickness = 1;

        var dialog = new Window
        {
            Title = title,
            Content = plate,
            Owner = owner,
            // No system chrome: the plate is the window. AllowsTransparency lets the chamfered
            // corners show the desktop through rather than sitting on a grey rectangle.
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        // Without a title bar there is nothing to drag, so the plate itself moves the window.
        plate.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { dialog.DragMove(); }
                catch (InvalidOperationException) { /* released before the drag began */ }
            }
        };

        return dialog;
    }

    private static Button MakeButton(string text, string? styleKey)
    {
        var button = new Button { Content = text, MinWidth = 104, Margin = new Thickness(8, 0, 0, 0) };
        if (styleKey is not null && Resource(styleKey) is Style style) button.Style = style;
        return button;
    }

    private static object? Resource(string key) => Application.Current?.TryFindResource(key);

    private static Brush? Brush(string key) => Resource(key) as Brush;
}
