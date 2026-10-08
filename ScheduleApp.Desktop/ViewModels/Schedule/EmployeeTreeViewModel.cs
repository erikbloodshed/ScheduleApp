using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// Backs the Schedule tab's left-hand Department/Employee tree (see
/// MainViewModel-Split-Plan.md's component list) -- the checkbox-driven
/// multi-select tree, plus everything that adds, edits, removes, or navigates the
/// employees/departments shown in it. Same tree shape as the Attendance tab's
/// report-scope tree (see ReportScopeViewModel, the direct precedent this class
/// follows), but its own separate instance/selection state: checking a box here
/// marks someone for a bulk schedule assignment, not for scoping a report, and
/// unlike that tree, every employee here starts *unchecked* (bulk-assigning a
/// schedule to the whole company by default would be a footgun the report tab
/// doesn't have to worry about).
///
/// SelectedEmployee/SelectedDepartment (the tree's or the Employees grid's selection,
/// not the per-row checkboxes) drive the single-employee calendar view on the rest of
/// the Schedule tab, and are also what EmployeesPage's editor buttons and
/// PayrollViewModel's own reactivity (it filters PropertyChanged on
/// nameof(SelectedEmployee)) key off of. Sibling ViewModels react to them for themselves
/// -- ScheduleCalendarViewModel subscribes to SelectedEmployee, ScheduleAssignmentViewModel
/// to SelectedEmployeeCount -- rather than this class reaching out to them.
///
/// A ReactiveUI ViewModel (ViewModelBase): each command's CanExecute follows the
/// selection it depends on through WhenAnyValue, a failure is shown on the status bar
/// and logged rather than escaping a command, and the Yes/No questions go through
/// Confirm, which EmployeesPage answers with a MessageBox. ReactiveObject raises
/// PropertyChanged as before, so the siblings' subscriptions are unchanged.
/// </summary>
public class EmployeeTreeViewModel : ViewModelBase
{
    private readonly IScheduleRepository _repository;
    private readonly ViewStateStore _viewStateStore;

    /// <summary>Bumped after every command below that adds, edits, removes, or
    /// (un)blacklists an employee, or adds/deletes a department (BumpRoster) -- the cache
    /// ActiveRosterProvider keeps for ReportScopeViewModel/PayslipScopeViewModel/
    /// PayrollWizardViewModel/PayrollRunViewModel/PayrollGroupViewModel's Active-only
    /// roster reads (see that class's own doc comment). This class is the only place any
    /// of those seven mutations happen, so it's the only place RosterVersion needs bumping
    /// from -- see ScheduleImportExportViewModel.ImportEmployeesAsync for the one
    /// roster-mutating write that happens outside this class entirely.
    ///
    /// DeleteEmployeeAsync also bumps the deleted employee's schedule
    /// (BumpScheduleForEmployees with their Pin), since the cascade-deleted schedule
    /// entries move that employee's payroll figures, and the Attendance Summary tab's own
    /// auto-reload-on-tab-select (see ReportViewModel.ShouldAutoReload) needs to see that
    /// -- see AttendanceDataVersion's own doc comment, which documents this exact call
    /// site by name.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>MainViewModel's own SaveViewState, passed in as a delegate rather than
    /// this class reaching back out to its facade -- same shape as
    /// ManualEntriesViewModel/PunchRecordsViewModel's own _saveViewState (see either's
    /// constructor). Called after SelectedEmployee/SelectedDepartment change so a
    /// revisit reopens on the same selection; NOT called from LoadAsync itself, since
    /// that already runs during the facade's own constructor-time DisplayedMonth
    /// assignment on the very first load, before HasRestoredSelection has had a chance
    /// to flip true (see that property's own doc comment, and MainViewModel.SaveViewState's,
    /// for the guard this mirrors).</summary>
    private readonly Action _saveViewState;

    /// <summary>The *same* instance MainViewModel receives (see App.xaml.cs's registration
    /// and ScheduleCalendarViewModel._busy's own doc comment) -- shared across Schedule/
    /// Payroll/Attendance since they all read/write the one shared, app-lifetime-scoped
    /// ScheduleDbContext. LoadAsync below is the one DB-touching call in this class that
    /// runs on its own (the commands' writes are each followed by it) -- see LoadAsync's
    /// own doc comment for the concurrent-DbContext crash going around this guard could
    /// produce against the Attendance/Payroll tabs during the first second or two after
    /// sign-in, while this method is still running.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Read only for RestDayPremiumPercentage/HolidayPremiumPercentage, handed
    /// straight to EmployeeDialog so its per-employee override boxes can show the
    /// company's current default as a live placeholder -- see EmployeeDialog's own
    /// constructor doc comment.</summary>
    private readonly PayrollPolicy _payrollPolicy;

    /// <summary>Read only for DefaultWorkTimeHours, handed straight to EmployeeDialog so
    /// its own DefaultWorkTimeHoursBox can show the company's current default as a live
    /// placeholder -- same reasoning as _payrollPolicy just above.</summary>
    private readonly AttendanceSettings _attendanceSettings;

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

        var hasEmployee = this.WhenAnyValue(x => x.SelectedEmployee).Select(e => e is not null);

        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AddDepartmentCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(AddDepartmentAsync));
        DeleteDepartmentCommand = ReactiveCommand.CreateFromTask(
            () => RunSafelyAsync(DeleteDepartmentAsync),
            this.WhenAnyValue(x => x.SelectedDepartment).Select(d => d is not null));
        AddEmployeeCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(AddEmployeeAsync));
        EditEmployeeCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(EditEmployeeAsync), hasEmployee);
        DeleteEmployeeCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(DeleteEmployeeAsync), hasEmployee);
        BlacklistEmployeeCommand = ReactiveCommand.CreateFromTask(
            () => RunSafelyAsync(BlacklistEmployeeAsync),
            this.WhenAnyValue(x => x.SelectedEmployee).Select(e => e is { IsBlacklisted: false }));
        UnblacklistEmployeeCommand = ReactiveCommand.CreateFromTask(
            () => RunSafelyAsync(UnblacklistEmployeeAsync),
            this.WhenAnyValue(x => x.SelectedEmployee).Select(e => e is { IsBlacklisted: true }));
        ClearEmployeeSelectionCommand = ReactiveCommand.Create(
            ClearEmployeeSelection,
            this.WhenAnyValue(x => x.SelectedEmployeeCount).Select(count => count > 0));

        // Skip(1): WhenAnyValue starts with the current values, and there's nothing to save
        // before anything's been selected.
        this.WhenAnyValue(x => x.SelectedEmployee, x => x.SelectedDepartment)
            .Skip(1)
            .Subscribe(_ => _saveViewState());
    }

    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = new();

    /// <summary>The departments SearchText hasn't hidden, each showing its VisibleEmployees --
    /// what the Schedule tab's SfTreeView binds to (see EmployeeTreeSearchFilter.Apply).
    /// EmployeesPage deliberately binds to Departments instead, ignoring the filter.</summary>
    public ObservableCollection<DepartmentGroupViewModel> VisibleDepartments { get; } = new();

    public Employee? SelectedEmployee
    {
        get => _selectedEmployee;
        set => this.RaiseAndSetIfChanged(ref _selectedEmployee, value);
    }

    private Employee? _selectedEmployee;

    public Department? SelectedDepartment
    {
        get => _selectedDepartment;
        set => this.RaiseAndSetIfChanged(ref _selectedDepartment, value);
    }

    private Department? _selectedDepartment;

    /// <summary>Reloads the tree -- see LoadAsync. SchedulePage.OnNavigatedToAsync
    /// executes it on every visit.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> AddDepartmentCommand { get; }

    /// <summary>Enabled while a real department (not the "(Unassigned)" bucket) is selected.</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteDepartmentCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> AddEmployeeCommand { get; }

    /// <summary>Enabled while an employee is selected, as are Delete below.</summary>
    public ReactiveCommand<RxVoid, RxVoid> EditEmployeeCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DeleteEmployeeCommand { get; }

    /// <summary>Enabled only for a selected employee who isn't blacklisted yet --
    /// UnblacklistEmployeeCommand the other way round, so of the two buttons only the one
    /// that applies is enabled.</summary>
    public ReactiveCommand<RxVoid, RxVoid> BlacklistEmployeeCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UnblacklistEmployeeCommand { get; }

    /// <summary>Unchecks every employee. Enabled while at least one is checked.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearEmployeeSelectionCommand { get; }

    /// <summary>Free-text filter for the tree, comma-separated same as the Attendance
    /// tab's report-scope search box (see ReportScopeViewModel.SearchText) and its
    /// Punch Records search box -- each term is matched against Employee ID, first
    /// name, last name, or department name, and terms are OR'd together (see the
    /// shared EmployeeTreeSearchFilter). Narrows which nodes are visible
    /// (EmployeeNodeViewModel.IsVisible, DepartmentGroupViewModel.IsVisible) rather
    /// than which are checked, so typing here never changes what multi-select mode
    /// would actually assign a schedule to.
    ///
    /// Named plainly "SearchText" here, matching ReportScopeViewModel's own internal
    /// name; MainViewModel forwards it back out under the EmployeeTreeSearchText name
    /// SchedulePage.xaml binds to, the same way AttendanceViewModel.ReportScopeSearchText
    /// forwards ReportScope.SearchText.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ApplySearchFilter();
        }
    }

    private string _searchText = string.Empty;

    private void ApplySearchFilter() => EmployeeTreeSearchFilter.Apply(Departments, SearchText, VisibleDepartments);

    /// <summary>Set once LoadAsync has applied the saved employee/department selection
    /// for the first time this run -- guards against a later reload (e.g. after Import
    /// Employees/Import Schedule, both of which call LoadAsync again) silently
    /// overwriting whatever the person has selected *this session* with the saved value.
    /// Public with a private setter, same shape as ReportScopeViewModel.HasRestoredScope,
    /// so MainViewModel.SaveViewState can check Tree.HasRestoredSelection before writing,
    /// the same way AttendanceViewModel.SaveViewState checks ReportScope.HasRestoredScope.</summary>
    public bool HasRestoredSelection { get; private set; }

    /// <summary>Rebuilds the tree from the database. Public because
    /// ScheduleImportExportViewModel calls it after Import Schedule and Import Employees,
    /// and the commands below call it after their own writes.
    ///
    /// Routed through _busy.RunAsync -- this is the Schedule tab's own first-ever
    /// database access each run (SchedulePage.OnNavigatedToAsync executes LoadCommand
    /// before anything else on the page happens), and without the wrap it would be the
    /// one place in the app that touched the shared, app-lifetime-scoped
    /// ScheduleDbContext without going through this guard -- compare
    /// AttendanceViewModel.InitializeAsync, which wraps its own equivalent (the
    /// Attendance tab's tree load) for exactly the reason given there: "a keystroke ...
    /// during that window could fire a second, concurrent operation against the same
    /// DbContext." The same is true here for a person switching to Attendance, Payroll,
    /// or Employees in the second or so this method is still running.
    ///
    /// visibly: false, same reasoning InitializeAsync gives for its own wrap -- this is
    /// the page's own initial population, not something with a progress bar. A failure
    /// is shown and logged (ShowFailure) rather than swallowed.</summary>
    public async Task LoadAsync()
    {
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            Departments.Clear();

            var departments = await _repository.GetDepartmentsWithEmployeesAsync(cancellationToken);
            var unassigned = await _repository.GetUnassignedEmployeesAsync(cancellationToken);

            // Wraps each Employee in an EmployeeNodeViewModel (for the tree's per-employee
            // checkbox) and wires up notifications both ways: employee checkbox changes
            // update the department's tri-state checkbox and this class's own
            // SelectedEmployeeCount; the department checkbox pushes back down to its
            // employees.
            foreach (var group in EmployeeTreeBuilder.Build(departments, unassigned, OnEmployeeNodeSelectionChanged))
                Departments.Add(group);

            // Every node above starts unchecked, so anything checked before this reload is
            // gone -- make sure the count (and so ClearEmployeeSelectionCommand, and
            // ScheduleAssignmentViewModel's own multi-select commands, which follow it)
            // reflects that.
            RecountSelectedEmployees();

            if (!HasRestoredSelection)
            {
                HasRestoredSelection = true;
                RestoreSelectionFromViewState();
            }

            // Freshly-built nodes all default to IsVisible = true, so a reload (e.g. after
            // Import Employees/Import Schedule) under an already-typed search term needs
            // this to re-hide whatever still doesn't match, rather than briefly showing
            // everyone until the next keystroke.
            ApplySearchFilter();
        }, onError: ex => ShowFailure(ex, "Couldn't load employees"));
    }

    /// <summary>Re-applies whichever employee or department is currently in the shared,
    /// in-memory ViewStateStore (see its own doc comment -- session-only, so this is
    /// always empty on a fresh launch and the tree simply opens with nothing selected),
    /// once the tree has just been built for the first time this run. Silently does
    /// nothing if that employee/department no longer exists -- reopening where you left
    /// off is a convenience, not a guarantee.</summary>
    private void RestoreSelectionFromViewState()
    {
        var state = _viewStateStore.Schedule;

        if (state.SelectedEmployeeId is int employeeId)
        {
            var node = Departments.SelectMany(d => d.Employees)
                .FirstOrDefault(n => n.Employee.Id == employeeId);
            if (node is not null)
            {
                SelectedEmployee = node.Employee;
                SelectedDepartment = node.Employee.Department;
                return;
            }
        }

        if (state.SelectedDepartmentId is int departmentId)
        {
            var department = RealDepartments.FirstOrDefault(d => d.Id == departmentId);
            if (department is not null)
                SelectedDepartment = department;
        }
    }

    private void OnEmployeeNodeSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EmployeeNodeViewModel.IsSelected)) return;

        RecountSelectedEmployees();
    }

    /// <summary>Employees currently checked in the tree, across every department
    /// (including Unassigned) -- the target list for bulk schedule assignment (see
    /// GetCheckedEmployees below). A stored value, recounted whenever a checkbox or the
    /// whole tree changes, so ClearEmployeeSelectionCommand and
    /// ScheduleAssignmentViewModel can follow it through change notification.</summary>
    public int SelectedEmployeeCount
    {
        get => _selectedEmployeeCount;
        private set => this.RaiseAndSetIfChanged(ref _selectedEmployeeCount, value);
    }

    private int _selectedEmployeeCount;

    private void RecountSelectedEmployees() =>
        SelectedEmployeeCount = Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    /// <summary>Employees currently checked in the tree, across every department
    /// (including Unassigned) -- excludes any blacklisted employee even if their node
    /// is somehow still checked (their checkbox is hidden along with the rest of their
    /// row once blacklisted -- see EmployeeTreeSearchFilter -- but this guards the
    /// actual bulk-assignment target list directly rather than relying on the UI alone
    /// to keep one in sync with the other). ScheduleAssignmentViewModel's bulk
    /// assignments are the callers.</summary>
    public List<Employee> GetCheckedEmployees() =>
        Departments.SelectMany(d => d.Employees)
            .Where(n => n.IsSelected && !n.Employee.IsBlacklisted)
            .Select(n => n.Employee)
            .ToList();

    /// <summary>Real departments only -- for combo boxes where "Unassigned" isn't a valid choice.</summary>
    public IEnumerable<Department> RealDepartments =>
        Departments.Where(g => g.RealDepartment is not null).Select(g => g.RealDepartment!);

    private async Task AddDepartmentAsync()
    {
        var dialog = new InputDialog("New Department", "Department name:") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;

        await _repository.AddDepartmentAsync(dialog.Value.Trim());
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
    }

    private async Task DeleteDepartmentAsync()
    {
        if (SelectedDepartment is null) return;

        var confirmed = await ConfirmAsync(
            $"Delete department \"{SelectedDepartment.Name}\"?\n\nIts employees will become unassigned, not deleted -- " +
            "their schedules are kept.",
            "Confirm delete", isWarning: true);

        if (!confirmed) return;

        await _repository.DeleteDepartmentAsync(SelectedDepartment.Id);
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        SelectedDepartment = null;
        SelectedEmployee = null;
        await LoadAsync();
    }

    private async Task AddEmployeeAsync()
    {
        var dialog = new EmployeeDialog(
            RealDepartments, SelectedEmployee?.DepartmentId ?? SelectedDepartment?.Id, _payrollPolicy,
            _attendanceSettings.DefaultWorkTimeHours,
            takenEmployeeIds: GetTakenPins())
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _repository.AddEmployeeAsync(dialog.LastName, dialog.FirstName, dialog.DepartmentId, dialog.EmployeeId,
                dialog.QualifiesForOvertime, dialog.QualifiesForNightDiff, dialog.DailyRate, dialog.DefaultLeaveIsPaid,
                dialog.ApplyOvertimeRatePercentageByDefault,
                dialog.DefaultSss, dialog.DefaultPhilHealth, dialog.DefaultPagIbig,
                dialog.DefaultPremiumPay, dialog.DefaultAllowance, dialog.DefaultCashAdvance,
                dialog.EmployeeType, dialog.MonthlyRate, dialog.RestDayWorkPremiumPercentage,
                dialog.QualifiesForRestDayPay, dialog.QualifiesForPremiumPay,
                dialog.ClockInBufferBeforeHours, dialog.ClockInBufferAfterHours,
                dialog.ClockOutBufferBeforeHours, dialog.ClockOutBufferAfterHours,
                dialog.HolidayPremiumPercentage,
                dialog.DefaultWorkTimeHours, dialog.ExemptFromUndertimeDeduction);
        }
        catch (DuplicateEmployeeIdException ex)
        {
            // The dialog already checks this against what was loaded when it opened --
            // this only fires if someone else added that same ID in the meantime.
            StatusBar.ShowCaution(ex.Message, "Duplicate Employee ID");
            return;
        }

        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
    }

    private async Task EditEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var dialog = new EmployeeDialog(
            RealDepartments, SelectedEmployee.DepartmentId, _payrollPolicy,
            _attendanceSettings.DefaultWorkTimeHours, SelectedEmployee,
            takenEmployeeIds: GetTakenPins(excludingEmployeeId: SelectedEmployee.Id))
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _repository.UpdateEmployeeAsync(
                SelectedEmployee.Id, dialog.LastName, dialog.FirstName, dialog.DepartmentId, dialog.EmployeeId,
                dialog.QualifiesForOvertime, dialog.QualifiesForNightDiff, dialog.DailyRate, dialog.DefaultLeaveIsPaid,
                dialog.ApplyOvertimeRatePercentageByDefault,
                dialog.DefaultSss, dialog.DefaultPhilHealth, dialog.DefaultPagIbig,
                dialog.DefaultPremiumPay, dialog.DefaultAllowance, dialog.DefaultCashAdvance,
                dialog.EmployeeType, dialog.MonthlyRate, dialog.RestDayWorkPremiumPercentage,
                dialog.QualifiesForRestDayPay, dialog.QualifiesForPremiumPay,
                dialog.ClockInBufferBeforeHours, dialog.ClockInBufferAfterHours,
                dialog.ClockOutBufferBeforeHours, dialog.ClockOutBufferAfterHours,
                dialog.HolidayPremiumPercentage,
                dialog.DefaultWorkTimeHours, dialog.ExemptFromUndertimeDeduction);
        }
        catch (DuplicateEmployeeIdException ex)
        {
            StatusBar.ShowCaution(ex.Message, "Duplicate Employee ID");
            return;
        }

        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
    }

    /// <summary>Employee IDs (Pin) currently in use, optionally excluding one employee
    /// (their own current ID shouldn't count as "taken" while editing them).</summary>
    private HashSet<int> GetTakenPins(int? excludingEmployeeId = null) =>
        Departments.SelectMany(d => d.Employees)
            .Select(n => n.Employee)
            .Where(emp => emp.Id != excludingEmployeeId)
            .Select(emp => emp.Pin)
            .ToHashSet();

    private async Task DeleteEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var confirmed = await ConfirmAsync(
            $"Delete employee \"{SelectedEmployee.DisplayName}\" and all of their schedule entries? This cannot be undone.",
            "Confirm delete", isWarning: true);

        if (!confirmed) return;

        var employeeId = SelectedEmployee.Id;

        // Captured before SelectedEmployee is nulled out below -- the per-employee schedule bump
        // needs the Pin, and by the time the delete has finished there's nothing left to read it
        // from. Pin rather than Id: _lastScheduleChangeVersionByPin is keyed by Pin like
        // everything else on the payroll side (see BumpScheduleForEmployees' own doc comment).
        var employeePin = SelectedEmployee.Pin;

        SelectedEmployee = null;
        await _repository.DeleteEmployeeAsync(employeeId);

        // Per-employee, not a plain BumpSchedule(): deleting an employee cascades their
        // ScheduleEntries away, which moves that employee's payroll numbers to zero -- and a
        // bump with no per-pin entry is invisible to AnyScheduleChangeSince, so an
        // already-loaded payroll group containing this employee would have gone on showing their
        // pre-delete figures until something else forced a recompute. See
        // BumpScheduleForEmployees' own doc comment for why the wiped date range doesn't need
        // reporting alongside the Pin.
        _dataVersion.BumpScheduleForEmployees([employeePin]);
        _dataVersion.BumpRoster(); // and removes them from the roster itself -- see this field's own doc comment
        await LoadAsync();
    }

    /// <summary>Hides the employee from the Schedule/Attendance trees and active-only
    /// pickers (report scope, payroll rosters/wizard) going forward, without touching
    /// any of their existing schedule/attendance/payroll history -- see
    /// Employee.IsBlacklisted's own doc comment. Reversible via UnblacklistEmployeeAsync.</summary>
    private async Task BlacklistEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var confirmed = await ConfirmAsync(
            $"Blacklist employee \"{SelectedEmployee.DisplayName}\"? " +
            "They'll be hidden from the Schedule and Attendance trees and left out of new payroll rosters, " +
            "but their existing records are kept, and they'll still appear here so they can be unblacklisted later.",
            "Confirm blacklist", isWarning: true);

        if (!confirmed) return;

        var employeeId = SelectedEmployee.Id;
        await _repository.SetEmployeeBlacklistAsync(employeeId, true);
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
        ReselectEmployee(employeeId);
    }

    private async Task UnblacklistEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var employeeId = SelectedEmployee.Id;
        await _repository.SetEmployeeBlacklistAsync(employeeId, false);
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
        ReselectEmployee(employeeId);
    }

    /// <summary>Re-points SelectedEmployee at the freshly-loaded node for this Id after
    /// a LoadAsync rebuild. Needed specifically after Blacklist/Unblacklist (unlike
    /// Edit/Delete, which either clear SelectedEmployee first or don't depend on
    /// CanExecute reflecting a just-changed property): LoadAsync only restores
    /// selection from ViewState on the very first load, so without this, SelectedEmployee
    /// would keep pointing at the pre-toggle Employee instance, and Blacklist/Unblacklist's
    /// CanExecute would keep following its old IsBlacklisted value until something else
    /// re-selected them.</summary>
    private void ReselectEmployee(int employeeId)
    {
        var node = Departments.SelectMany(d => d.Employees).FirstOrDefault(n => n.Employee.Id == employeeId);
        if (node is not null) SelectedEmployee = node.Employee;
    }

    /// <summary>Unchecks every employee -- ClearEmployeeSelectionCommand, and MainViewModel
    /// directly whenever multi-select mode ends.</summary>
    public void ClearEmployeeSelection()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = false;
    }
}
