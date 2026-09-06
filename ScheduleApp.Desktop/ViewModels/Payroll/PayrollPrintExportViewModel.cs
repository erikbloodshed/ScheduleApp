using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using ScheduleApp.Excel;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 4 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel as
/// build-order step 5, the plan's last extraction. Owns batch print/export: previewing a
/// payslip for many employees at once (<see cref="PrintPayslipsAsync"/>) and saving an Excel
/// roster of many employees' payroll at once (<see cref="ExportPayrollReportAsync"/>) -- both
/// the same "open PayslipScopeDialog, then IPayrollComputationService.PrepareBatchAsync +
/// ComputeOneFromBatchAsync over whoever's checked" shape, differing only in what they do with
/// the results once computed (a PayslipPreviewDialog vs a SaveFileDialog + PayrollExcelExporter
/// call) -- see each method's own doc comment.
///
/// Like PayrollRunViewModel (concern 3), needs no reference to PayrollSummaryViewModel or
/// PayrollGroupViewModel themselves -- see the refactor plan's own "Dependency graph": this
/// class and PayrollRunViewModel are the two concerns that stay fully decoupled from Summary
/// and Group, touching only PayrollScopeState, AttendanceBusyState, and their own repositories.
/// Unlike PayrollRunViewModel, this class doesn't take MainViewModel either -- neither method
/// here ever reads or writes MainViewModel.SelectedEmployee the way NewPayrollRun/
/// LoadPayrollGroupAsync's own landing sequence does; both open PayslipScopeDialog's own
/// department/employee tree and loop over whichever employees come back checked from *that*,
/// entirely independent of whoever happens to be selected on the Payroll tab's own
/// MainViewModel-shared tree. The refactor plan's own "The one real wrinkle" section describes
/// Run and this class together as touching "PayrollScopeState, _mainViewModel, and their own
/// repositories" -- the actual extraction found that generalization doesn't quite hold for this
/// half of the pair, so MainViewModel is left off this class's constructor as a correction, the
/// same "actual extraction found X" precedent PayrollRunViewModel's own doc comment already sets
/// for IStatusBarService.
///
/// PayrollPage.xaml's "Print Payslips…"/"Export Payroll Report…" toolbar buttons are bound
/// directly to PayrollViewModel (the facade), never to this class -- so PrintPayslipsCommand/
/// ExportPayrollReportCommand are forwarded back out under the same name via
/// PayrollViewModel.PrintExport (see that class's own "Forwarded members
/// (PayrollPrintExportViewModel)" region). Nothing in this class itself needs to know that.
///
/// Takes PayrollScopeState and AttendanceBusyState the same shared-not-owned way every other
/// child does (see any of their own doc comments and the refactor plan's own "Dependency
/// graph") -- both constructed on PayrollViewModel and passed in here. Also takes
/// ActiveRosterProvider, purely to hand off to PayslipScopeDialog's own tree -- replaces the
/// raw IScheduleRepository this class used to hand off directly, same "constructor-injected
/// just to hand off to a dialog" convention PayrollViewModel's own _rosterProvider doc
/// comment already describes, and PayrollRunViewModel's own constructor already follows for
/// PayrollWizardDialog/LoadPayrollGroupDialog -- see ActiveRosterProvider's own doc comment
/// for why both PrintPayslipsAsync and ExportPayrollReportAsync below now go through the
/// shared, RosterVersion-gated cache instead of each paying for its own
/// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync round trip. Also
/// takes IPayrollComputationService (the batch compute loop itself) and IStatusBarService
/// (the success/failure messages both methods report through).</summary>
public partial class PayrollPrintExportViewModel : ObservableObject
{
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IStatusBarService _statusBarService;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _busy doc comment for why this is one instance, not one per child.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _scope doc comment. This class reads PeriodStart/PeriodEnd/BatchScopeEmployees
    /// straight off this instance rather than through any forwarding property of its own, the
    /// same way PayrollSummaryViewModel/PayrollGroupViewModel/PayrollRunViewModel already do.
    /// Never writes to it -- unlike Run, neither method here establishes or replaces a batch
    /// scope, they only read whatever's already active to preset PayslipScopeDialog's own
    /// tree (see HasActiveBatchScope below).</summary>
    private readonly PayrollScopeState _scope;

    /// <summary>Already resolved to whatever's effective -- see PayrollSummaryViewModel's
    /// own _companyName doc comment for the shared reasoning (both children get the same
    /// one value from PayrollViewModel's constructor). Passed straight through to
    /// PayslipPreviewDialog by PrintPayslipsAsync below.</summary>
    private readonly string _companyName;

    public PayrollPrintExportViewModel(
        ActiveRosterProvider rosterProvider,
        IPayrollComputationService payrollComputationService,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope,
        string companyName)
    {
        _rosterProvider = rosterProvider;
        _payrollComputationService = payrollComputationService;
        _statusBarService = statusBarService;
        _busy = busy;
        _scope = scope;
        _companyName = companyName;

        // Trimmed to just this class's own concern (PrintPayslipsCommand/
        // ExportPayrollReportCommand) as of build-order step 5 -- the Payroll refactor plan's
        // last extraction, so PayrollViewModel's own NotifyToolbarCommands is retired entirely
        // rather than trimmed further (nothing is left on the facade for it to cover -- see
        // that class's own now-removed doc comment history). Same "PropertyChanged ->
        // NotifyCanExecuteChanged, immediately, every time, no artificial hold" behavior every
        // other child's own _busy.PropertyChanged subscription already follows -- see any of
        // their doc comments for why smoothing this independently never actually worked.
        //
        // No pending-refresh flag to check afterward the way PayrollSummaryViewModel's own
        // _refreshPending/PayrollGroupViewModel's own _payrollGroupRefreshPending/
        // _fullRosterRefreshPending are -- same reasoning PayrollRunViewModel's own
        // constructor doc comment gives for itself: neither PrintPayslipsAsync nor
        // ExportPayrollReportAsync is ever triggered by a _scope change arriving mid-refresh,
        // both only ever run from a direct button click, so there's nothing here that could go
        // stale while _busy.IsRunning was already true.
        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AttendanceBusyState.IsRunning)) return;
            NotifyPrintExportCommands();
        };
    }

    /// <summary>The two command notifications gated on _busy.IsRunning that this class owns --
    /// factored out so the constructor's _busy.PropertyChanged handler above is the only
    /// caller, same "one list, not two copies that could drift apart" reasoning every other
    /// child's own NotifyXCommands() already follows.</summary>
    private void NotifyPrintExportCommands()
    {
        PrintPayslipsCommand.NotifyCanExecuteChanged();
        ExportPayrollReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>_scope.BatchScopeEmployees.Count > 0, read directly off the shared scope
    /// rather than through PayrollGroupViewModel.HasBatchScope -- this class has no reference
    /// to Group to read that through (see this class's own doc comment for why staying
    /// decoupled from Group is deliberate here), so the same one-line condition is duplicated
    /// rather than taking on that dependency just for it. Both PrintPayslipsAsync and
    /// ExportPayrollReportAsync read this once, to decide whether PayslipScopeDialog's own
    /// tree should start checked to the active batch instead of the whole company -- see
    /// each method's own doc comment, build-order step 11.</summary>
    private bool HasActiveBatchScope => _scope.BatchScopeEmployees.Count > 0;

    /// <summary>Build-order step 5's batch command: "Print Payslips…" on
    /// PayrollPage. Opens PayslipScopeDialog first (its own department/employee
    /// tree, defaulting to this tab's current period and, per build-order step 11,
    /// pre-checked to BatchScopeEmployees when HasActiveBatchScope is true instead of the
    /// whole company -- see that dialog's own doc
    /// comment) entirely outside the _busy window, same "dialog shown before
    /// _busy.RunAsync starts" reasoning ManualEntryEditorViewModel.AddOrEditManualEntryAsync
    /// follows for its own dialog -- Cancel-ing the scope dialog never touches
    /// _busy.IsRunning at all.
    ///
    /// Once a scope and period are confirmed, recomputes a PayrollResult for every
    /// checked employee via IPayrollComputationService.PrepareBatchAsync (one shared
    /// attendance/adjustments/waiver fetch for every checked pin) followed by
    /// ComputeOneFromBatchAsync per employee -- see PayrollBatchContext's own doc comment
    /// for why looping ComputeOneAsync here instead used to repeat that same company-wide
    /// attendance fetch once per checked employee. Runs inside _busy.RunAsync the same way
    /// every other read/write in this
    /// class does, so the progress bar shows something's happening and every busy-gated
    /// command disables itself while a large run is still fetching -- unlike most of those
    /// other _busy.RunAsync calls, though, nothing here writes to the database, so
    /// there's no reload to chain afterward, just PayslipPreviewDialog to open once
    /// every employee's PayrollResult is in hand.</summary>
    [RelayCommand(CanExecute = nameof(CanPrintPayslips))]
    private async Task PrintPayslipsAsync()
    {
        var scopeDialog = new PayslipScopeDialog(
            _rosterProvider, _scope.PeriodStart, _scope.PeriodEnd,
            title: "Print Payslips",
            description: "Choose who to print payslips for and which period -- 4 per Letter page, cut apart along the quarter-page lines.",
            confirmButtonText: "Print…",
            confirmButtonTooltip: "Opens a preview of every checked employee's payslip for the chosen period.",
            // Build-order step 11: with an active payroll group, start the tree checked to
            // that group instead of the whole company -- still just a starting point, every
            // box stays editable before Print… is clicked.
            presetSelection: HasActiveBatchScope ? _scope.BatchScopeEmployees : null)
        {
            Owner = Application.Current.MainWindow,
        };
        if (scopeDialog.ShowDialog() != true) return;

        var start = DateOnly.FromDateTime(scopeDialog.PeriodStart);
        var end = DateOnly.FromDateTime(scopeDialog.PeriodEnd);
        var employees = scopeDialog.SelectedEmployees;

        List<PayrollResult>? results = null;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // One IPayrollComputationService.PrepareBatchAsync call for every checked
            // employee's pin, then ComputeOneFromBatchAsync per employee against that shared
            // result -- see PayrollBatchContext's own doc comment for why looping
            // ComputeOneAsync here used to repeat a company-wide attendance fetch once per
            // employee.
            var pins = employees.Select(e => e.Pin).ToHashSet();
            var batch = await _payrollComputationService.PrepareBatchAsync(pins, start, end, cancellationToken);

            // Same batch-wide seed-before-compute shape PayrollGroupViewModel.
            // RefreshPayrollGroupRowsAsync uses -- see SeedContributionsForBatchAsync's own
            // doc comment.
            batch = await _payrollComputationService.SeedContributionsForBatchAsync(
                employees, start, end, batch, cancellationToken);

            var computed = new List<PayrollResult>(employees.Count);
            foreach (var employee in employees)
            {
                var (result, _) = await _payrollComputationService.ComputeOneFromBatchAsync(
                    employee, start, end, batch, cancellationToken);
                computed.Add(result);
            }

            results = computed;
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not compute payroll for printing"));

        // A cancelled or failed run leaves results null -- nothing to preview.
        if (results is not { Count: > 0 }) return;

        var previewDialog = new PayslipPreviewDialog(results, _companyName) { Owner = Application.Current.MainWindow };
        previewDialog.ShowDialog();
    }

    /// <summary>Same !_busy.IsRunning guard as PayrollSummaryViewModel's own
    /// CanPrintCurrentPayslip/CanEditAdjustments -- unlike either of those, doesn't also
    /// check SelectedEmployee/Result, since "Print Payslips…" is scope-independent by
    /// design (see PayslipScopeDialog): it opens its own tree covering whichever
    /// employees the person checks there, with no dependency on whoever happens to
    /// be selected on the Payroll tab's own MainViewModel-shared tree right now.
    /// </summary>
    private bool CanPrintPayslips() => !_busy.IsRunning;

    /// <summary>"Export Payroll Report…" on PayrollPage, sitting next to "Print
    /// Payslips…" -- same scope-dialog-then-batch-compute shape as PrintPayslipsAsync
    /// above (see that method's own doc comment for why the dialog is shown entirely
    /// outside the _busy window and why the PrepareBatchAsync + ComputeOneFromBatchAsync
    /// pair runs inside it),
    /// but ending in a SaveFileDialog + PayrollExcelExporter.ExportRosterToExcel call
    /// instead of PayslipPreviewDialog. The try/catch around the save itself mirrors
    /// ReportViewModel.ExportSummary -- computing payroll and writing the workbook are
    /// kept as two separate steps (batch compute inside _busy.RunAsync with its own
    /// onError, then save in its own try/catch) so a failure in one is reported with
    /// the right message for what actually went wrong ("could not compute" vs "could
    /// not save"), the same split PrintPayslipsAsync already keeps between computing
    /// and previewing.</summary>
    [RelayCommand(CanExecute = nameof(CanExportPayrollReport))]
    private async Task ExportPayrollReportAsync()
    {
        var scopeDialog = new PayslipScopeDialog(
            _rosterProvider, _scope.PeriodStart, _scope.PeriodEnd,
            title: "Export Payroll Report",
            description: "Choose who this payroll report covers and which period.",
            confirmButtonText: "Export…",
            confirmButtonTooltip: "Computes payroll for everyone checked and saves it to an Excel workbook.",
            // Same active-group preset as PrintPayslipsAsync above -- see build-order step 11.
            presetSelection: HasActiveBatchScope ? _scope.BatchScopeEmployees : null)
        {
            Owner = Application.Current.MainWindow,
        };
        if (scopeDialog.ShowDialog() != true) return;

        var start = DateOnly.FromDateTime(scopeDialog.PeriodStart);
        var end = DateOnly.FromDateTime(scopeDialog.PeriodEnd);
        var employees = scopeDialog.SelectedEmployees;

        List<PayrollResult>? results = null;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // Same PrepareBatchAsync-once-then-ComputeOneFromBatchAsync-per-employee shape
            // PrintPayslipsAsync above uses -- see that method's own doc comment.
            var pins = employees.Select(e => e.Pin).ToHashSet();
            var batch = await _payrollComputationService.PrepareBatchAsync(pins, start, end, cancellationToken);

            // Same batch-wide seed-before-compute shape PrintPayslipsAsync above uses -- see
            // SeedContributionsForBatchAsync's own doc comment.
            batch = await _payrollComputationService.SeedContributionsForBatchAsync(
                employees, start, end, batch, cancellationToken);

            var computed = new List<PayrollResult>(employees.Count);
            foreach (var employee in employees)
            {
                var (result, _) = await _payrollComputationService.ComputeOneFromBatchAsync(
                    employee, start, end, batch, cancellationToken);
                computed.Add(result);
            }

            results = computed;
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not compute payroll for export"));

        // A cancelled or failed run leaves results null -- nothing to save, same guard
        // PrintPayslipsAsync applies before opening its own preview dialog.
        if (results is not { Count: > 0 }) return;

        var saveDialog = new SaveFileDialog
        {
            Title = "Save Payroll Report",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"Salary_{start:MMddyy}-{end:MMddyy}.xlsx",
        };
        if (saveDialog.ShowDialog() != true) return;

        try
        {
            PayrollExcelExporter.ExportRosterToExcel(saveDialog.FileName, results, employees, start, end);
            _statusBarService.ShowSuccess($"Saved payroll report to {saveDialog.FileName}.");
        }
        catch (Exception ex)
        {
            _statusBarService.ShowError(ex.Message, "Could not save payroll report");
        }
    }

    /// <summary>Same !_busy.IsRunning-only guard as CanPrintPayslips above, and for the
    /// same reason -- "Export Payroll Report…" opens its own scope tree via
    /// PayslipScopeDialog too, so it has no dependency on whoever's currently selected on
    /// this tab's own MainViewModel-shared tree either.</summary>
    private bool CanExportPayrollReport() => !_busy.IsRunning;
}
