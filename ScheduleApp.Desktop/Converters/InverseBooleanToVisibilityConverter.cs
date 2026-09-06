using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>True -> Collapsed, False -> Visible. Used wherever AttendanceView.xaml needs
/// the opposite of BoolToVisibility's usual sense -- e.g. hiding the Summary tab's
/// results grid until HasSummaryRows is true, or hiding the Manual Entries grid's
/// placeholder text once HasLoadedManualEntries is true.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visibility = value is Visibility v ? v : Visibility.Visible;
        return visibility != Visibility.Visible;
    }
}
