using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Computes DataGridRow.Background for the Attendance Summary grid from two
/// inputs at once -- Status and IsSelected -- rather than letting a plain
/// Style Setter (the status tint) and the theme's own IsSelected trigger
/// fight over the same property. WPF's style precedence rules mean a local
/// Style Setter beats the implicit theme style's Selected trigger, so a
/// Setter-only version of the status tint silently makes selection
/// invisible; the fix used to be an extra IsSelected Trigger layered on top
/// to win it back. This does the same job as one Convert() call instead --
/// selection always wins because the code says so, not because of where a
/// Trigger happens to sit in WPF's precedence order.
/// </summary>
public class SummaryRowBackgroundConverter : IMultiValueConverter
{
    private static readonly PunchStatusToRowBackgroundBrushConverter StatusTint = new();

    /// <summary>values[0] is Status (from the row's DataContext), values[1] is
    /// IsSelected (from the DataGridRow itself via RelativeSource Self) -- see
    /// the MultiBinding in AttendanceView.xaml's DataGrid.RowStyle.</summary>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isSelected = values.Length > 1 && values[1] is true;
        if (isSelected)
            return SystemColors.HighlightBrush;

        object? status = values.Length > 0 ? values[0] : null;
        return StatusTint.Convert(status, targetType, parameter, culture)!;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
