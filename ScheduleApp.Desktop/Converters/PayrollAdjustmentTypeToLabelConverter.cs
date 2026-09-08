using System.Globalization;
using System.Windows.Data;
using ScheduleApp.Core.Enums;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Renders PayrollAdjustmentType.ToText() (see PayrollAdjustmentTypeLabel) for each
/// category card's header in PayrollSummaryView -- "Pag-IBIG" rather than the raw enum
/// name PagIbig a plain binding would show -- with one deliberate override: PremiumHoliday
/// reads "Holiday Pay" here, not ToText()'s own "Premium Pay".
///
/// The override lives in this converter rather than in ToText() itself because ToText()
/// feeds several other places that were deliberately left saying "Premium Pay":
/// PayrollComputationService's seeded PayrollAdjustment.Description text,
/// PayrollAdjustmentRepository's duplicate-value error message, PayrollSummaryViewModel's
/// own delete-confirmation and status-bar messages, and PayslipLineBuilder's printed
/// payslip. Changing ToText() would have renamed all of those too, not just this view's
/// card header. Same role as ScheduleTypeToDisplayTextConverter otherwise, just for this
/// enum.
/// </summary>
public class PayrollAdjustmentTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PayrollAdjustmentType.PremiumHoliday => "Holiday Pay",
            PayrollAdjustmentType type => type.ToText(),
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
