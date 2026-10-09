using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// Backs the Schedule tab's left-hand Department/Employee tree (see
/// MainViewModel-Split-Plan.md's component list) -- the checkbox-driven multi-select tree,
/// plus everything that adds, edits, removes, or navigates the employees/departments shown in
/// it. Same tree shape as the Attendance tab's report-scope tree (ReportScopeViewModel), but its
/// own selection state: checking a box here marks someone for a bulk schedule assignment, not
/// for scoping a report, and every employee starts *unchecked* (bulk-assigning a schedule to the
/// whole company by default would be a footgun).
///
/// SelectedEmployee/SelectedDepartment (the tree's or the Employees grid's selection, not the
/// checkboxes) drive the single-employee calendar on the rest of the Schedule tab and are the
/// app-wide selection the Payroll tab follows (IEmployeeSelection). Sibling ViewModels react to
/// them for themselves -- ScheduleCalendarViewModel to SelectedEmployee,
/// ScheduleAssignmentViewModel to SelectedEmployeeCount.
/// </summary>
public partial class EmployeeTreeViewModel : ViewModelBase
{
    private readonly IScheduleRepository _repository;
    private readonly ViewStateStore _viewStateStore;

    /// <summary>Bumped after every add, edit, removal, or (un)blacklisting of an employee, and
    /// every department added or deleted (BumpRoster) -- the cache ActiveRosterProvider keeps
    /// for the Active-only roster reads elsewhere. This class is where all of those happen
    /// (Import Employees aside). Deleting an employee also bumps their schedule, since the
    /// cascade-deleted entries move their payroll figures.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>MainViewModel's SaveViewState, called after the selection changes so a revisit
    /// reopens on it.</summary>
    private readonly Action _saveViewState;

    /// <summary>The app-wide busy state -- the tree load is the Schedule tab's first database
    /// access each run, and goes through it like everything else touching the shared
    /// ScheduleDbContext.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Only for the Rest Day/Holiday premiums, shown as the Employee dialog's override
    /// placeholders.</summary>
    private readonly PayrollPolicy _payrollPolicy;

    /// <summary>Only for DefaultWorkTimeHours, shown as the Employee dialog's
    /// placeholder.</summary>
    private readonly AttendanceSettings _attendanceSettings;

    private readonly IObservable<bool> _hasDepartment;
    private readonly IObservable<bool> _hasEmployee;
    private readonly IObservable<bool> _canBlacklist;
    private readonly IObservable<bool> _canUnblacklist;
    private readonly IObservable<bool> _hasCheckedEmployees;

    public EmployeeTreeViewModel(
        IScheduleRepository repository,
        IStatusBarService statusBarService,
        ViewStateStore viewStateStore,
        AttendanceDataVersion dataVersion,
        Action saveViewState,
        AttendanceBusyState busy,
        PayrollPolicy payrollPolicy,
        AttendanceSettings attendanceSettings)
        : base(statusBarService)
    {
        _repository = repository;
        _viewStateStore = viewStateStore;
        _dataVersion = dataVersion;
        _saveViewState = saveViewState;
        _busy = busy;
        _payrollPolicy = payrollPolicy;
        _attendanceSettings = attendanceSettings;

        // Every checkbox change across the current tree, recounted -- a newly loaded tree
        // starts with nobody checked.
        _selectedEmployeeCountHelper = Observable.Switch(this.WhenAnyValue(x => x.LoadedTree)
                .Select(tree => EmployeeTreeBuilder.SelectionChanges(tree).StartWith(RxVoid.Default)))
            .Select(_ => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected))
            .ToProperty(this, x => x.SelectedEmployeeCount);

        _hasDepartment = this.WhenAnyValue(x => x.SelectedDepartment).Select(d => d is not null);
        _hasEmployee = this.WhenAnyValue(x => x.SelectedEmployee).Select(e => e is not null);
        _canBlacklist = this.WhenAnyValue(x => x.SelectedEmployee).Select(e => e is { IsBlacklisted: false });
        _canUnblacklist = this.WhenAnyValue(x => x.SelectedEmployee).Select(e => e is { IsBlacklisted: true });
        _hasCheckedEmployees = this.WhenAnyValue(x => x.SelectedEmployeeCount).Select(count => count > 0);

        ReportFailuresOf(LoadCommand, AddDepartmentCommand, DeleteDepartmentCommand, AddEmployeeCommand, EditEmployeeCommand,
            DeleteEmployeeCommand, BlacklistEmployeeCommand, UnblacklistEmployeeCommand, ClearEmployeeSelectionCommand);

        // Skip(1): nothing to save before anything's been selected.
        this.WhenAnyValue(x => x.SelectedEmployee, x => x.SelectedDepartment)
            .Skip(1)
            .Subscribe(_ => _saveViewState());
        this.WhenAnyValue(x => x.SearchText).Skip(1).Subscribe(_ => ApplySearchFilter());
    }

    /// <summary>Replaced in one change per load.</summary>
    public RangeObservableCollection<DepartmentGroupViewModel> Departments { get; } = [];

    /// <summary>The departments SearchText hasn't hidden, each showing its VisibleEmployees --
    /// what the Schedule tab's SfTreeView binds to (see EmployeeTreeSearchFilter.Apply).
    /// EmployeesPage deliberately binds to Departments instead, ignoring the filter.</summary>
    public ObservableCollection<DepartmentGroupViewModel> VisibleDepartments { get; } = [];

    /// <summary>The tree the last load built -- what the checked count follows.</summary>
    [Reactive]
    internal partial IReadOnlyList<DepartmentGroupViewModel> LoadedTree { get; private set; } = [];

    [Reactive]
    public partial Employee? SelectedEmployee { get; set; }

    [Reactive]
    public partial Department? SelectedDepartment { get; set; }

    /// <summary>A row picked in the Schedule tree or the Employees page: an employee selects
    /// them and their department; a department selects it alone (none for the "(Unassigned)"
    /// bucket); nothing -- the Employees grid's row cleared -- just drops the employee.</summary>
    [ReactiveCommand]
    public void SelectNode(object? node)
    {
        switch (node)
        {
            case EmployeeNodeViewModel employeeNode:
                SelectedEmployee = employeeNode.Employee;
                SelectedDepartment = employeeNode.Employee.Department;
                break;

            case DepartmentGroupViewModel group:
                SelectedDepartment = group.RealDepartment;
                SelectedEmployee = null;
                break;

            default:
                SelectedEmployee = null;
                break;
        }
    }

    /// <summary>Free-text filter for the tree, comma-separated like the Attendance tab's search
    /// boxes -- each term matched against Employee ID, first name, last name, or department,
    /// terms OR'd together (EmployeeTreeSearchFilter). Narrows which nodes are visible, never
    /// which are checked, so typing never changes what a bulk assignment would cover.</summary>
    [Reactive]
    public partial string SearchText { get; set; } = string.Empty;

    private void ApplySearchFilter() => EmployeeTreeSearchFilter.Apply(Departments, SearchText, VisibleDepartments);

    /// <summary>Set once the saved employee/department selection has been applied, the first
    /// load this run -- so a later reload (after an import) doesn't overwrite what the person
    /// has selected since. MainViewModel.SaveViewState checks it before writing.</summary>
    public bool HasRestoredSelection { get; private set; }

    /// <summary>Employees checked in the tree, across every department (Unassigned
    /// included).</summary>
    [ObservableAsProperty]
    public partial int SelectedEmployeeCount { get; }

    /// <summary>Real departments only -- for pickers where "Unassigned" isn't a choice.</summary>
    public IEnumerable<Department> RealDepartments =>
        Departments.Where(g => g.RealDepartment is not null).Select(g => g.RealDepartment!);

    /// <summary>
    /// Rebuilds the tree from the database -- on every visit to the Schedule page, after an
    /// import, and after each command below writes. Under the app-wide busy state (silently):
    /// without it, switching tabs in the second this runs could start a concurrent operation on
    /// the shared ScheduleDbContext.
    /// </summary>
    [ReactiveCommand]
    public async Task LoadAsync()
    {
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            var departments = await _repository.GetDepartmentsWithEmployeesAsync(cancellationToken);
            var unassigned = await _repository.GetUnassignedEmployeesAsync(cancellationToken);

            var tree = EmployeeTreeBuilder.Build(departments, unassigned);
            Departments.ReplaceAll(tree);
            LoadedTree = tree;

            if (!HasRestoredSelection)
            {
                HasRestoredSelection = true;
                RestoreSelectionFromViewState();
            }

            // Fresh nodes all start visible, so a reload under a typed search term re-hides
            // whatever doesn't match rather than briefly showing everyone.
            ApplySearchFilter();
        }, onError: ex => ShowFailure(ex, "Couldn't load employees"));
    }

    /// <summary>Re-applies the employee or department in the session-only ViewStateStore, the
    /// first time the tree is built this run. Silently nothing if it no longer exists --
    /// reopening where you left off is a convenience, not a guarantee.</summary>
    private void RestoreSelectionFromViewState()
    {
        var state = _viewStateStore.Schedule;

        if (state.SelectedEmployeeId is int employeeId
            && Departments.SelectMany(d => d.Employees).FirstOrDefault(n => n.Employee.Id == employeeId) is { } node)
        {
            SelectedEmployee = node.Employee;
            SelectedDepartment = node.Employee.Department;
            return;
        }

        if (state.SelectedDepartmentId is int departmentId && RealDepartments.FirstOrDefault(d => d.Id == departmentId) is { } department)
            SelectedDepartment = department;
    }

    /// <summary>The checked employees -- the bulk-assignment target list -- leaving out a
    /// blacklisted one even if their node is somehow still checked (their row is hidden, but
    /// the list itself guards it rather than relying on the UI).</summary>
    public List<Employee> GetCheckedEmployees() =>
        [.. Departments.SelectMany(d => d.Employees)
            .Where(n => n.IsSelected && !n.Employee.IsBlacklisted)
            .Select(n => n.Employee)];

    [ReactiveCommand]
    private async Task AddDepartmentAsync()
    {
        var prompt = new TextPromptViewModel("New Department", "Department name:");
        if (!await ShowDialogAsync(prompt)) return;

        await _repository.AddDepartmentAsync(prompt.Value);
        _dataVersion.BumpRoster();
        await LoadAsync();
    }

    /// <summary>Enabled while a real department (not the "(Unassigned)" bucket) is
    /// selected.</summary>
    [ReactiveCommand(CanExecute = nameof(_hasDepartment))]
    private async Task DeleteDepartmentAsync()
    {
        if (SelectedDepartment is not { } department) return;

        if (!await ConfirmAsync(
                $"Delete department \"{department.Name}\"?\n\nIts employees will become unassigned, not deleted -- " +
                "their schedules are kept.",
                "Confirm delete", isWarning: true))
            return;

        await _repository.DeleteDepartmentAsync(department.Id);
        _dataVersion.BumpRoster();
        SelectedDepartment = null;
        SelectedEmployee = null;
        await LoadAsync();
    }

    [ReactiveCommand]
    private Task AddEmployeeAsync() =>
        SaveEmployeeAsync(
            new EmployeeEditorViewModel([.. RealDepartments], SelectedEmployee?.DepartmentId ?? SelectedDepartment?.Id,
                _payrollPolicy, _attendanceSettings.DefaultWorkTimeHours, takenPins: TakenPins()),
            details => _repository.AddEmployeeAsync(details.LastName, details.FirstName, details.DepartmentId, details.Pin,
                details.QualifiesForOvertime, details.QualifiesForNightDiff, details.DailyRate, details.DefaultLeaveIsPaid,
                details.ApplyOvertimeRatePercentageByDefault,
                details.DefaultSss, details.DefaultPhilHealth, details.DefaultPagIbig,
                details.DefaultPremiumPay, details.DefaultAllowance, details.DefaultCashAdvance,
                details.EmployeeType, details.MonthlyRate, details.RestDayWorkPremiumPercentage,
                details.QualifiesForRestDayPay, details.QualifiesForPremiumPay,
                details.ClockInBufferBeforeHours, details.ClockInBufferAfterHours,
                details.ClockOutBufferBeforeHours, details.ClockOutBufferAfterHours,
                details.HolidayPremiumPercentage, details.DefaultWorkTimeHours, details.ExemptFromUndertimeDeduction));

    /// <summary>Enabled while an employee is selected, as is Delete.</summary>
    [ReactiveCommand(CanExecute = nameof(_hasEmployee))]
    private Task EditEmployeeAsync()
    {
        if (SelectedEmployee is not { } employee) return Task.CompletedTask;

        return SaveEmployeeAsync(
            new EmployeeEditorViewModel([.. RealDepartments], employee.DepartmentId, _payrollPolicy,
                _attendanceSettings.DefaultWorkTimeHours, employee, TakenPins(excludingEmployeeId: employee.Id)),
            details => _repository.UpdateEmployeeAsync(
                employee.Id, details.LastName, details.FirstName, details.DepartmentId, details.Pin,
                details.QualifiesForOvertime, details.QualifiesForNightDiff, details.DailyRate, details.DefaultLeaveIsPaid,
                details.ApplyOvertimeRatePercentageByDefault,
                details.DefaultSss, details.DefaultPhilHealth, details.DefaultPagIbig,
                details.DefaultPremiumPay, details.DefaultAllowance, details.DefaultCashAdvance,
                details.EmployeeType, details.MonthlyRate, details.RestDayWorkPremiumPercentage,
                details.QualifiesForRestDayPay, details.QualifiesForPremiumPay,
                details.ClockInBufferBeforeHours, details.ClockInBufferAfterHours,
                details.ClockOutBufferBeforeHours, details.ClockOutBufferAfterHours,
                details.HolidayPremiumPercentage, details.DefaultWorkTimeHours, details.ExemptFromUndertimeDeduction));
    }

    /// <summary>Shows the Employee dialog, and writes what it settles on.</summary>
    private async Task SaveEmployeeAsync(EmployeeEditorViewModel editor, Func<EmployeeDetails, Task> save)
    {
        if (!await ShowDialogAsync(editor) || editor.AcceptedDetails is not { } details) return;

        try
        {
            await save(details);
        }
        catch (DuplicateEmployeeIdException ex)
        {
            // The dialog already checks against what was loaded when it opened -- this only
            // fires if someone else took that ID in the meantime.
            StatusBar.ShowCaution(ex.Message, "Duplicate Employee ID");
            return;
        }

        _dataVersion.BumpRoster();
        await LoadAsync();
    }

    /// <summary>Employee IDs in use, optionally not counting one employee's own (while editing
    /// them).</summary>
    private HashSet<int> TakenPins(int? excludingEmployeeId = null) =>
        [.. Departments.SelectMany(d => d.Employees)
            .Select(n => n.Employee)
            .Where(e => e.Id != excludingEmployeeId)
            .Select(e => e.Pin)];

    [ReactiveCommand(CanExecute = nameof(_hasEmployee))]
    private async Task DeleteEmployeeAsync()
    {
        if (SelectedEmployee is not { } employee) return;

        if (!await ConfirmAsync(
                $"Delete employee \"{employee.DisplayName}\" and all of their schedule entries? This cannot be undone.",
                "Confirm delete", isWarning: true))
            return;

        SelectedEmployee = null;
        await _repository.DeleteEmployeeAsync(employee.Id);

        // Per-employee, not a plain BumpSchedule(): the cascade-deleted schedule entries move
        // this employee's payroll to zero, and a bump with no per-pin entry is invisible to a
        // loaded payroll group's AnyScheduleChangeSince check.
        _dataVersion.BumpScheduleForEmployees([employee.Pin]);
        _dataVersion.BumpRoster();
        await LoadAsync();
    }

    /// <summary>Hides the employee from the Schedule/Attendance trees and active-only pickers
    /// going forward, keeping all their history -- see Employee.IsBlacklisted. Enabled only
    /// for one who isn't blacklisted yet; Unblacklist the other way round.</summary>
    [ReactiveCommand(CanExecute = nameof(_canBlacklist))]
    private async Task BlacklistEmployeeAsync()
    {
        if (SelectedEmployee is not { } employee) return;

        if (!await ConfirmAsync(
                $"Blacklist employee \"{employee.DisplayName}\"? " +
                "They'll be hidden from the Schedule and Attendance trees and left out of new payroll rosters, " +
                "but their existing records are kept, and they'll still appear here so they can be unblacklisted later.",
                "Confirm blacklist", isWarning: true))
            return;

        await SetBlacklistedAsync(employee.Id, blacklisted: true);
    }

    [ReactiveCommand(CanExecute = nameof(_canUnblacklist))]
    private async Task UnblacklistEmployeeAsync()
    {
        if (SelectedEmployee is { } employee)
            await SetBlacklistedAsync(employee.Id, blacklisted: false);
    }

    /// <summary>Re-points SelectedEmployee at the reloaded node afterwards -- otherwise it
    /// would keep the pre-toggle Employee, and the two commands' CanExecute its old
    /// IsBlacklisted.</summary>
    private async Task SetBlacklistedAsync(int employeeId, bool blacklisted)
    {
        await _repository.SetEmployeeBlacklistAsync(employeeId, blacklisted);
        _dataVersion.BumpRoster();
        await LoadAsync();

        if (Departments.SelectMany(d => d.Employees).FirstOrDefault(n => n.Employee.Id == employeeId) is { } node)
            SelectedEmployee = node.Employee;
    }

    /// <summary>Unchecks every employee -- also called by MainViewModel whenever multi-select
    /// mode ends. Enabled while anyone is checked.</summary>
    [ReactiveCommand(CanExecute = nameof(_hasCheckedEmployees))]
    public void ClearEmployeeSelection()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = false;
    }
}
