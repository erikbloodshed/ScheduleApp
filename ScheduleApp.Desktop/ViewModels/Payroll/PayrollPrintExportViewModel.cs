using System.Reactive.Linq;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Excel;
using ScheduleApp.Payroll;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 4 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel as
/// build-order step 5, the plan's last extraction. Owns batch print/export: previewing payslips
/// for many employees at once (<see cref="PrintPayslipsAsync"/>) and saving an Excel roster of
/// many employees' payroll at once (<see cref="ExportPayrollReportAsync"/>) -- both "pick a
/// scope, then compute over whoever's checked" (<see cref="ComputeForChosenScopeAsync"/>),
/// differing only in what they do with the results.
///
/// Needs no reference to PayrollSummaryViewModel, PayrollGroupViewModel or MainViewModel:
/// neither command reads whoever is selected on the Payroll tab -- each opens its own
/// department/employee tree and works over whoever comes back checked from *that*.
///
/// Takes PayrollScopeState and AttendanceBusyState shared-not-owned, the same way every other
/// Payroll child does. Never writes to the scope -- it only reads the active batch to preset
/// the scope tree. ActiveRosterProvider is only here to hand to that tree.</summary>
public partial class PayrollPrintExportViewModel : ViewModelBase
{
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollComputationService _payrollComputationService;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _busy doc comment for why this is one instance, not one per child.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _scope doc comment.</summary>
    private readonly PayrollScopeState _scope;

    /// <summary>Already resolved to whatever's effective -- see PayrollSummaryViewModel's
    /// own _companyName doc comment. Passed straight through to the payslip preview.</summary>
    private readonly string _companyName;

    /// <summary>Both commands are scope-independent by design -- they open their own tree --
    /// so, unlike Print Current Payslip, nothing but a running refresh holds them back.</summary>
    private readonly IObservable<bool> _notBusy;

    public PayrollPrintExportViewModel(
        ActiveRosterProvider rosterProvider,
        IPayrollComputationService payrollComputationService,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope,
        string companyName)
        : base(statusBarService)
    {
        _rosterProvider = rosterProvider;
        _payrollComputationService = payrollComputationService;
        _busy = busy;
        _scope = scope;
        _companyName = companyName;

        _notBusy = _busy.WhenAnyValue(b => b.IsRunning).Select(running => !running);

        ReportFailuresOf(PrintPayslipsCommand, ExportPayrollReportCommand);
    }

    /// <summary>"Print Payslips…" on PayrollPage: picks a scope and period, computes everyone
    /// checked, and previews their payslips (see PayslipPreviewViewModel).</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task PrintPayslipsAsync()
    {
        var chosen = await ComputeForChosenScopeAsync(
            new PayslipScopeViewModel(_rosterProvider)
            {
                Title = "Print Payslips",
                Description = "Choose who to print payslips for and which period -- 4 per Letter page, cut apart along the quarter-page lines.",
                ConfirmText = "Print…",
                ConfirmToolTip = "Opens a preview of every checked employee's payslip for the chosen period.",
                PeriodStart = _scope.PeriodStart,
                PeriodEnd = _scope.PeriodEnd,
                PresetSelection = ActiveBatch,
            },
            "Could not compute payroll for printing");
        if (chosen is not var (_, results))
            return;

        await ShowDialogAsync(new PayslipPreviewViewModel(results, _companyName));
    }

    /// <summary>"Export Payroll Report…" on PayrollPage: the same scope-then-compute as
    /// <see cref="PrintPayslipsAsync"/>, ending in an Excel workbook instead of a preview.
    /// Computing and saving are reported apart ("could not compute" vs "could not save"), so a
    /// failure says which step actually went wrong.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task ExportPayrollReportAsync()
    {
        var chosen = await ComputeForChosenScopeAsync(
            new PayslipScopeViewModel(_rosterProvider)
            {
                Title = "Export Payroll Report",
                Description = "Choose who this payroll report covers and which period.",
                ConfirmText = "Export…",
                ConfirmToolTip = "Computes payroll for everyone checked and saves it to an Excel workbook.",
                PeriodStart = _scope.PeriodStart,
                PeriodEnd = _scope.PeriodEnd,
                PresetSelection = ActiveBatch,
            },
            "Could not compute payroll for export");
        if (chosen is not var (scope, results))
            return;

        var start = DateOnly.FromDateTime(scope.PeriodStart);
        var end = DateOnly.FromDateTime(scope.PeriodEnd);
        if (await PickFileToSaveAsync("Excel Workbook (*.xlsx)|*.xlsx", $"Salary_{start:MMddyy}-{end:MMddyy}.xlsx",
                "Save Payroll Report") is not { } path)
        {
            return;
        }

        try
        {
            PayrollExcelExporter.ExportRosterToExcel(path, results, scope.Employees, start, end);
            StatusBar.ShowSuccess($"Saved payroll report to {path}.");
        }
        catch (Exception ex)
        {
            ShowFailure(ex, "Could not save payroll report");
        }
    }

    /// <summary>The active payroll group, if there is one, for a scope picker to start checked
    /// to instead of the whole company (build-order step 11) -- still just a starting point,
    /// every box stays editable.</summary>
    private IReadOnlyCollection<Employee>? ActiveBatch =>
        _scope.BatchScopeEmployees.Count > 0 ? _scope.BatchScopeEmployees : null;

    /// <summary>
    /// Shows <paramref name="scopePicker"/>, then computes a PayrollResult for everyone it comes back
    /// with. Null if the picker was cancelled, the computation failed (reported under
    /// <paramref name="failureTitle"/>) or was cancelled, or it produced nothing.
    ///
    /// The picker is shown outside the _busy window, so cancelling it never touches
    /// _busy.IsRunning. The computation runs inside it: one PrepareBatchAsync for every checked
    /// pin, a batch-wide contribution seed, then ComputeOneFromBatchAsync per employee -- see
    /// PayrollBatchContext's own doc comment for why looping ComputeOneAsync instead repeated a
    /// company-wide attendance fetch per employee.
    /// </summary>
    private async Task<(PayslipScope Scope, List<PayrollResult> Results)?> ComputeForChosenScopeAsync(
        PayslipScopeViewModel scopePicker, string failureTitle)
    {
        if (!await ShowDialogAsync(scopePicker) || scopePicker.AcceptedScope is not { } scope)
            return null;

        var start = DateOnly.FromDateTime(scope.PeriodStart);
        var end = DateOnly.FromDateTime(scope.PeriodEnd);
        var employees = scope.Employees;
        List<PayrollResult>? results = null;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var pins = employees.Select(e => e.Pin).ToHashSet();
            var batch = await _payrollComputationService.PrepareBatchAsync(pins, start, end, cancellationToken);
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
        onError: ex => ShowFailure(ex, failureTitle));

        return results is { Count: > 0 } ? (scope, results) : null;
    }
}
