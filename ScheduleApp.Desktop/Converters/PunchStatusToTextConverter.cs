using System.Globalization;
using System.Windows.Data;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Renders PunchStatus.ToText() (see PunchStatusLabel) -- "Official Business" with a
/// space. A parameter, when given, is a format string the text is put into
/// ("Filter to {0} only"), since a Binding's StringFormat is ignored when the target
/// isn't a string, such as a ToolTip.
/// </summary>
public class PunchStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value is PunchStatus status ? status.ToText() : value?.ToString() ?? string.Empty;
        return parameter is string format ? string.Format(culture, format, text) : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
