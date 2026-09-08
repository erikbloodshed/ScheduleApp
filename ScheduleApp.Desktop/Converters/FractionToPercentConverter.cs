using System.Globalization;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Multiplies a stored fraction by 100 for display, divides a displayed percent number
/// back down to a fraction on the way back -- the one piece of arithmetic a plain TwoWay
/// Binding can't express on its own. Used only by PercentTextBox.xaml, wiring its own
/// Value/PlaceholderValue (fraction units -- 0.30 for 30%, the same convention every
/// PayrollPolicy/Employee rate field already stores) to the wrapped NumericTextBox's
/// identically-shaped DPs, which show and edit the scaled percent number (30) instead.
///
/// A real two-way IValueConverter rather than a one-off event handler doing the division
/// by hand: WPF's own binding engine calls Convert for the fraction-to-percent direction
/// (Value set from outside, e.g. by a data-bound ViewModel property, needs to reach the
/// inner box's displayed number) and ConvertBack for the reverse (someone types a new
/// percent, PercentTextBox.Value needs to become the matching fraction) -- both directions,
/// automatically, with no risk of one of them being forgotten the way a single
/// ValueCommitted-only handler would only ever cover the type-and-commit direction.
/// </summary>
public class FractionToPercentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal fraction ? fraction * 100m : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal percent ? percent / 100m : null;
}
