using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Schedule;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs the Schedule tab, and (via the same Scoped instance -- see App.xaml.cs's
/// registration comment) is also handed straight to EmployeesPage and PayrollPage as their
/// own DataContext, plus held directly by PayrollViewModel, which filters its own
/// PropertyChanged subscription on nameof(SelectedEmployee) -- see MainViewModel-Split-
/// Plan.md's "Background" section for the fuller "every property and command here is an
/// external contract" story. Composes four single-purpose child ViewModels (see
/// ViewModels/Schedule/) rather than implementing the tab's four concerns itself:
///
///  - Tree       -- EmployeeTreeViewModel: the Department/Employee checkbox tree, plus
///    everything that adds, edits, removes, or navigates what's in it.
///  - Calendar   -- ScheduleCalendarViewModel: month navigation, the 42-cell day grid, day
///    selection, and the background attendance-completion markers decorating each cell.
///  - Assignment -- ScheduleAssignmentViewModel: Set/Clear Schedule and Set Leave (both the
///    single-employee flow and the multi-select bulk flow), plus the calendar's right-click
///    "Add Manual Entry…".
///  - ImportExport -- ScheduleImportExportViewModel: Export/Import Schedule and Import
///    Employees.
///
/// Same precedent AttendanceViewModel already establishes for its own seven children: forward
/// every bindable member so no XAML (or EmployeesPage.xaml.cs/PayrollPage.xaml.cs/
/// MonthCalendarControl.xaml.cs's code-behind, or PayrollViewModel's own reactivity) needs to
/// change, relay each child's PropertyChanged under the same name so two-way bindings and
/// external subscribers keep working, and reserve the facade itself for what's genuinely
/// cross-cutting rather than any one child's own concern.
///
/// This class's own job is the three things no single child owns:
///
///  - View-state persistence (SaveViewState) -- an employee/department selection and a
///    displayed month need to be captured as one consistent snapshot, which only something
///    above Tree and Calendar can do. Passed into both of them as a delegate, same shape as
///    AttendanceViewModel hands SaveViewState to Report/PunchRecords/ManualEntriesTab.
///  - CalendarHeaderText -- reads both Tree.SelectedEmployee and MultiSelectModeState.
///    IsMultiSelectMode, so it's cross-cutting facade behavior, not something either sibling
///    could compute on its own (see ScheduleCalendarViewModel's own doc comment, which
///    deliberately left this one on the facade rather than reproducing it).
///  - The MultiSelectModeState cascade -- IsMultiSelectMode/ToggleMultiSelectMode/
///    MultiSelectButtonText, plus the dedicated MultiSelectModeState.PropertyChanged
///    subscription below that clears Tree's checked employees and notifies
///    Assignment's three affected commands whenever the flag flips, from *either* direction
///    (an explicit ToggleMultiSelectMode click, or Assignment's own bulk-write clearing it
///    back to false after a successful assign). See MultiSelectModeState's own doc comment
///    for why this cascade couldn't live on either sibling that reads/writes the flag.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ViewStateStore _viewStateStore;

    /// <summary>Constructed here, not injected -- see that class's own doc comment for why
    /// (not shared with anything outside the Schedule page, unlike AttendanceBusyState/
    /// AttendanceDataVersion). Handed to Calendar and Assignment below, the two siblings that
    /// actually read/write it; Tree and ImportExport have no reason to see it.</summary>
    private readonly MultiSelectModeState _multiSelectMode;

    public EmployeeTreeViewModel Tree { get; }
    public ScheduleCalendarViewModel Calendar { get; }
    public ScheduleAssignmentViewModel Assignment { get; }
    public ScheduleImportExportViewModel ImportExport { get; }

    public MainViewModel(
        IScheduleRepository repository, IHolidayRepository holidayRepository, IStatusBarService statusBarService,
        ViewStateStore viewStateStore,
        AttendanceSettings attendanceSettings, IAttendanceRunner attendanceRunner, AttendanceBusyState busy,
        AttendanceDataVersion dataVersion, PayrollSettings payrollSettings, ManualEntryEditorViewModel manualEntryEditor,
        ActiveRosterProvider activeRosterProvider, IDayPunchPairingEditorLauncher pairingLauncher)
    {
        _viewStateStore = viewStateStore;
        _multiSelectMode = new MultiSelectModeState();

        // Build order matches the split plan's own phased list: Tree first (nothing else
        // depends on), then Calendar (needs Tree), then Assignment (needs Tree and Calendar),
        // then ImportExport (needs Tree and Calendar too, built last as the smallest/lowest-
        // risk piece). SaveViewState is passed to Tree/Calendar as a delegate -- see this
        // class's own doc comment above -- rather than either reaching back out to this
        // facade by reference.
        Tree = new EmployeeTreeViewModel(repository, statusBarService, viewStateStore, dataVersion, SaveViewState, busy,
            payrollSettings.Policy, attendanceSettings);

        Calendar = new ScheduleCalendarViewModel(
            repository, holidayRepository, statusBarService, attendanceSettings, attendanceRunner, busy, viewStateStore,
            SaveViewState, Tree, _multiSelectMode, dataVersion);

        Assignment = new ScheduleAssignmentViewModel(
            repository, holidayRepository, statusBarService, attendanceSettings, payrollSettings, busy, dataVersion,
            manualEntryEditor, Tree, Calendar, _multiSelectMode, pairingLauncher);

        // activeRosterProvider is only for ImportExport's own PayslipScopeDialog (Export
        // Schedule…) -- see ActiveRosterProvider's own doc comment and
        // ScheduleImportExportViewModel's own _activeRosterProvider field doc comment. Tree/
        // Calendar/Assignment have no PayslipScopeDialog-style tree of their own to feed.
        ImportExport = new ScheduleImportExportViewModel(
            repository, statusBarService, dataVersion, Tree, Calendar, activeRosterProvider);

        // Relays each child's own PropertyChanged onto this class under the same property
        // name, so a two-way XAML binding on (say) MainViewModel.SelectedEmployee -- which
        // reads/writes through the forwarding property below -- still refreshes when
        // Tree.SelectedEmployee changes for a reason other than that same binding setting it
        // (e.g. PayrollViewModel's own SelectBatchEmployee/RemoveEmployee paths assigning
        // Tree.SelectedEmployee indirectly through this same forwarding property). No name
        // collides across more than one child (each forwarded property belongs to exactly one
        // of them; see the region below), so a blanket relay is safe -- same reasoning
        // AttendanceViewModel's own constructor already documents for its seven children.
        Tree.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Calendar.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Assignment.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        ImportExport.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        // CalendarHeaderText is built out of Tree.SelectedEmployee *and*
        // MultiSelectModeState.IsMultiSelectMode (see that property below), so the blanket
        // Tree relay above -- which already forwards "SelectedEmployee" itself under its own
        // name for two-way bindings/PayrollViewModel's subscription -- doesn't also refresh
        // this computed one. This is the other half of the gap ScheduleCalendarViewModel's own
        // constructor doc comment flagged as staying open until this phase (the first half,
        // RequestScheduleRefresh(), is that class's own Tree.PropertyChanged subscription).
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EmployeeTreeViewModel.SelectedEmployee))
                OnPropertyChanged(nameof(CalendarHeaderText));
        };

        // The MultiSelectModeState cascade -- see this class's own doc comment above and
        // MultiSelectModeState's own ("every reaction to a flip of this flag ... is
        // cross-cutting facade behavior that stays on MainViewModel's own PropertyChanged
        // relay ... not something this class does itself"). Fires whichever direction the
        // flag flips from: an explicit ToggleMultiSelectMode click below, or Assignment's own
        // AssignScheduleToCheckedEmployeesAsync/SetLeaveForCheckedEmployeesAsync writing it
        // back to false after a successful bulk operation -- that write was "currently inert
        // beyond flipping the raw flag" per ScheduleAssignmentViewModel's own doc comment
        // until this subscription existed. Mirrors the shape of the _busy.PropertyChanged
        // block the old, unsplit MainViewModel used to wire up directly in its own
        // constructor.
        _multiSelectMode.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MultiSelectModeState.IsMultiSelectMode)) return;

            OnPropertyChanged(nameof(IsMultiSelectMode));
            OnPropertyChanged(nameof(MultiSelectButtonText));
            OnPropertyChanged(nameof(CalendarHeaderText));
            Assignment.SetScheduleForSelectionCommand.NotifyCanExecuteChanged();
            Assignment.SetLeaveForSelectionCommand.NotifyCanExecuteChanged();
            Assignment.ClearScheduleForSelectionCommand.NotifyCanExecuteChanged();
            Calendar.RecalculateScheduleCommand.NotifyCanExecuteChanged();

            // Leaving the mode (whether by cancelling or after a successful bulk assign)
            // clears whatever was checked -- checkboxes are about to disappear, so a leftover
            // checked state would just be invisible state. Execute(null) rather than a direct
            // method call: ClearEmployeeSelection itself is private on EmployeeTreeViewModel
            // (this facade only ever reaches it through the generated command, same as every
            // other member in the "Forwarded members" region below), and -- same as the old,
            // unsplit OnIsMultiSelectModeChanged's own direct call -- this bypasses
            // ClearEmployeeSelectionCommand's own CanExecute gate entirely rather than
            // respecting it, since there's always something to clear (or nothing, harmlessly)
            // whenever this fires.
            if (!_multiSelectMode.IsMultiSelectMode)
                Tree.ClearEmployeeSelectionCommand.Execute(null);
        };
    }

    // ---- Forwarded members ----
    //
    // Every property/command SchedulePage.xaml/EmployeesPage.xaml/PayrollPage.xaml(.cs)/
    // MonthCalendarControl.xaml.cs binds to or calls, forwarded from whichever child
    // ViewModel actually owns it. Nothing here has its own logic -- it's here so none of
    // that XAML or code-behind needed to change as part of the split.

    public ObservableCollection<DepartmentGroupViewModel> Departments => Tree.Departments;
    public ObservableCollection<ScheduleEntry> ScheduleEntries => Calendar.ScheduleEntries;
    public ObservableCollection<CalendarDayViewModel> CalendarDays => Calendar.CalendarDays;

    public Employee? SelectedEmployee
    {
        get => Tree.SelectedEmployee;
        set => Tree.SelectedEmployee = value;
    }

    public Department? SelectedDepartment
    {
        get => Tree.SelectedDepartment;
        set => Tree.SelectedDepartment = value;
    }

    public DateTime DisplayedMonth
    {
        get => Calendar.DisplayedMonth;
        set => Calendar.DisplayedMonth = value;
    }

    /// <summary>One-line forwarding property over the shared MultiSelectModeState instance
    /// (see this class's own doc comment above) -- SchedulePage.xaml's bindings (the two
    /// checkbox-visibility ones and MultiSelectButtonText's own dependency on this) don't
    /// change. Reading/writing through here rather than exposing MultiSelectModeState itself
    /// keeps the external contract exactly what it was before the split: a plain bool
    /// property on MainViewModel.</summary>
    public bool IsMultiSelectMode
    {
        get => _multiSelectMode.IsMultiSelectMode;
        set => _multiSelectMode.IsMultiSelectMode = value;
    }

    public string MultiSelectButtonText => IsMultiSelectMode ? "Cancel Multi-Select" : "Assign Schedule to Multiple Employees";

    /// <summary>Forwards Tree.SearchText under the external name SchedulePage.xaml already
    /// binds to -- same "child keeps the plain internal name, facade forwards under the
    /// longer external one" shape AttendanceViewModel.ReportScopeSearchText already
    /// establishes for ReportScope.SearchText (see that property's own doc comment). Nothing
    /// other than the bound TextBox itself (UpdateSourceTrigger=PropertyChanged) ever sets
    /// Tree.SearchText, so -- same as ReportScopeSearchText -- the blanket Tree relay above
    /// forwarding it under its own "SearchText" name rather than this one is harmless: there's
    /// no other writer for an external refresh to ever need to catch up with.</summary>
    public string EmployeeTreeSearchText
    {
        get => Tree.SearchText;
        set => Tree.SearchText = value;
    }

    public string SetScheduleButtonText => Calendar.SetScheduleButtonText;

    /// <summary>What the calendar's header shows -- the selected employee's name normally,
    /// but a mode-appropriate message in multi-select mode instead, since the (now blank)
    /// calendar isn't showing anyone's schedule there. Cross-cutting facade behavior, not
    /// either sibling's own -- see this class's own doc comment above and
    /// ScheduleCalendarViewModel's, which deliberately left this one here rather than
    /// reproducing it.</summary>
    public string CalendarHeaderText => IsMultiSelectMode
        ? "Multiple employees -- select days, then assign"
        : Tree.SelectedEmployee?.DisplayName ?? "Select an employee";

    /// <summary>Kept on the facade -- see this class's own doc comment above for why this
    /// three-line body (rather than a one-line forward) is exactly the "genuinely
    /// cross-cutting" case that doc comment describes. Flips the shared flag (triggering the
    /// MultiSelectModeState.PropertyChanged cascade wired up in the constructor above), then
    /// calls Calendar.RequestScheduleRefresh(visibly: true) explicitly -- that method itself
    /// stays Calendar-owned (see its own doc comment on ScheduleCalendarViewModel), since
    /// refreshing the calendar's contents isn't part of the multi-select flag flip itself, the
    /// same "child subscribes to / is called by whoever needs it" layering the rest of this
    /// split already follows.</summary>
    [RelayCommand]
    private void ToggleMultiSelectMode()
    {
        IsMultiSelectMode = !IsMultiSelectMode;
        Calendar.RequestScheduleRefresh(visibly: true);
    }

    public string DisplayedMonthText => Calendar.DisplayedMonthText;

    public IRelayCommand PreviousMonthCommand => Calendar.PreviousMonthCommand;
    public IRelayCommand NextMonthCommand => Calendar.NextMonthCommand;
    public IAsyncRelayCommand RecalculateScheduleCommand => Calendar.RecalculateScheduleCommand;

    public IAsyncRelayCommand LoadCommand => Tree.LoadCommand;

    /// <summary>Schedule-page navigation entry point beyond LoadCommand (Tree's own) -- the
    /// calendar's company-wide holiday markers aren't tied to an employee selection, so
    /// they'd otherwise only load once one is picked (see
    /// ScheduleCalendarViewModel.RefreshHolidaysAsync). Called from
    /// SchedulePage.OnNavigatedToAsync.</summary>
    internal Task RefreshCalendarHolidaysAsync() => Calendar.RefreshHolidaysAsync();

    public int SelectedEmployeeCount => Tree.SelectedEmployeeCount;

    public IEnumerable<Department> RealDepartments => Tree.RealDepartments;

    public IAsyncRelayCommand AddDepartmentCommand => Tree.AddDepartmentCommand;
    public IAsyncRelayCommand DeleteDepartmentCommand => Tree.DeleteDepartmentCommand;
    public IAsyncRelayCommand AddEmployeeCommand => Tree.AddEmployeeCommand;
    public IAsyncRelayCommand EditEmployeeCommand => Tree.EditEmployeeCommand;
    public IAsyncRelayCommand DeleteEmployeeCommand => Tree.DeleteEmployeeCommand;
    public IAsyncRelayCommand BlacklistEmployeeCommand => Tree.BlacklistEmployeeCommand;
    public IAsyncRelayCommand UnblacklistEmployeeCommand => Tree.UnblacklistEmployeeCommand;

    public IAsyncRelayCommand<ScheduleType?> SetScheduleForSelectionCommand => Assignment.SetScheduleForSelectionCommand;
    public IAsyncRelayCommand SetLeaveForSelectionCommand => Assignment.SetLeaveForSelectionCommand;
    public IRelayCommand ClearEmployeeSelectionCommand => Tree.ClearEmployeeSelectionCommand;
    public IAsyncRelayCommand ClearScheduleForSelectionCommand => Assignment.ClearScheduleForSelectionCommand;
    public IRelayCommand ClearCalendarSelectionCommand => Calendar.ClearCalendarSelectionCommand;
    public IAsyncRelayCommand<CalendarDayViewModel> AddManualEntryForDayCommand => Assignment.AddManualEntryForDayCommand;

    /// <summary>Calendar right-click "Edit Punch Pairing…" -- see
    /// ScheduleAssignmentViewModel.EditPunchPairingForDayAsync. Reached only from
    /// MonthCalendarControl.BuildDayContextMenu (no button on SchedulePage.xaml), same as
    /// AddManualEntryForDayCommand just above.</summary>
    public IAsyncRelayCommand<CalendarDayViewModel> EditPunchPairingForDayCommand => Assignment.EditPunchPairingForDayCommand;

    /// <summary>Calendar right-click "Mark as Holiday…" / "Remove Holiday" -- see
    /// ScheduleAssignmentViewModel.ToggleHolidayForSelectionAsync. Reached only from
    /// MonthCalendarControl.BuildDayContextMenu (no button on SchedulePage.xaml), same as
    /// AddManualEntryForDayCommand above.</summary>
    public IAsyncRelayCommand ToggleHolidayForSelectionCommand => Assignment.ToggleHolidayForSelectionCommand;

    public IAsyncRelayCommand ExportScheduleCommand => ImportExport.ExportScheduleCommand;
    public IAsyncRelayCommand ImportScheduleCommand => ImportExport.ImportScheduleCommand;
    public IAsyncRelayCommand ImportEmployeesCommand => ImportExport.ImportEmployeesCommand;

    // ---- View-state persistence ----

    /// <summary>Writes whichever employee, department, and month are currently showing into
    /// the shared, in-memory ViewStateStore (see its own doc comment -- session-only now,
    /// nothing here reaches disk or survives a restart). Deliberately one method reading
    /// current values off both Tree and Calendar, rather than each writing its own slice
    /// independently, so anything else reading this state mid-session always sees a
    /// consistent combination -- same reasoning
    /// AttendanceViewModel.SaveViewState's own doc comment gives for itself. Passed into both
    /// Tree's and Calendar's constructors as a plain Action (see this class's own doc comment
    /// above), so those children can trigger a save at their own property-changed moments
    /// without needing to know this class exists.
    ///
    /// Skips saving before the tree has been restored at least once this run (see
    /// Tree.HasRestoredSelection) -- without this, Calendar's own constructor-time
    /// DisplayedMonth assignment (which fires this same handler before Tree.LoadAsync has had
    /// a chance to restore SelectedEmployee/SelectedDepartment from the freshly loaded tree)
    /// would write "nothing selected" over a previously-saved employee/department selection.
    /// Same guard shape AttendanceViewModel.SaveViewState uses against ReportScope.
    /// HasRestoredScope.</summary>
    private void SaveViewState()
    {
        if (!Tree.HasRestoredSelection) return;

        var state = _viewStateStore.Schedule;
        state.SelectedEmployeeId = Tree.SelectedEmployee?.Id;
        state.SelectedDepartmentId = Tree.SelectedDepartment?.Id;
        state.DisplayedMonth = Calendar.DisplayedMonth;
        _viewStateStore.Save();
    }
}
