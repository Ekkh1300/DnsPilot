using System;
using System.Windows;

using Application = System.Windows.Application;

namespace DnsPilot.Ui;

public enum Theme { Light, Dark }

/// <summary>
/// Swaps the active theme by replacing the one merged resource dictionary, which every brush
/// reference then re-resolves through.
/// </summary>
/// <remarks>
/// Brushes are referenced with DynamicResource throughout the window. That is the whole
/// mechanism: a StaticResource would be resolved once while the window is built and would keep
/// pointing at the old colour after a switch, so the toggle would appear to do nothing until the
/// window was reopened - a bug that would look like the theme feature is broken rather than like
/// a binding mistake.
/// </remarks>
public static class AppTheme
{
    private const string PackUri = "pack://application:,,,/Themes/{0}.xaml";

    public static Theme Current { get; private set; } = Theme.Light;

    public static event Action? Changed;

    /// <summary>
    /// Replaces the merged dictionary whose source ends in <paramref name="slot"/>.
    /// </summary>
    /// <remarks>
    /// The merged list is reached through <see cref="ResourceDictionary.MergedDictionaries"/>
    /// rather than by naming its type in a parameter: the collection's type name differs between
    /// WPF revisions, and taking the whole dictionary keeps this method compiling either way.
    /// </remarks>
    private static void Apply(ResourceDictionary resources, string slot, string themeName)
    {
        var merged = resources.MergedDictionaries;

        ResourceDictionary? found = null;
        foreach (var d in merged)
        {
            var source = d.Source?.OriginalString ?? "";
            if (source.EndsWith(slot, StringComparison.OrdinalIgnoreCase)) { found = d; break; }
        }

        var replacement = new ResourceDictionary
        {
            Source = new Uri(string.Format(PackUri, themeName), UriKind.Absolute),
        };

        if (found is not null) merged[merged.IndexOf(found)] = replacement;
        else merged.Add(replacement);
    }

    public static void Set(Theme theme)
    {
        if (Application.Current is null) return;
        Apply(Application.Current.Resources, "Light.xaml", theme == Theme.Light ? "Light" : "Dark");
        Current = theme;
        Changed?.Invoke();
    }

    public static void Toggle() => Set(Current == Theme.Light ? Theme.Dark : Theme.Light);
}
