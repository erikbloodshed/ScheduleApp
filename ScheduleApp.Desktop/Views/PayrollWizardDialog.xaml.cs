using System.Windows;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Build-order Group C's "New Payroll Run…" wizard shell (steps 5-8): a step indicator
/// plus Back/Next/Finish over three pages -- Period + Label, Employees, and Calculate
/// &amp; Review. That third page automatically computes and shows the Employee/Net
/// Pay/Gross Pay/Deductions review grid the moment it's reached (build-order steps 8.1/8.2
/// -- see PayrollWizardViewModel.CalculateReviewAsync), and its "Save Payroll Group"
/// button does the real repository write that creates the PayrollRun (build-order step
/// 8.3 -- see PayrollWizardViewModel.SavePayrollGroupCommand).
///
/// Constructed directly (`new PayrollWizardDialog(...)`), not through DI -- same
/// convention every other dialog in this app follows (see PayslipScopeDialog's own doc
/// comment for why): PayrollWizardViewModel is built here, not resolved from the
/// container, so there's a fresh instance -- and therefore no leftover period/label/
/// tree-selection state -- every time this dialog is opened.
///
/// All navigation and validation lives on PayrollWizardViewModel; this code-behind's only
/// job is turning its RequestClose event into an actual DialogResult/window close, so the
/// ViewModel itself never needs to know it's backing a real Window.
/// </summary>
public partial class PayrollWizardDialog : Window
{
    private readonly PayrollWizardViewModel _wizard;

    /// <summary>Build-order step 9.2's own passthrough -- a live read of
    /// PayrollWizardViewModel.SavedRun (see that property's own doc comment) rather than a
    /// value captured on confirm the way PayslipScopeDialog.SelectedEmployees/PeriodStart/
    /// PeriodEnd below are: Finish here doesn't itself write anything (unlike
    /// PayslipScopeDialog's own confirm button), so whatever Step 3's "Save Payroll Group"
    /// button (build-order step 8.3) already saved before Finish was clicked is sitting on
    /// _wizard.SavedRun by the time a caller reads this after ShowDialog() returns -- null
    /// if Step 3's Save was never clicked, same as the ViewModel's own property.</summary>
    public PayrollRun? SavedRun => _wizard.SavedRun;

    /// <summary>Build-order step 9.3's own passthrough -- what PayrollPage sets
    /// BatchScopeEmployees from, so the checklist shown right after Finish matches the
    /// actual Employee objects Step 2's tree had in hand, rather than round-tripping the
    /// Pins on SavedRun.Employees back through the DB to resolve them -- that round trip is
    /// step 10's job (Load Payroll Group…, reopening a run that wasn't just created in this
    /// same session), not this one.
    ///
    /// Reads PayrollWizardViewModel.SavedEmployees -- the selection frozen at the moment
    /// "Save Payroll Group" actually wrote SavedRun.Employees -- rather than a live
    /// GetSelectedEmployees() call: Finish itself doesn't re-save, so if a person goes Back
    /// to Step 2 and changes the tree after saving, a live read here would hand back a set
    /// that no longer matches what's actually sitting in the PayrollRunEmployees table (see
    /// SavedEmployees' own doc comment). Falls back to a live GetSelectedEmployees() only
    /// when nothing's been saved yet, which the current caller (PayrollRunViewModel.
    /// NewPayrollRun) never actually hits -- it only reads this once SavedRun is non-null,
    /// at which point SavedEmployees is guaranteed to be set alongside it -- kept purely so
    /// this property is never in a "nothing to return" state for some future caller.</summary>
    public List<Employee> SelectedEmployees =>
        _wizard.SavedEmployees?.ToList() ?? _wizard.GetSelectedEmployees();

    /// <summary>defaultPeriodStart/defaultPeriodEnd are optional so this can be opened
    /// with nothing pre-filled, as well as with a sensible starting period the way
    /// PayrollPage's real "New Payroll Run…" entry point (build-order step 9 --
    /// PayrollRunViewModel.NewPayrollRun) passes its own current period, the same way
    /// PayslipScopeDialog's own constructor takes the Payroll tab's current period as its
    /// default.</summary>
    public PayrollWizardDialog(
        ActiveRosterProvider activeRosterProvider,
        IPayrollRunRepository payrollRunRepository,
        IPayrollComputationService payrollComputationService,
        DateTime? defaultPeriodStart = null,
        DateTime? defaultPeriodEnd = null)
    {
        InitializeComponent();

        _wizard = new PayrollWizardViewModel(
            activeRosterProvider, payrollRunRepository, payrollComputationService,
            defaultPeriodStart, defaultPeriodEnd);
        DataContext = _wizard;

        // Setting DialogResult on a window shown via ShowDialog() closes it immediately,
        // so there's nothing further to do here beyond that -- see
        // PayrollWizardViewModel.RequestClose's own doc comment for why this is an event
        // rather than the ViewModel doing this itself.
        _wizard.RequestClose += (_, result) => DialogResult = result;

        Loaded += async (_, _) => await _wizard.LoadEmployeeTreeCommand.ExecuteAsync(null);
    }
}
