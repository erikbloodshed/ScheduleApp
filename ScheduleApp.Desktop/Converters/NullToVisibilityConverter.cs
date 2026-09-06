using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>null -> Visible, non-null -> Collapsed. Used for the Employees page's
/// "Select a department..." placeholder, which should only show before anything's
/// picked in the department tree -- see NotNullToVisibilityConverter for its exact
/// inverse, used by the department name/count header that placeholder stands in
/// for.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}