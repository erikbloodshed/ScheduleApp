using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>non-null -> Visible, null -> Collapsed -- the exact inverse of
/// NullToVisibilityConverter; see that class for where/why this pair is used.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}