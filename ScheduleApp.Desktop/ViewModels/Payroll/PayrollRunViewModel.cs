using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 3 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel as
/// build-order step 4. Owns the Payroll Run lifecycle: starting a fresh run via the wizard
/// (<see cref="NewPayrollRun"/>) and reopening a run saved in a past session, or earlier this
/// one (<see cref="LoadPayrollGroupAsync"/>) -- see PayrollGroupViewModel's own doc comment for
/// the sibling concern (concern 2) this differs from: Group owns the *table* of who's currently
/// in the batch and lets that membership be edited afterward; this class owns the two
/// *lifecycle* moments that establish or wholesale-replace that batch in the first place (a
/// fresh wizard Finish, or reopening an old run's saved membership). Unlike Group, this class
/// needs no reference to PayrollSummaryViewModel or PayrollGroupViewModel themselves -- see the
/// refactor plan's own "Dependency graph": PayrollRunViewModel and PayrollPrintExportViewModel
/// are the two concerns that stay fully decoupled from Summary and Group, touching only
/// PayrollScopeState, AttendanceBusyState, _mainViewModel, and their own repositories.
///
/// PayrollPage.xaml's "New Payroll Run…"/"Load Payroll Group…" toolbar buttons are bound
/// directly to PayrollViewModel (the facade), never to this class -- so NewPayrollRunCommand/
/// LoadPayrollGroupCommand are forwarded back out under the same name via PayrollViewModel.Run
/// (see that class's own "Forwarded members (PayrollRunViewModel)" region). Nothing in this
/// class itself needs to know that.
///
/// Takes PayrollScopeState and AttendanceBusyState the same shared-not-owned way
/// PayrollSummaryViewModel/PayrollGroupViewModel do (see either class's own doc comment and the
/// refactor plan's own "Dependency graph") -- both constructed on PayrollViewModel and passed in
/// here. Also takes ActiveRosterProvider/IPayrollRunRepository/IPayrollComputationService, all
/// three purely to hand off to PayrollWizardDialog/LoadPayrollGroupDialog or to resolve a loaded
/// run's Pins back to Employee objects -- ActiveRosterProvider replaces the raw
/// IScheduleRepository this class used to take, the same "constructor-injected just to hand off
/// to a dialog" convention PayrollViewModel's own _rosterProvider doc comment already describes
/// for PayslipScopeDialog/PayrollWizardDialog -- see ActiveRosterProvider's own doc comment for
/// why LoadPayrollGroupAsync's own roster read below now goes through the shared,
/// RosterVersion-gated cache too, instead of paying for its own
/// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync round trip. Also
/// takes IStatusBarService, for the
/// success/failure messages both methods below report through -- the refactor plan's own
/// "Dependency graph" table leaves this one off PayrollRunViewModel's dependency list, but both
/// NewPayrollRun and LoadPayrollGroupAsync genuinely need it (see each method's own final
/// line/onError callback), so it's included here as a correction rather than followed to the
/// letter.</summary>
public partial class PayrollRunViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollRunRepository _payrollRunRepository;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IStatusBarService _statusBarService;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _busy doc comment for why this is one instance, not one per child.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _scope doc comment. This class reads/writes PeriodStart/PeriodEnd/
    /// BatchScopeEmployees/ActivePayrollRunId straight off this instance rather than through
    /// any forwarding property of its own, the same way PayrollSummaryViewModel/
    /// PayrollGroupViewModel already do.</summary>
    private readonly PayrollScopeState _scope;

    public PayrollRunViewModel(
        MainViewModel mainViewModel,
        ActiveRosterProvider rosterProvider,
        IPayrollRunRepository payrollRunRepository,
        IPayrollComputationService payrollComputationService,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope)
    {
        _mainViewModel = mainViewModel;
        _rosterProvider = rosterProvider;
        _payrollRunRepository = payrollRunRepository;
        _payrollComputationService = payrollComputationService;
        _statusBarService = statusBarService;
        _busy = busy;
        _scope = scope;

        // Trimmed to just this class's own concern (NewPayrollRunCommand/
        // LoadPayrollGroupCommand) as of build-order step 4. At the time, PayrollViewModel's
        // own NotifyToolbarCommands still covered PrintPayslipsCommand/ExportPayrollReportCommand
        // from its own, independent _busy.PropertyChanged subscription, pending their own
        // extraction -- build-order step 5 has since moved those two onto
        // PayrollPrintExportViewModel's own NotifyPrintExportCommands()/_busy.PropertyChanged
        // subscription and retired NotifyToolbarCommands entirely (see the facade's own
        // constructor doc comment). Same "PropertyChanged -> NotifyCanExecuteChanged,
        // immediately, every time, no artificial hold" behavior as that one -- see its own doc
        // comment for why smoothing this independently never actually worked.
        //
        // No pending-refresh flag to check afterward the way PayrollSummaryViewModel's own
        // _refreshPending/PayrollGroupViewModel's own _payrollGroupRefreshPending/
        // _fullRosterRefreshPending are -- neither NewPayrollRun nor LoadPayrollGroupAsync is
        // ever triggered by a _scope change arriving mid-refresh the way
        // RequestRefresh()/RequestPayrollGroupRefresh() are; both only ever run from a direct
        // button click, so there's nothing here that could go stale while _busy.IsRunning was
        // already true.
        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AttendanceBusyState.IsRunning)) return;
            NotifyRunCommands();
        };
    }

    /// <summary>The two command notifications gated on _busy.IsRunning that this class owns --
    /// factored out so the constructor's _busy.PropertyChanged handler above is the only
    /// caller, same "one list, not two copies that could drift apart" reasoning
    /// PayrollGroupViewModel's own NotifyGroupCommands()/PayrollSummaryViewModel's own
    /// NotifyAdjustmentCommands() already follow.</summary>
    private void NotifyRunCommands()
    {
        NewPayrollRunCommand.NotifyCanExecuteChanged();
        LoadPayrollGroupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Build-order step 9's own entry point for starting a payroll run --
    /// "New Payroll Run…" on PayrollPage (see PayrollWizardDialog's own doc comment for
    /// the wizard itself). This replaces the old "Start Payroll Period…" flow --
    /// StartPayrollPeriodAsync, CanStartPayrollPeriod, and SeedDefaultContributionsForPeriodAsync
    /// are gone as of build-order step 9.4, along with the button that called them and the
    /// "(wizard preview)" suffix this command's own button used to carry while the two sat
    /// side by side. There's no seed step to carry over: IPayrollComputationService.
    /// ComputeOneAsync already seeds SSS/PhilHealth/Pag-IBIG/Allowance/Premium Pay/Cash
    /// Advance defaults (see PayrollComputationService.ContributionDefaultsFor/
    /// SeedOrReseedContributionsAsync) internally, per employee, every time it's called,
    /// so Step 3's own compute loop over
    /// selected employees (see PayrollWizardViewModel) *is* the seeding -- StartPayrollPeriodAsync's
    /// separate seed pass only ever existed because the old flow had nothing else that
    /// called ComputeOneAsync up front.
    ///
    /// Passes _rosterProvider and _payrollRunRepository through, plus this tab's own
    /// PeriodStart/PeriodEnd (off _scope) as the wizard's starting period -- Step 1's period
    /// fields and Step 2's employee tree (built from _rosterProvider the same way
    /// PayslipScopeViewModel's own tree is) both need somewhere real to start from rather
    /// than testing against always-empty defaults. Owner set the same way every other modal
    /// dialog on this tab is, so it centers over MainWindow and blocks input to it while
    /// open.
    ///
    /// Once ShowDialog() returns true with a non-null SavedRun (Step 3's own "Save Payroll
    /// Group" button, build-order step 8.3, already wrote the PayrollRun by then -- see
    /// PayrollWizardViewModel.SavePayrollGroupCommand), this sets PeriodStart/PeriodEnd (off
    /// the saved run, not the wizard's own in-progress fields, so this reflects exactly
    /// what's on disk), BatchScopeEmployees, ActivePayrollRunId, and
    /// _mainViewModel.SelectedEmployee = employees[0] -- the same SelectedEmployee
    /// assignment EmployeeTree_OnSelectedItemChanged already makes from an ordinary tree
    /// click (see PayrollPage.xaml.cs), so it re-triggers the exact same
    /// RequestRefresh()/RefreshScheduleForSelectedEmployeeAsync cascade a normal selection
    /// already does -- the person lands on a populated review screen for that employee
    /// instead of wherever (or nothing) was selected before. BatchScopeEmployees comes from
    /// PayrollWizardDialog.SelectedEmployees (the Employee objects Step 2's tree already has
    /// in hand) rather than resolving SavedRun.Employees' Pins back through the DB -- that
    /// round trip belongs to step 10 (Load Payroll Group…, reopening a run from a past
    /// session where nothing is "already in hand"), not this one. There's no reentrancy
    /// hazard to manage first: by the time ShowDialog() returns, the wizard's own async work
    /// (LoadEmployeeTreeCommand, SavePayrollGroupCommand) is long finished -- a modal dialog
    /// only closes once RequestClose fires from a synchronous Finish click.
    ///
    /// The status-bar message just confirms the run was saved by its Label, with no
    /// seeded/already-had counts the way the old flow's message had -- ComputeOneAsync
    /// doesn't return that kind of count, and Step 3's review grid is already the
    /// confirmation that the numbers are right.
    ///
    /// Cancel, or a clean Finish with nothing ever saved on Step 3, both leave SavedRun
    /// null, so none of this runs and PeriodStart/PeriodEnd/BatchScopeEmployees/
    /// SelectedEmployee are all left untouched -- nothing was actually confirmed, so
    /// there's nothing to reflect.
    ///
    /// Same !_busy.IsRunning-only guard as PayrollPrintExportViewModel's own
    /// CanPrintPayslips/CanExportPayrollReport, and for the same underlying reason even
    /// though this command's own body never touches the database directly:
    /// PayrollWizardDialog's Loaded handler fires LoadEmployeeTreeCommand the moment it
    /// opens, which reads through this same _rosterProvider -- backed by the one
    /// shared, app-lifetime-scoped ScheduleDbContext instance
    /// PayrollPrintExportViewModel.PrintPayslipsAsync/ExportPayrollReportAsync's own scope
    /// dialogs already read through -- so letting this dialog open mid-refresh would risk
    /// the exact "second operation started on this context instance before a previous
    /// operation completed" race _busy exists to prevent (see _busy's own doc comment),
    /// just via the wizard's tree load instead of PayslipScopeDialog's.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNewPayrollRun))]
    private void NewPayrollRun()
    {
        var wizardDialog = new PayrollWizardDialog(
            _rosterProvider, _payrollRunRepository, _payrollComputationService,
            _scope.PeriodStart, _scope.PeriodEnd)
        {
            Owner = Application.Current.MainWindow,
        };

        // != true (not just checking SavedRun directly) since Cancel also leaves the
        // wizard's DataContext instance around with whatever partial state it had --
        // ShowDialog returning true is what actually distinguishes "closed via Finish"
        // from "closed via Cancel", same guard PrintPayslipsAsync/ExportPayrollReportAsync
        // already use on their own scope dialogs' result.
        if (wizardDialog.ShowDialog() != true || wizardDialog.SavedRun is not { } run) return;

        // Off the saved run, not the wizard's own PeriodStart/PeriodEnd fields -- see this
        // method's own doc comment for why that's the one that reflects what's actually on
        // disk. SelectedEmployees is read before the assignments below so employees[0] and
        // BatchScopeEmployees are guaranteed to come from the exact same tree state Save
        // Payroll Group itself saved from.
        var employees = wizardDialog.SelectedEmployees;

        // Cleared first, ahead of the PeriodStart/PeriodEnd/BatchScopeEmployees/
        // ActivePayrollRunId cascade below -- same "don't leave a stale employee's name
        // sitting above a 'Nothing to show yet.' placeholder for a few intermediate render
        // passes" fix LoadPayrollGroupAsync's own landing sequence needs, and for the exact
        // same reason: this method's five assignments have the identical shape (each one's
        // own partial-method handler calls RequestRefresh() again), so SelectedEmployee,
        // PeriodStart/PeriodEnd, and ActivePayrollRunId are transiently inconsistent here
        // too, and HeaderText reads SelectedEmployee directly with no guard of its own. See
        // that method's own doc comment on this same line for the full explanation.
        _mainViewModel.SelectedEmployee = null;

        _scope.PeriodStart = run.PeriodStart.ToDateTime(TimeOnly.MinValue);
        _scope.PeriodEnd = run.PeriodEnd.ToDateTime(TimeOnly.MinValue);
        _scope.BatchScopeEmployees = employees;
        _scope.ActivePayrollRunId = run.Id;
        _mainViewModel.SelectedEmployee = employees[0];

        _statusBarService.ShowSuccess($"Saved payroll run: {run.Label}");
    }

    /// <summary>Same !_busy.IsRunning-only guard as PayrollViewModel's own CanPrintPayslips/
    /// CanExportPayrollReport, and for the same reason -- see NewPayrollRun's own doc
    /// comment.</summary>
    private bool CanNewPayrollRun() => !_busy.IsRunning;

    /// <summary>Build-order step 10's own entry point -- "Load Payroll Group…" on
    /// PayrollPage, reopening a run saved in a past session (or earlier this one) --
    /// see LoadPayrollGroupDialog's own doc comment for the dialog itself. Opens it
    /// entirely outside the _busy window, same "dialog shown before _busy.RunAsync
    /// starts" reasoning NewPayrollRun above follows for PayrollWizardDialog --
    /// Cancel-ing the list dialog never touches _busy.IsRunning at all.
    ///
    /// Once a run is picked (ShowDialog() returns true with a non-null SelectedRun),
    /// resolves that run's PayrollRunEmployee.EmployeeId Pins back to actual Employee
    /// objects -- the round trip NewPayrollRun's own doc comment calls out as this
    /// method's job, not its own: there's no tree already "in hand" the way the
    /// wizard's Step 2 has one, since this run may have been saved in an earlier
    /// session entirely. Reads the same department/unassigned-employee data
    /// EmployeeTreeBuilder itself builds its tree from, off _rosterProvider now instead of
    /// a direct IScheduleRepository read (see ActiveRosterProvider's own doc comment),
    /// just flattened and matched by Pin rather than shaped into a tree -- there's no
    /// checkbox UI here to justify building one. Runs inside _busy.RunAsync the same
    /// way every other read/write in this class does, so the progress bar shows
    /// something's happening and every busy-gated command disables itself while this
    /// resolves.
    ///
    /// A Pin on the saved run that no longer matches any current *active* employee
    /// (e.g. that employee was deleted, or has since been blacklisted, since the run
    /// was saved) is silently dropped rather than failing the whole load -- same "best effort over all-or-nothing" spirit
    /// PayslipScopeViewModel.GetSelectedEmployees' own Pin-less-employee exclusion
    /// follows, just going the other direction (a Pin with no matching employee,
    /// instead of an employee with no Pin). If that drops every employee -- the run's
    /// entire membership has since been deleted -- there's nothing usable to load,
    /// which is reported as its own error rather than silently setting
    /// BatchScopeEmployees to an empty list; an empty batch would look identical to
    /// nothing having loaded at all. That empty-result case is distinguished from a
    /// failed round trip by employees staying null (not an empty list) when the
    /// _busy.RunAsync block itself throws -- same "null means the onError callback
    /// already reported it, don't report a second time" convention
    /// PrintPayslipsAsync/ExportPayrollReportAsync's own results checks already use.
    ///
    /// Once resolved, sets PeriodStart/PeriodEnd/BatchScopeEmployees/ActivePayrollRunId,
    /// and _mainViewModel.SelectedEmployee = employees[0] -- the exact same five
    /// assignments NewPayrollRun makes on Finish (see that method's own doc comment for
    /// why each one matters), so a loaded run looks and behaves identically to one just
    /// saved this session: same checklist, same review-screen landing.
    ///
    /// Same !_busy.IsRunning-only guard as CanNewPayrollRun above and PayrollViewModel's
    /// own CanPrintPayslips/CanExportPayrollReport, for the same reason --
    /// LoadPayrollGroupDialog's own Loaded handler fires a ListAsync the moment it opens,
    /// and this method's own resolve step reads through the same shared,
    /// app-lifetime-scoped ScheduleDbContext instance every other dialog on this tab
    /// already reads through, so letting either happen mid-refresh would risk the same
    /// race _busy exists to prevent.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadPayrollGroup))]
    private async Task LoadPayrollGroupAsync()
    {
        var listDialog = new LoadPayrollGroupDialog(_payrollRunRepository)
        {
            Owner = Application.Current.MainWindow,
        };

        if (listDialog.ShowDialog() != true || listDialog.SelectedRun is not { } run) return;

        List<Employee>? employees = null;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var (departments, unassigned) = await _rosterProvider.GetAsync(cancellationToken);

            var employeesByPin = departments.SelectMany(d => d.Employees)
                .Concat(unassigned)
                .ToDictionary(e => e.Pin);

            employees = [.. run.Employees
                .Select(runEmployee => employeesByPin.GetValueOrDefault(runEmployee.EmployeeId))
                .Where(e => e is not null)
                .Select(e => e!)];
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not load payroll group"));

        // null means the block above threw and onError already reported it -- see this
        // method's own doc comment for why that's distinct from an empty (but non-null)
        // result below.
        if (employees is null) return;

        if (employees.Count == 0)
        {
            _statusBarService.ShowError(
                $"None of \"{run.Label}\"'s employees could be found -- they may have been deleted.",
                "Could not load payroll group");
            return;
        }

        // Cleared first, ahead of the PeriodStart/PeriodEnd/BatchScopeEmployees/
        // ActivePayrollRunId cascade below, rather than left holding whatever employee was
        // selected before this run was loaded (from this tab's own previous group, or a
        // stale selection carried over from Schedule/Employees) -- each of those four
        // assignments fires its own partial-method handler (OnPeriodStartChanged/
        // OnPeriodEndChanged/OnBatchScopeEmployeesChanged/OnActivePayrollRunIdChanged), and
        // every one of them calls RequestRefresh() again, so for a few property-changed
        // notifications in a row here SelectedEmployee, PeriodStart/PeriodEnd, and
        // ActivePayrollRunId are a genuinely inconsistent combination (e.g. the OLD selected
        // employee against the NEW run's period, or against ActivePayrollRunId already
        // pointing at the new run but BatchScopeEmployees not yet updated to include them).
        // RequestRefresh's own guard clears Result for exactly that kind of combination, but
        // HeaderText reads SelectedEmployee directly and has no equivalent guard -- without
        // this, that leaves the old employee's name sitting above PayrollSummaryView's
        // "Nothing to show yet."/"Select an employee…" placeholder for however many WPF
        // render passes this cascade spans, which reads as if their payroll just vanished
        // rather than as "a new group is being loaded". Setting this to null first makes
        // every one of those intermediate states -- not just the final one -- show "Select
        // an employee to see their payroll breakdown." instead, until the very last
        // assignment below (_mainViewModel.SelectedEmployee = employees[0]) lands the new
        // group's first employee for real. NewPayrollRun's own five-assignment landing
        // sequence has the exact same shape and gets the exact same fix, for the same
        // reason.
        _mainViewModel.SelectedEmployee = null;

        _scope.PeriodStart = run.PeriodStart.ToDateTime(TimeOnly.MinValue);
        _scope.PeriodEnd = run.PeriodEnd.ToDateTime(TimeOnly.MinValue);
        _scope.BatchScopeEmployees = employees;
        _scope.ActivePayrollRunId = run.Id;
        _mainViewModel.SelectedEmployee = employees[0];

        _statusBarService.ShowSuccess($"Loaded payroll run: {run.Label}");
    }

    /// <summary>Same !_busy.IsRunning-only guard as CanNewPayrollRun above, and for the
    /// same reason -- see LoadPayrollGroupAsync's own doc comment.</summary>
    private bool CanLoadPayrollGroup() => !_busy.IsRunning;
}
