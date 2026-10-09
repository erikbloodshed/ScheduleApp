using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Schedule;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// The Schedule and Employees pages' composition root (one scoped instance shared by both, and
/// the app-wide <see cref="IEmployeeSelection"/> the Payroll tab follows). Its children do the
/// work and the views bind to them directly:
/// <list type="bullet">
/// <item><see cref="Tree"/> -- the Department/Employee tree and everything that edits it.</item>
/// <item><see cref="Calendar"/> -- month navigation, the day grid and its markers.</item>
/// <item><see cref="Assignment"/> -- schedule, leave and holiday writes for the selected days.</item>
/// <item><see cref="ImportExport"/> -- Excel import/export of schedules and employees.</item>
/// </list>
/// What stays here is what spans them: multi-select mode (and its header and button text),
/// the first-visit load, and saving the selection and month as one consistent view state.
/// </summary>
public sealed partial class MainViewModel : ReactiveObject, IEmployeeSelection
{
    private readonly ViewStateStore _viewStateStore;

    /// <summary>Shared by the calendar (which goes blank in it) and the assignment (which
    /// writes to every checked employee in it, and leaves it after).</summary>
    private readonly MultiSelectModeState _multiSelectMode = new();

    private bool _opened;

    public MainViewModel(
        IScheduleRepository repository, IHolidayRepository holidayRepository, IStatusBarService statusBarService,
        ViewStateStore viewStateStore,
        AttendanceSettings attendanceSettings, IAttendanceRunner attendanceRunner, AttendanceBusyState busy,
        AttendanceDataVersion dataVersion, PayrollSettings payrollSettings, ManualEntryEditorViewModel manualEntryEditor,
        ActiveRosterProvider activeRosterProvider, IDayPunchPairingEditorLauncher pairingLauncher)
    {
        _viewStateStore = viewStateStore;
        ManualEntryEditor = manualEntryEditor;

        Tree = new EmployeeTreeViewModel(repository, statusBarService, viewStateStore, dataVersion, SaveViewState, busy,
            payrollSettings.Policy, attendanceSettings);
        Calendar = new ScheduleCalendarViewModel(
            repository, holidayRepository, statusBarService, attendanceSettings, attendanceRunner, busy, viewStateStore,
            SaveViewState, Tree, _multiSelectMode, dataVersion);
        Assignment = new ScheduleAssignmentViewModel(
            repository, holidayRepository, statusBarService, attendanceSettings, payrollSettings, busy, dataVersion,
            manualEntryEditor, Tree, Calendar, _multiSelectMode, pairingLauncher);
        ImportExport = new ScheduleImportExportViewModel(
            repository, statusBarService, dataVersion, Tree, Calendar, activeRosterProvider);

        var multiSelect = _multiSelectMode.WhenAnyValue(m => m.IsMultiSelectMode);
        _isMultiSelectModeHelper = multiSelect.ToProperty(this, x => x.IsMultiSelectMode);
        _multiSelectButtonTextHelper = multiSelect
            .Select(on => on ? "Cancel Multi-Select" : "Assign Schedule to Multiple Employees")
            .ToProperty(this, x => x.MultiSelectButtonText);

        // The calendar shows nobody's schedule in multi-select mode.
        _calendarHeaderTextHelper = Observable.CombineLatest(multiSelect, Tree.WhenAnyValue(t => t.SelectedEmployee),
                (on, employee) => on
                    ? "Multiple employees -- select days, then assign"
                    : employee?.DisplayName ?? "Select an employee")
            .ToProperty(this, x => x.CalendarHeaderText);

        Tree.WhenAnyValue(t => t.SelectedEmployee)
            .Skip(1)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(SelectedEmployee)));

        // Leaving the mode -- by cancelling, or after a bulk write -- unchecks everyone: the
        // checkboxes are about to disappear, and what they held would be invisible state.
        multiSelect
            .Skip(1)
            .Where(on => !on)
            .Subscribe(_ => Tree.ClearEmployeeSelection());
    }

    public EmployeeTreeViewModel Tree { get; }
    public ScheduleCalendarViewModel Calendar { get; }
    public ScheduleAssignmentViewModel Assignment { get; }
    public ScheduleImportExportViewModel ImportExport { get; }

    /// <summary>The Attendance tab's manual-entry editor -- the calendar's "Add Manual Entry…"
    /// opens its dialog, so the Schedule page answers its interactions.</summary>
    public ManualEntryEditorViewModel ManualEntryEditor { get; }

    /// <summary>The app-wide selected employee (IEmployeeSelection) -- the tree's.</summary>
    public Employee? SelectedEmployee
    {
        get => Tree.SelectedEmployee;
        set => Tree.SelectedEmployee = value;
    }

    /// <summary>Checkboxes in the tree; the Set/Leave buttons apply to every checked
    /// employee.</summary>
    [ObservableAsProperty]
    public partial bool IsMultiSelectMode { get; }

    [ObservableAsProperty(InitialValue = "Assign Schedule to Multiple Employees")]
    public partial string MultiSelectButtonText { get; }

    /// <summary>The selected employee's name, or what multi-select mode is for.</summary>
    [ObservableAsProperty(InitialValue = "Select an employee")]
    public partial string CalendarHeaderText { get; }

    [ReactiveCommand]
    private void ToggleMultiSelectMode()
    {
        _multiSelectMode.IsMultiSelectMode = !_multiSelectMode.IsMultiSelectMode;
        Calendar.RequestScheduleRefresh(visibly: true);
    }

    /// <summary>The Schedule page's first visit each run: the tree (restoring last time's
    /// selection), then the company-wide holidays -- which no employee selection would load on
    /// a fresh run with nobody restored. Later visits do nothing; the data stays loaded.</summary>
    [ReactiveCommand]
    private async Task OpenAsync()
    {
        if (_opened) return;
        _opened = true;

        await Tree.LoadAsync();
        await Calendar.RefreshHolidaysAsync();
    }

    /// <summary>Writes the selected employee, department and month as one snapshot into the
    /// session-only ViewStateStore. Skipped until the tree has restored last time's selection:
    /// the calendar's opening month would otherwise overwrite it with "nothing
    /// selected".</summary>
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
