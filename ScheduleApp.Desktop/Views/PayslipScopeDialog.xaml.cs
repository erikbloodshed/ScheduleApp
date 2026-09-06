using System.Windows;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Phase 3 of the batch payroll entry-point plan generalizes what was a
/// print-only dialog into a shared department/employee checkbox-tree +
/// period-picker scope dialog for "Print Payslips…"
/// (PayrollPrintExportViewModel.PrintPayslipsAsync) and "Export Payroll Report…"
/// (PayrollPrintExportViewModel.ExportPayrollReportAsync) -- see
/// <see cref="PayslipScopeViewModel"/>'s own doc comment for why the tree/
/// period/search machinery underneath is identical for both callers: same
/// tree, same period pickers, same validation (end &lt; start, "check at
/// least one employee"). "Start Payroll Period…" (PayrollViewModel.
/// StartPayrollPeriodAsync) was a third caller of this same dialog until
/// build-order step 9.4 replaced that flow with "New Payroll Run…", which
/// opens PayrollWizardDialog instead -- see PayrollRunViewModel.NewPayrollRun's
/// own doc comment. Only the window title, the description blurb above
/// the period pickers, and the confirm button's caption/tooltip differ
/// between the two remaining callers, so those four are now constructor
/// parameters instead of being hardcoded to the "Print Payslips" wording.
/// MainViewModel.ExportScheduleAsync ("Export Schedule…") is a later fourth
/// caller reusing the same tree/period-picker/search machinery for its own
/// employee-and-date-range export scope; it's the one caller that passes
/// requirePin: false, since schedule export (unlike payroll) has never
/// required an Employee ID -- see GetSelectedEmployees' own doc comment.
/// Kept the PayslipScopeDialog class name rather than renaming to e.g.
/// PayrollScopeDialog -- the batch payroll entry-point plan's own Phase 3
/// notes that rename as "functionally optional", and leaving it avoids
/// touching every call site's type reference for a rename that changes no
/// behavior.
///
/// Same self-validating-on-confirm shape as ManualLogEntryDialog: nothing is
/// committed to <see cref="SelectedEmployees"/>/<see cref="PeriodStart"/>/
/// <see cref="PeriodEnd"/> until the confirm button's own checks pass, so
/// neither caller ever sees a half-valid result.
///
/// Constructed directly (`new PayslipScopeDialog(...)`) rather than through
/// DI, same as every other dialog in this app -- see PayslipScopeViewModel's
/// own doc comment for why that ViewModel itself isn't DI-registered either.
/// </summary>
public partial class PayslipScopeDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly PayslipScopeViewModel _scope;

    /// <summary>See PayslipScopeViewModel.GetSelectedEmployees' own doc comment --
    /// forwarded to that call in ConfirmButton_Click below. True (the default) for
    /// the two original payroll callers; MainViewModel.ExportScheduleAsync is the
    /// one caller that passes false.</summary>
    private readonly bool _requirePin;

    public List<Employee> SelectedEmployees { get; private set; } = [];
    public DateTime PeriodStart { get; private set; }
    public DateTime PeriodEnd { get; private set; }

    public PayslipScopeDialog(
        ActiveRosterProvider activeRosterProvider, DateTime defaultPeriodStart, DateTime defaultPeriodEnd,
        string title, string description, string confirmButtonText, string confirmButtonTooltip,
        IReadOnlyCollection<Employee>? presetSelection = null, bool requirePin = true)
    {
        InitializeComponent();

        _requirePin = requirePin;
        _scope = new PayslipScopeViewModel(activeRosterProvider);
        DataContext = _scope;

        Title = title;
        DescriptionText.Text = description;
        ConfirmButton.Content = confirmButtonText;
        ConfirmButton.ToolTip = confirmButtonTooltip;

        PeriodStartPicker.SelectedDate = defaultPeriodStart;
        PeriodEndPicker.SelectedDate = defaultPeriodEnd;

        // presetSelection defaults to null -- today's "check everyone" tree, unchanged for
        // every existing caller. PrintPayslipsAsync/ExportPayrollReportAsync are the only
        // callers that ever pass one, and only when they have an active payroll group to
        // hand in -- see PayslipScopeViewModel.LoadEmployeeTreeAsync's own doc comment.
        Loaded += async (_, _) => await _scope.LoadEmployeeTreeCommand.ExecuteAsync(presetSelection);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (PeriodStartPicker.SelectedDate is not DateTime start || PeriodEndPicker.SelectedDate is not DateTime end)
        {
            Warn("Choose a period start and end date.");
            return;
        }

        if (end < start)
        {
            Warn("Period end can't be before period start.");
            return;
        }

        var selected = _scope.GetSelectedEmployees(_requirePin);
        if (selected.Count == 0)
        {
            Warn("Check at least one employee to continue.");
            return;
        }

        SelectedEmployees = selected;
        PeriodStart = start;
        PeriodEnd = end;

        DialogResult = true;
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "Check your selection", MessageBoxButton.OK, MessageBoxImage.Warning);
}