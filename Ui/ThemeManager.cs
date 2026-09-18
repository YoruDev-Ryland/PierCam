using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PierCam.Ui;

/// <summary>A palette the user can pick. Adding one is a file plus a line here.</summary>
public sealed record ThemeDefinition(string Id, string Name, string Blurb, string Source);

/// <summary>
/// Swaps the palette dictionary at runtime.
///
/// The application's merged dictionaries are ordered [palette, controls]. Every style in
/// Controls.xaml refers to palette keys with DynamicResource, so replacing slot 0 re-themes
/// the entire window with no reload, no rebuilt visual tree, and no per-control work —
/// WPF just invalidates the affected properties.
/// </summary>
public static class ThemeManager
{
    public const int PaletteSlot = 0;

    /// <summary>
    /// To add a theme: copy Ui/Theme/Dark.xaml, change the values, keep every key, and add
    /// it to this list. Nothing else needs to change.
    /// </summary>
    public static readonly IReadOnlyList<ThemeDefinition> Themes = new[]
    {
        new ThemeDefinition("dark", "Dark", "Near-black, cool panels, orange signal", "Ui/Theme/Dark.xaml"),
        new ThemeDefinition("light", "Light", "Paper white, black hairlines, hot orange", "Ui/Theme/Light.xaml"),
        new ThemeDefinition("night", "Night", "Red only — preserves dark adaptation", "Ui/Theme/Night.xaml"),
    };

    public static ThemeDefinition Current { get; private set; } = Themes[0];

    public static event Action<ThemeDefinition>? Changed;

    public static ThemeDefinition Resolve(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    public static void Apply(string? id)
    {
        var theme = Resolve(id);
        var app = Application.Current;
        if (app is null) return;

        try
        {
            var dict = new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) };
            if (app.Resources.MergedDictionaries.Count > PaletteSlot)
                app.Resources.MergedDictionaries[PaletteSlot] = dict;
            else
                app.Resources.MergedDictionaries.Insert(PaletteSlot, dict);

            Current = theme;
            Changed?.Invoke(theme);
        }
        catch (Exception ex)
        {
            // A broken theme file must never take the app down — stay on the current one.
            PierCam.App.Log(ex, $"ApplyTheme({theme.Id})");
        }
    }

    /// <summary>Looks up a palette brush, falling back to transparent rather than throwing.</summary>
    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    public static bool IsLight =>
        Application.Current?.TryFindResource("ThemeIsLight") is bool b && b;
}
