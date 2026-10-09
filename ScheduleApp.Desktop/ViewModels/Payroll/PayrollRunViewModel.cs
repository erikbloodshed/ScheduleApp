using System.Reactive.Linq;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 3 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel as
/// build-order step 4. Owns the Payroll Run lifecycle: starting a fresh run via the wizard
/// (<see cref="NewPayrollRunAsync"/>) and reopening a run saved in a past session, or earlier
/// this one (<see cref="LoadPayrollGroupAsync"/>). PayrollGroupViewModel (concern 2) owns the
/// *table* of who's currently in the batch and lets that membership be edited afterward; this
/// class owns the two *lifecycle* moments that establish or wholesale-replace that batch in the
/// first place. It needs no reference to PayrollSummaryViewModel or PayrollGroupViewModel --
/// only PayrollScopeState, AttendanceBusyState, its own repositories, and the app-wide employee
/// selection.
///
/// Takes PayrollScopeState and AttendanceBusyState shared-not-owned, the same way every other
/// Payroll child does -- both constructed on PayrollViewModel and passed in here.
/// ActiveRosterProvider/IPayrollRunRepository/IPayrollComputationService are only here to hand
/// to the wizard and the run picker, or to resolve a loaded run's Pins back to Employee objects
/// through the shared, RosterVersion-gated roster cache (see ActiveRosterProvider's own doc
/// comment).
///
/// Both commands share one !_busy.IsRunning guard even though neither body touches the
/// database directly: the wizard loads its tree and the picker its run list the moment they
/// open, through the one shared, app-lifetime-scoped ScheduleDbContext, so letting either open
/// mid-refresh would risk the "second operation started on this context instance" race _busy
/// exists to prevent.</summary>
public partial class PayrollRunViewModel : ViewModelBase
{
    /// <summary>The employee selected app-wide -- moved onto the batch just landed on.</summary>
    private readonly IEmployeeSelection _selection;
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollRunRepository _payrollRunRepository;
    private readonly IPayrollComputationService _payrollComputationService;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _busy doc comment for why this is one instance, not one per child.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _scope doc comment.</summary>
    private readonly PayrollScopeState _scope;

    private readonly IObservable<bool> _notBusy;

    public PayrollRunViewModel(
        IEmployeeSelection selection,
        ActiveRosterProvider rosterProvider,
        IPayrollRunRepository payrollRunRepository,
        IPayrollComputationService payrollComputationService,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope)
        : base(statusBarService)
    {
        _selection = selection;
        _rosterProvider = rosterProvider;
        _payrollRunRepository = payrollRunRepository;
        _payrollComputationService = payrollComputationService;
        _busy = busy;
        _scope = scope;

        _notBusy = _busy.WhenAnyValue(b => b.IsRunning).Select(running => !running);

        ReportFailuresOf(NewPayrollRunCommand);
        ReportFailuresOf("Could not load payroll group", LoadPayrollGroupCommand);
    }

    /// <summary>"New Payroll Run…" on PayrollPage: opens the wizard (see
    /// PayrollWizardViewModel) starting from this tab's own period. There's no seed step of
    /// its own: IPayrollComputationService already seeds SSS/PhilHealth/Pag-IBIG/Allowance/
    /// Premium Pay/Cash Advance defaults per employee every time it computes, so the wizard's
    /// Step 3 review *is* the seeding.
    ///
    /// Once the wizard is finished with a run saved on Step 3, lands on it (see
    /// <see cref="LandOn"/>), with the employees Step 2's tree already had in hand rather than
    /// resolving the saved run's Pins back through the database -- that round trip is
    /// <see cref="LoadPayrollGroupAsync"/>'s job, for a run from a past session where nothing is
    /// in hand. Cancel, or a Finish with nothing saved, changes nothing.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task NewPayrollRunAsync()
    {
        var wizard = new PayrollWizardViewModel(
            _rosterProvider, _payrollRunRepository, _payrollComputationService,
            _scope.PeriodStart, _scope.PeriodEnd);
        if (!await ShowDialogAsync(wizard) || wizard is not { SavedRun: { } run, SavedEmployees: { } employees })
            return;

        LandOn(run, [.. employees]);
        StatusBar.ShowSuccess($"Saved payroll run: {run.Label}");
    }

    /// <summary>"Load Payroll Group…" on PayrollPage: picks a saved run (see
    /// LoadPayrollGroupViewModel), outside the _busy window so cancelling the picker never
    /// touches _busy.IsRunning, then resolves the run's Pins back to current Employee objects
    /// inside it -- the employees may have been added, renamed or deleted since the run was
    /// saved.
    ///
    /// A Pin that no longer matches any current *active* employee (deleted, or since
    /// blacklisted) is silently dropped rather than failing the whole load -- best effort over
    /// all-or-nothing. If that drops everyone, it's reported as its own error rather than landing
    /// on an empty batch, which would look identical to nothing having loaded at all. That case
    /// is told apart from a failed round trip by employees staying null when the _busy.RunAsync
    /// block itself throws (onError has already reported it then).</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task LoadPayrollGroupAsync()
    {
        var picker = new LoadPayrollGroupViewModel(_payrollRunRepository);
        if (!await ShowDialogAsync(picker) || picker.ChosenRun is not { } run)
            return;

        List<Employee>? employees = null;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var (departments, unassigned) = await _rosterProvider.GetAsync(cancellationToken);

            var employeesByPin = departments.SelectMany(d => d.Employees)
                .Concat(unassigned)
                .ToDictionary(e => e.Pin);

            employees = [.. run.Employees
                .Select(runEmployee => employeesByPin.GetValueOrDefault(runEmployee.EmployeeId))
                .OfType<Employee>()];
        },
        onError: ex => ShowFailure(ex, "Could not load payroll group"));

        if (employees is null)
            return;

        if (employees.Count == 0)
        {
            StatusBar.ShowError(
                $"None of \"{run.Label}\"'s employees could be found -- they may have been deleted.",
                "Could not load payroll group");
            return;
        }

        LandOn(run, employees);
        StatusBar.ShowSuccess($"Loaded payroll run: {run.Label}");
    }

    /// <summary>
    /// Makes <paramref name="run"/> the tab's active batch -- its period (off the saved run, so
    /// this reflects exactly what's on disk), <paramref name="employees"/> as the batch, and its
    /// Id -- then selects the first of them, the same SelectedEmployee assignment an ordinary
    /// row click makes, so the person lands on a populated review screen for that employee.
    ///
    /// SelectedEmployee is cleared first: each of the four scope assignments re-triggers a
    /// refresh, so for a few notifications in a row SelectedEmployee, the period and
    /// ActivePayrollRunId are an inconsistent combination (the OLD employee against the NEW
    /// run's period). RequestRefresh's own guard clears Result for that, but HeaderText reads
    /// SelectedEmployee directly -- without this, the old employee's name would sit above a
    /// "Nothing to show yet." placeholder for a few render passes, reading as if their payroll
    /// had vanished.
    /// </summary>
    private void LandOn(PayrollRun run, List<Employee> employees)
    {
        _selection.SelectedEmployee = null;

        _scope.PeriodStart = run.PeriodStart.ToDateTime(TimeOnly.MinValue);
        _scope.PeriodEnd = run.PeriodEnd.ToDateTime(TimeOnly.MinValue);
        _scope.BatchScopeEmployees = employees;
        _scope.ActivePayrollRunId = run.Id;
        _selection.SelectedEmployee = employees[0];
    }
}
