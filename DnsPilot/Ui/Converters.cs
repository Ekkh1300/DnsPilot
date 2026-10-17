using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace DnsPilot.Ui;

/// <summary>True shows the element.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// False shows the element. Written rather than reusing BooleanToVisibilityConverter with its
/// ConverterParameter, because a parameter has to be passed through the converter resource every
/// time and forgetting it silently inverts the result - an empty state that is always visible.
/// </summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>
/// Resolves a brush by name, so a row can choose its own colour from data.
/// </summary>
/// <remarks>
/// XAML's DynamicResource cannot take a value from a binding, and latency colour is decided per
/// row at runtime, so the lookup happens here instead. Looking it up in Current rather than in a
/// passed-in application is deliberate: the resource lives in Application.Current, and after a
/// theme swap this converter returns the new colour while a brush captured earlier would still
/// hold the old one.
/// </remarks>
public sealed class BrushKeyToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return Brushes.Transparent;

        var found = Application.Current?.TryFindResource(key);
        return found as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EnumEquals : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Only a check writes. The uncheck of the button that lost its selection returns UnsetValue,
        // which WPF skips - so the losing button cannot write its own false back over the enum the
        // winning button just set.
        if (value is not true || parameter is null) return DependencyProperty.UnsetValue;
        try
        {
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
        }
        catch (ArgumentException)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}

/// <summary>Visible only when there is a non-empty string - used for the failure-detail line.</summary>
public sealed class StringToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible only when the string is empty or missing - the mirror of
/// <see cref="StringToVisibility"/>, written for the search field's placeholder: a hint that
/// exists exactly as long as the user has not typed anything, and gets out of the way the moment
/// they do.
/// </summary>
public sealed class EmptyStringToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
