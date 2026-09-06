using System.Globalization;
using System.Windows.Data;
using ScheduleApp.Core.Enums;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Renders ScheduleType.ToText() (see ScheduleTypeLabel) for ApplyScheduleDialog's
/// TypeCombo -- "Official Business" with a space, rather than the raw enum name
/// TypeCombo would otherwise show by binding straight to Enum.GetValues.
/// </summary>
public class ScheduleTypeToDisplayTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ScheduleType type ? type.ToText() : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
