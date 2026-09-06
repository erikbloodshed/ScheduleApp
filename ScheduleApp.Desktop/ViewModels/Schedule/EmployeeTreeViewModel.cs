using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

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
/// SelectedEmployee/SelectedDepartment (TreeView.SelectedItem, not the per-row
/// checkboxes) drive the single-employee calendar view on the rest of the
/// Schedule tab, and are also what EmployeesPage's editor panel and
/// PayrollViewModel's own reactivity (it filters PropertyChanged on
/// nameof(SelectedEmployee)) key off of -- see this class's own doc comments below
/// on OnSelectedEmployeeChanged/OnSelectedDepartmentChanged for what stays here
/// versus what phase 3/4's ScheduleCalendarViewModel/ScheduleAssignmentViewModel
/// will each subscribe to for themselves once they exist, rather than this class
/// reaching out to them.
///
/// Extracted per the split plan's phase 2 ("pure extraction, no other class
/// depends on it yet") -- nothing outside this file references
/// EmployeeTreeViewModel yet, and MainViewModel itself is untouched: it still owns
/// its own copy of every member here. Phase 6 rebuilds MainViewModel as a facade
/// that constructs this class and forwards its members flatly, the same way
/// AttendanceViewModel forwards ReportScopeViewModel's. LoadAsync below went from
/// private to public once phase 5 actually landed and needed to call it from
/// ScheduleImportExportViewModel -- see that method's own doc comment for the gap
/// this closes.
/// </summary>
public partial class EmployeeTreeViewModel : ObservableObject
{
    private readonly IScheduleRepository _repository;
    private readonly IStatusBarService _statusBarService;
    private readonly ViewStateStore _viewStateStore;

    /// <summary>Bumped after DeleteEmployeeAsync, which deletes that employee's
    /// schedule entries too -- the Attendance Summary tab's own auto-reload-on-
    /// tab-select (see ReportViewModel.ShouldAutoReload) needs to see that,
    /// exactly as it already does for every other schedule-mutating call MainViewModel
    /// makes (see AttendanceDataVersion's own doc comment).
    ///
    /// NOT listed in the split plan's component-list table for this class -- the table
    /// only names IScheduleRepository, IStatusBarService, ViewStateStore, and a
    /// SaveViewState delegate. That's a gap in the table, not a deliberate omission:
    /// DeleteEmployeeAsync is squarely a tree-owned command (component list puts
    /// "Add/Edit/Delete/Blacklist/Unblacklist Employee" on this class), and it's always
    /// called _dataVersion.BumpSchedule() -- see MainViewModel.DeleteEmployeeAsync,
    /// still unmodified at the time of this extraction, and AttendanceDataVersion's own
    /// doc comment, which already documents this exact call site by name. Dropping the
    /// bump here would silently break that auto-reload the moment this class actually
    /// starts being used, so this constructor takes the same shared instance MainViewModel
    /// already receives (see App.xaml.cs's existing `services.AddScoped&lt;AttendanceDataVersion&gt;()`
    /// registration -- already shared app-wide, so this needs no new registration of its
    /// own) rather than following the table literally. Flagging this here the way phase
    /// 1's own doc comment flagged its uncompiled status, for whoever wires this into
    /// MainViewModel's constructor in phase 6 to notice if the table is trusted instead
    /// of this file.
    ///
    /// Also the instance every command below that adds, edits, removes, or (un)blacklists
    /// an employee, or adds/deletes a department, bumps via _dataVersion.BumpRoster() --
    /// added once ActiveRosterProvider existed to cache ReportScopeViewModel/
    /// PayslipScopeViewModel/PayrollWizardViewModel/PayrollRunViewModel/
    /// PayrollGroupViewModel's own Active-only roster reads (see that class's own doc
    /// comment). This class is the only place any of those seven mutations happen, so it's
    /// the only place RosterVersion needs bumping from -- see
    /// ScheduleImportExportViewModel.ImportEmployeesAsync for the one roster-mutating write
    /// that happens outside this class entirely.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>MainViewModel's own SaveViewState, passed in as a delegate rather than
    /// this class reaching back out to its facade -- same shape as
    /// ManualEntriesViewModel/PunchRecordsViewModel's own _saveViewState (see either's
    /// constructor). Called after SelectedEmployee/SelectedDepartment change so a
    /// restart reopens on the same selection; NOT called from LoadAsync itself, since
    /// that already runs during the facade's own constructor-time DisplayedMonth
    /// assignment on the very first load, before HasRestoredSelection has had a chance
    /// to flip true (see that property's own doc comment, and MainViewModel.SaveViewState's,
    /// for the guard this mirrors).</summary>
    private readonly Action _saveViewState;

    /// <summary>The *same* instance MainViewModel receives (see App.xaml.cs's registration
    /// and ScheduleCalendarViewModel._busy's own doc comment) -- shared across Schedule/
    /// Payroll/Attendance since they all read/write the one shared, app-lifetime-scoped
    /// ScheduleDbContext. LoadAsync below is the one DB-touching call in this class, and
    /// (until this field existed) the only tree/data load anywhere in the app that didn't
    /// route through this guard -- see LoadAsync's own doc comment for the concurrent-
    /// DbContext crash that gap could produce against the Attendance/Payroll tabs during
    /// the first second or two after sign-in, while this method is still running.</summary>
    private readonly AttendanceBusyState _busy;

    public EmployeeTreeViewModel(
        IScheduleRepository repository,
        IStatusBarService statusBarService,
        ViewStateStore viewStateStore,
        AttendanceDataVersion dataVersion,
        Action saveViewState,
        AttendanceBusyState busy)
    {
        _repository = repository;
        _statusBarService = statusBarService;
        _viewStateStore = viewStateStore;
        _dataVersion = dataVersion;
        _saveViewState = saveViewState;
        _busy = busy;
    }

    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = new();

    [ObservableProperty]
    private Employee? selectedEmployee;

    [ObservableProperty]
    private Department? selectedDepartment;

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
    /// name, rather than the "EmployeeTreeSearchText" name MainViewModel.xaml.cs's
    /// external contract currently exposes -- there's no second search box on this
    /// class the way AttendanceViewModel's ReportScopeSearchText has to avoid
    /// colliding with PunchRecords.LogViewSearchText, so nothing here forces the
    /// longer name. Phase 6's facade forwards this back out under the
    /// EmployeeTreeSearchText name SchedulePage.xaml already binds to, the same way
    /// AttendanceViewModel.ReportScopeSearchText forwards ReportScope.SearchText.</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    private void ApplySearchFilter() => EmployeeTreeSearchFilter.Apply(Departments, SearchText);

    /// <summary>Set once LoadAsync has applied the saved employee/department selection
    /// for the first time this run -- guards against a later reload (e.g. after Import
    /// Employees/Import Schedule, both of which call LoadAsync again) silently
    /// overwriting whatever the person has selected *this session* with last launch's
    /// saved value. Public with a private setter, same shape as
    /// ReportScopeViewModel.HasRestoredScope -- the direct precedent the split plan's
    /// own "Two more precedents" section calls out -- so phase 6's facade SaveViewState
    /// can check Tree.HasRestoredSelection before writing, the same way
    /// AttendanceViewModel.SaveViewState checks ReportScope.HasRestoredScope.</summary>
    public bool HasRestoredSelection { get; private set; }

    /// <summary>Public -- gap found while landing phase 5: the split plan's component-list
    /// table says ScheduleImportExportViewModel "calls Tree.LoadAsync() afterward" for both
    /// Import Schedule and Import Employees, the same full-tree reload
    /// MainViewModel.ImportScheduleAsync/ImportEmployeesAsync already call today, but this
    /// method was left `private` during phase 2's own extraction (nothing outside this file
    /// called it yet at the time). Same "phase N's own table already needs it, so make it
    /// accessible now" reasoning this class's own GetCheckedEmployees doc comment already
    /// used for the same situation in phase 2, and phase 4 later used again for
    /// ScheduleCalendarViewModel.RefreshCalendarAttendanceStatusesAsync -- flagging the fix
    /// here for whoever next reads this table against this file. The generated LoadCommand
    /// (RelayCommand strips the "Async" suffix, same as every other [RelayCommand] Task
    /// method in this codebase -- SchedulePage.xaml.cs already calls the old MainViewModel's
    /// own equivalent as `_viewModel.LoadCommand`, not `LoadAsyncCommand`, confirming the
    /// name; corrected here in phase 6 after this comment's own first draft got it wrong) is
    /// unaffected by this method's own accessibility either way -- [RelayCommand] generates
    /// that wrapper regardless of whether the method underneath is private or public.
    ///
    /// Routed through _busy.RunAsync as of the fix noted on the _busy field itself --
    /// this is the Schedule tab's own first-ever database access each run
    /// (SchedulePage.OnNavigatedToAsync awaits LoadCommand before anything else on the
    /// page happens), and until this wrap existed it was the one place in the app that
    /// touched the shared, app-lifetime-scoped ScheduleDbContext without going through
    /// this guard at all -- compare AttendanceViewModel.InitializeAsync, which wraps its
    /// own equivalent (the Attendance tab's tree load) in _busy.IsRunning for exactly the
    /// reason given there: "a keystroke ... during that window could fire a second,
    /// concurrent operation against the same DbContext." The same was true here for a
    /// person switching to Attendance, Payroll, or Employees in the second or so this
    /// method is still running -- AttendancePage/PayrollPage's own navigation isn't
    /// gated on Schedule's load finishing first, so nothing previously stopped that
    /// second tab's own DB access from starting while this one was still using the
    /// connection, which is exactly what EF Core's "a second operation was started on
    /// this context" exception is -- and with no global unhandled-exception handler
    /// anywhere in this app, that exception took the whole process down rather than
    /// failing gracefully.
    ///
    /// visibly: false, same reasoning InitializeAsync gives for its own wrap -- this is
    /// the page's own initial population, not something with a progress bar today, so
    /// this shouldn't start showing one now.</summary>
    [RelayCommand]
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
            // SelectedEmployeeCount/ClearEmployeeSelectionCommand; the department checkbox
            // pushes back down to its employees.
            foreach (var group in EmployeeTreeBuilder.Build(departments, unassigned, OnEmployeeNodeSelectionChanged))
                Departments.Add(group);

            // Every node above starts unchecked, so anything checked before this reload is
            // gone -- make sure this class's own count display and button reflect that.
            // Deliberately does NOT also notify SetScheduleForSelectionCommand/
            // SetLeaveForSelectionCommand the way MainViewModel's own LoadAsync still does
            // today -- those are ScheduleAssignmentViewModel's commands (phase 4), not this
            // class's, and this class can't reach forward to a sibling that doesn't exist
            // yet. Once phase 4 lands, ScheduleAssignmentViewModel is expected to subscribe
            // to this class's own PropertyChanged(nameof(SelectedEmployeeCount)) itself (the
            // same "child subscribes to the sibling it depends on" layering
            // ScheduleCalendarViewModel's own future subscription to SelectedEmployee will
            // follow) to keep its own commands in sync -- see OnEmployeeNodeSelectionChanged's
            // doc comment below for the same note at its other call site.
            OnPropertyChanged(nameof(SelectedEmployeeCount));
            ClearEmployeeSelectionCommand.NotifyCanExecuteChanged();

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
        });
    }

    /// <summary>Re-applies whichever employee or department is currently in the shared,
    /// in-memory ViewStateStore (see its own doc comment -- session-only now, so this is
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

        // See LoadAsync's own doc comment just above -- SetScheduleForSelectionCommand/
        // SetLeaveForSelectionCommand belong to ScheduleAssignmentViewModel (phase 4),
        // which is expected to subscribe to this class's own
        // PropertyChanged(nameof(SelectedEmployeeCount)) itself rather than this class
        // notifying a sibling it has no reference to.
        OnPropertyChanged(nameof(SelectedEmployeeCount));
        ClearEmployeeSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Employees currently checked in the tree, across every department
    /// (including Unassigned) -- the target list for bulk schedule assignment (see
    /// GetCheckedEmployees below).</summary>
    public int SelectedEmployeeCount => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    /// <summary>Employees currently checked in the tree, across every department
    /// (including Unassigned) -- excludes any blacklisted employee even if their node
    /// is somehow still checked (their checkbox is hidden along with the rest of their
    /// row once blacklisted -- see EmployeeTreeSearchFilter -- but this guards the
    /// actual bulk-assignment target list directly rather than relying on the UI alone
    /// to keep one in sync with the other). Public (unlike MainViewModel's own private
    /// copy) -- ScheduleAssignmentViewModel (phase 4) is the caller once it exists (see
    /// AssignScheduleToCheckedEmployeesAsync/SetLeaveForCheckedEmployeesAsync in
    /// MainViewModel today).</summary>
    public List<Employee> GetCheckedEmployees() =>
        Departments.SelectMany(d => d.Employees)
            .Where(n => n.IsSelected && !n.Employee.IsBlacklisted)
            .Select(n => n.Employee)
            .ToList();

    /// <summary>Real departments only -- for combo boxes where "Unassigned" isn't a valid choice.</summary>
    public IEnumerable<Department> RealDepartments =>
        Departments.Where(g => g.RealDepartment is not null).Select(g => g.RealDepartment!);

    /// <summary>Notifies only this class's own tree-owned commands (Delete/Edit/
    /// Blacklist/Unblacklist Employee) and saves view state. Deliberately does NOT also
    /// notify CalendarHeaderText or call RequestScheduleRefresh() the way
    /// MainViewModel's own OnSelectedEmployeeChanged still does today -- both belong to
    /// ScheduleCalendarViewModel (phase 3), which takes this class as a constructor
    /// dependency specifically so it can subscribe to this property's own
    /// PropertyChanged itself, the same "child reacts to the sibling it depends on"
    /// layering OnEmployeeNodeSelectionChanged's own doc comment above describes for
    /// ScheduleAssignmentViewModel and SelectedEmployeeCount.</summary>
    partial void OnSelectedEmployeeChanged(Employee? value)
    {
        DeleteEmployeeCommand.NotifyCanExecuteChanged();
        EditEmployeeCommand.NotifyCanExecuteChanged();
        BlacklistEmployeeCommand.NotifyCanExecuteChanged();
        UnblacklistEmployeeCommand.NotifyCanExecuteChanged();
        _saveViewState();
    }

    partial void OnSelectedDepartmentChanged(Department? value)
    {
        DeleteDepartmentCommand.NotifyCanExecuteChanged();
        _saveViewState();
    }

    [RelayCommand]
    private async Task AddDepartmentAsync()
    {
        var dialog = new InputDialog("New Department", "Department name:") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;

        await _repository.AddDepartmentAsync(dialog.Value.Trim());
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
    }

    private bool CanDeleteDepartment() => SelectedDepartment is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteDepartment))]
    private async Task DeleteDepartmentAsync()
    {
        if (SelectedDepartment is null) return;

        var confirm = MessageBox.Show(
            $"Delete department \"{SelectedDepartment.Name}\"?\n\nIts employees will become unassigned, not deleted -- " +
            "their schedules are kept.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        await _repository.DeleteDepartmentAsync(SelectedDepartment.Id);
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        SelectedDepartment = null;
        SelectedEmployee = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddEmployeeAsync()
    {
        var dialog = new EmployeeDialog(
            RealDepartments, SelectedEmployee?.DepartmentId ?? SelectedDepartment?.Id,
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
                dialog.ClockOutBufferBeforeHours, dialog.ClockOutBufferAfterHours);
        }
        catch (DuplicateEmployeeIdException ex)
        {
            // The dialog already checks this against what was loaded when it opened --
            // this only fires if someone else added that same ID in the meantime.
            _statusBarService.ShowCaution(ex.Message, "Duplicate Employee ID");
            return;
        }

        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
    }

    private bool CanEditEmployee() => SelectedEmployee is not null;

    [RelayCommand(CanExecute = nameof(CanEditEmployee))]
    private async Task EditEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var dialog = new EmployeeDialog(
            RealDepartments, SelectedEmployee.DepartmentId, SelectedEmployee,
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
                dialog.ClockOutBufferBeforeHours, dialog.ClockOutBufferAfterHours);
        }
        catch (DuplicateEmployeeIdException ex)
        {
            _statusBarService.ShowCaution(ex.Message, "Duplicate Employee ID");
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

    private bool CanDeleteEmployee() => SelectedEmployee is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteEmployee))]
    private async Task DeleteEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var confirm = MessageBox.Show(
            $"Delete employee \"{SelectedEmployee.DisplayName}\" and all of their schedule entries? This cannot be undone.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var employeeId = SelectedEmployee.Id;
        SelectedEmployee = null;
        await _repository.DeleteEmployeeAsync(employeeId);
        _dataVersion.BumpSchedule(); // deletes that employee's schedule entries too -- see this field's own doc comment
        _dataVersion.BumpRoster(); // and removes them from the roster itself -- see this field's own doc comment
        await LoadAsync();
    }

    private bool CanBlacklistEmployee() => SelectedEmployee is { IsBlacklisted: false };

    /// <summary>Hides the employee from the Schedule/Attendance trees and active-only
    /// pickers (report scope, payroll rosters/wizard) going forward, without touching
    /// any of their existing schedule/attendance/payroll history -- see
    /// Employee.IsBlacklisted's own doc comment. Reversible via UnblacklistEmployeeAsync.</summary>
    [RelayCommand(CanExecute = nameof(CanBlacklistEmployee))]
    private async Task BlacklistEmployeeAsync()
    {
        if (SelectedEmployee is null) return;

        var confirm = MessageBox.Show(
            $"Blacklist employee \"{SelectedEmployee.DisplayName}\"? " +
            "They'll be hidden from the Schedule and Attendance trees and left out of new payroll rosters, " +
            "but their existing records are kept, and they'll still appear here so they can be unblacklisted later.",
            "Confirm blacklist", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var employeeId = SelectedEmployee.Id;
        await _repository.SetEmployeeBlacklistAsync(employeeId, true);
        _dataVersion.BumpRoster(); // see this class's own _dataVersion doc comment
        await LoadAsync();
        ReselectEmployee(employeeId);
    }

    private bool CanUnblacklistEmployee() => SelectedEmployee is { IsBlacklisted: true };

    [RelayCommand(CanExecute = nameof(CanUnblacklistEmployee))]
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
    /// would keep pointing at the pre-toggle Employee instance and
    /// CanBlacklistEmployee/CanUnblacklistEmployee would keep evaluating the old
    /// IsBlacklisted value until something else re-selected them.</summary>
    private void ReselectEmployee(int employeeId)
    {
        var node = Departments.SelectMany(d => d.Employees).FirstOrDefault(n => n.Employee.Id == employeeId);
        if (node is not null) SelectedEmployee = node.Employee;
    }

    private bool CanClearEmployeeSelection() => SelectedEmployeeCount > 0;

    [RelayCommand(CanExecute = nameof(CanClearEmployeeSelection))]
    private void ClearEmployeeSelection()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = false;
    }
}
