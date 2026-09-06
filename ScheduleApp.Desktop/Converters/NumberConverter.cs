using System.Globalization;
using System.Windows.Data;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Formats a decimal amount as a plain number -- "1,234.56", or "-1,234.56" for a negative
/// value (PayrollResult.NetPay can go negative, e.g. a Cash Advance bigger than what was
/// earned that period -- see its own doc comment) -- with no currency symbol. Used for
/// every money value the app shows -- PayrollSummaryView's computed lines/adjustment rows/
/// category subtotals/Total Gross Pay/Deductions/Net Pay, EmployeesPage's Daily Rate/SSS/
/// PhilHealth/Pag-IBIG columns, and PayrollWizardDialog's own totals -- so all of them read
/// the same plain-number way, with no currency symbol anywhere in the app (PayrollExcelExporter's
/// own roster export and the PDF payslip generator likewise write plain numbers, not pesos).
/// </summary>
public class NumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal amount ? amount.ToString("N2") : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
