using System.Globalization;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Undertime's waive toggle in PayrollSummaryView's DeductionLineTemplate -- shows
/// the action a click will take, not the current state: "Exclude" while Waived is
/// false (the line still counts toward Total Deductions), "Include" once it's
/// true. ConvertBack unused -- the toggle button's Click handler reads and inverts
/// PayrollLineItem.Waived itself rather than round-tripping through a two-way
/// binding here.
/// </summary>
public class WaivedToExcludeIncludeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Include" : "Exclude";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
