using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Companion to SummaryRowBackgroundConverter -- swaps a DataGridRow's text
/// color to the system's selected-text color when it's selected, same as
/// SummaryRowBackgroundConverter does for Background, and for the same
/// reason (a plain Style Setter here would otherwise permanently shadow the
/// theme's own Selected-state text color).
/// </summary>
public class SelectedRowForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? SystemColors.HighlightTextBrush : SystemColors.ControlTextBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
