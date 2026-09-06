using System.Globalization;
using System.Windows.Data;
using ScheduleApp.Core.Enums;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Renders PayrollAdjustmentType.ToText() (see PayrollAdjustmentTypeLabel) for each
/// category card's header in PayrollSummaryView -- "Premium Pay" and "Pag-IBIG"
/// rather than the raw enum names PremiumHoliday/PagIbig a plain binding would show. Same
/// role as ScheduleTypeToDisplayTextConverter, just for this enum.
/// </summary>
public class PayrollAdjustmentTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PayrollAdjustmentType type ? type.ToText() : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
