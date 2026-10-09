using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// The Schedule tab's writes: Set/Edit Schedule, Set Leave and Remove Schedule for the
/// calendar's highlighted days -- for the selected employee, or in multi-select mode for every
/// employee checked in the tree -- plus Mark/Remove Holiday and the calendar tiles' Add Manual
/// Entry and Edit Punch Pairing.
///
/// Every write goes through the app-wide <see cref="AttendanceBusyState"/>, and the commands
/// are disabled while it runs rather than deferring: by the time one writes, the person has
/// already decided something in a dialog, and silently queuing that behind unrelated work
/// would be worse than not letting the click start. Each write then reloads the calendar
/// *after* its own busy window closes (see
/// ScheduleCalendarViewModel.RefreshScheduleForSelectedEmployeeAsync), and only if the same
/// employee is still selected -- a selection that moved on mid-write has already queued its
/// own reload.
/// </summary>
public partial class ScheduleAssignmentViewModel : ViewModelBase
{
    private const string NoDaysSelected =
        "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.";

    private readonly IScheduleRepository _repository;

    /// <summary>Written by Mark/Remove Holiday; the calendar reads it back.</summary>
    private readonly IHolidayRepository _holidayRepository;

    private readonly AttendanceSettings _attendanceSettings;

    /// <summary>Only for the overtime/night diff rates, shown as the Set Schedule dialog's
    /// placeholders.</summary>
    private readonly PayrollPolicy _payrollPolicy;

    private readonly AttendanceBusyState _busy;

    /// <summary>Bumped after every schedule or holiday write, so the other tabs pick it
    /// up.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>The Attendance tab's own editor, so a manual entry added from a calendar tile
    /// takes the same path as one added there.</summary>
    private readonly ManualEntryEditorViewModel _manualEntryEditor;

    private readonly EmployeeTreeViewModel _tree;
    private readonly ScheduleCalendarViewModel _calendar;

    /// <summary>Read to pick the bulk branch, and switched off after a bulk write
    /// succeeds.</summary>
    private readonly MultiSelectModeState _multiSelectMode;

    /// <summary>Shared with the Attendance Summary's identical item, so both load, show and
    /// save a day's pairing one way.</summary>
    private readonly IDayPunchPairingEditorLauncher _pairingLauncher;

    private readonly IObservable<bool> _canSetSchedule;
    private readonly IObservable<bool> _canClearSchedule;
    private readonly IObservable<bool> _notBusy;

    public ScheduleAssignmentViewModel(
        IScheduleRepository repository,
        IHolidayRepository holidayRepository,
        IStatusBarService statusBarService,
        AttendanceSettings attendanceSettings,
        PayrollSettings payrollSettings,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        ManualEntryEditorViewModel manualEntryEditor,
        EmployeeTreeViewModel tree,
        ScheduleCalendarViewModel calendar,
        MultiSelectModeState multiSelectMode,
        IDayPunchPairingEditorLauncher pairingLauncher)
        : base(statusBarService)
    {
        _repository = repository;
        _holidayRepository = holidayRepository;
        _attendanceSettings = attendanceSettings;
        _payrollPolicy = payrollSettings.Policy;
        _busy = busy;
        _dataVersion = dataVersion;
        _manualEntryEditor = manualEntryEditor;
        _tree = tree;
        _calendar = calendar;
        _multiSelectMode = multiSelectMode;
        _pairingLauncher = pairingLauncher;

        _notBusy = _busy.WhenAnyValue(b => b.IsRunning).Select(running => !running);
        var multiSelect = _multiSelectMode.WhenAnyValue(m => m.IsMultiSelectMode);

        // In multi-select mode, Set/Leave need someone checked to apply to.
        _canSetSchedule = Observable.CombineLatest(_notBusy, multiSelect, _tree.WhenAnyValue(t => t.SelectedEmployeeCount),
                (idle, bulk, checkedCount) => idle && (!bulk || checkedCount > 0))
            .DistinctUntilChanged();

        // Remove Schedule has no bulk form.
        _canClearSchedule = Observable.CombineLatest(_notBusy, multiSelect, (idle, bulk) => idle && !bulk)
            .DistinctUntilChanged();

        ReportFailuresOf(SetScheduleForSelectionCommand, SetLeaveForSelectionCommand, ClearScheduleForSelectionCommand,
            ToggleHolidayForSelectionCommand, AddManualEntryForDayCommand, EditPunchPairingForDayCommand);
    }

    /// <summary>
    /// Set/Edit Schedule for the highlighted days: one schedule, chosen once in the dialog,
    /// replacing whatever each day had. For one employee the dialog prefills what the days
    /// share and frames it as an edit; in multi-select mode it applies to every checked
    /// employee, without either. <paramref name="presetType"/> is the calendar's "Set
    /// Schedule As" pick, preselected in the dialog; null for the plain button.
    /// </summary>
    [ReactiveCommand(CanExecute = nameof(_canSetSchedule))]
    private async Task SetScheduleForSelectionAsync(ScheduleType? presetType)
    {
        if (_multiSelectMode.IsMultiSelectMode)
        {
            await AssignScheduleToCheckedEmployeesAsync(presetType);
            return;
        }

        if (_tree.SelectedEmployee is not { } employee)
        {
            StatusBar.ShowCaution("Select an employee first.", "No employee selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            StatusBar.ShowCaution(NoDaysSelected, "No days selected");
            return;
        }

        var (isEditing, prefill) = _calendar.AnalyzeSelectionSchedule(selectedDates);
        if (await ChooseScheduleAsync([employee], selectedDates, isEditing, prefill, presetType) is not { } choice) return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await SetScheduleAsync(employee.Pin, selectedDates, choice, cancellationToken);
            _dataVersion.BumpScheduleForEmployees([employee.Pin]);
        },
        onError: ex => ShowFailure(ex, "Could not save the schedule"));

        await ReloadCalendarIfStillShowingAsync(employee.Id);
    }

    private async Task AssignScheduleToCheckedEmployeesAsync(ScheduleType? presetType)
    {
        if (CheckedEmployeesAndDays() is not { } target) return;
        var (employees, selectedDates) = target;

        if (await ChooseScheduleAsync(employees, selectedDates, isEditing: false, prefill: null, presetType) is not { } choice) return;

        var shownEmployeeId = _tree.SelectedEmployee?.Id;
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // One at a time -- the DbContext can't take concurrent operations.
            foreach (var employee in employees)
                await SetScheduleAsync(employee.Pin, selectedDates, choice, cancellationToken);
            _dataVersion.BumpScheduleForEmployees([.. employees.Select(e => e.Pin)]);

            // Before the reload below reads it: the calendar shows a single employee again.
            _multiSelectMode.IsMultiSelectMode = false;

            StatusBar.ShowSuccess(
                $"Schedule applied to {employees.Count} employee(s) across {selectedDates.Count} day(s).",
                "Bulk assign complete");
        },
        onError: ex => ShowFailure(ex, "Could not assign the schedule"));

        await ReloadCalendarIfStillShowingAsync(shownEmployeeId);
    }

    /// <summary>Shows the Set Schedule dialog; null if cancelled.</summary>
    private async Task<ScheduleChoice?> ChooseScheduleAsync(
        IReadOnlyList<Employee> employees, List<DateOnly> selectedDates, bool isEditing, ScheduleEntry? prefill, ScheduleType? presetType)
    {
        var editor = new ApplyScheduleViewModel(
            employees, ScheduleCalendarViewModel.GroupIntoContiguousRanges(selectedDates), isEditing, prefill, presetType,
            _attendanceSettings, _payrollPolicy);
        return await ShowDialogAsync(editor) ? editor.AcceptedChoice : null;
    }

    private Task SetScheduleAsync(int pin, List<DateOnly> dates, ScheduleChoice choice, CancellationToken cancellationToken) =>
        _repository.SetScheduleForDatesAsync(
            pin, dates, choice.ScheduleType, choice.WorkTimeHours, choice.TimeIn, choice.FlexibleSegments,
            choice.ClockInBufferBeforeHours, choice.ClockInBufferAfterHours,
            choice.ClockOutBufferBeforeHours, choice.ClockOutBufferAfterHours,
            choice.IsPaidLeave,
            choice.OvertimeEligibleOverride, choice.NightDiffEligibleOverride, choice.ApplyOvertimeRatePercentageOverride,
            choice.OvertimeRatePercentageOverride, choice.NightDiffRatePercentageOverride,
            choice.RestrictedTimeIn, choice.RestrictedTimeOut,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Sets the highlighted days straight to Leave, with no dialog -- Leave has nothing else to
    /// ask (the "Set Schedule As" submenu's Leave item). Choosing Leave in the Set Schedule
    /// dialog still works as before. Same multi-select branching as Set Schedule.
    /// </summary>
    [ReactiveCommand(CanExecute = nameof(_canSetSchedule))]
    private async Task SetLeaveForSelectionAsync()
    {
        if (_multiSelectMode.IsMultiSelectMode)
        {
            await SetLeaveForCheckedEmployeesAsync();
            return;
        }

        if (_tree.SelectedEmployee is not { } employee)
        {
            StatusBar.ShowCaution("Select an employee first.", "No employee selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            StatusBar.ShowCaution(NoDaysSelected, "No days selected");
            return;
        }

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _repository.SetScheduleForDatesAsync(
                employee.Pin, selectedDates, ScheduleType.Leave, workTimeHours: null, timeIn: null,
                cancellationToken: cancellationToken);
            _dataVersion.BumpScheduleForEmployees([employee.Pin]);

            StatusBar.ShowSuccess($"Set to Leave for {selectedDates.Count} day(s).");
        },
        onError: ex => ShowFailure(ex, "Could not set Leave"));

        await ReloadCalendarIfStillShowingAsync(employee.Id);
    }

    private async Task SetLeaveForCheckedEmployeesAsync()
    {
        if (CheckedEmployeesAndDays() is not { } target) return;
        var (employees, selectedDates) = target;

        var shownEmployeeId = _tree.SelectedEmployee?.Id;
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            foreach (var employee in employees)
            {
                await _repository.SetScheduleForDatesAsync(
                    employee.Pin, selectedDates, ScheduleType.Leave, workTimeHours: null, timeIn: null,
                    cancellationToken: cancellationToken);
            }
            _dataVersion.BumpScheduleForEmployees([.. employees.Select(e => e.Pin)]);

            _multiSelectMode.IsMultiSelectMode = false;

            StatusBar.ShowSuccess(
                $"Set to Leave for {employees.Count} employee(s) across {selectedDates.Count} day(s).",
                "Bulk assign complete");
        },
        onError: ex => ShowFailure(ex, "Could not set Leave"));

        await ReloadCalendarIfStillShowingAsync(shownEmployeeId);
    }

    /// <summary>The checked employees and highlighted days a bulk write applies to -- null,
    /// having said what's missing, when either is empty.</summary>
    private (List<Employee> Employees, List<DateOnly> Dates)? CheckedEmployeesAndDays()
    {
        var employees = _tree.GetCheckedEmployees();
        if (employees.Count == 0)
        {
            StatusBar.ShowCaution("Check one or more employees in the tree first (each has a checkbox).", "No employees selected");
            return null;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            StatusBar.ShowCaution(NoDaysSelected, "No days selected");
            return null;
        }

        return (employees, selectedDates);
    }

    /// <summary>Reloads the calendar once a write is done -- unless the selection moved on to
    /// someone else meanwhile, whose own reload is already queued.</summary>
    private async Task ReloadCalendarIfStillShowingAsync(int? employeeId)
    {
        if (_tree.SelectedEmployee?.Id == employeeId)
            await _calendar.RefreshScheduleForSelectedEmployeeAsync(visibly: true);
    }

    /// <summary>Removes the schedule from every highlighted day -- the selected employee's
    /// only.</summary>
    [ReactiveCommand(CanExecute = nameof(_canClearSchedule))]
    private async Task ClearScheduleForSelectionAsync()
    {
        if (_tree.SelectedEmployee is not { } employee)
        {
            StatusBar.ShowCaution("Select an employee first.", "No employee selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            StatusBar.ShowCaution(NoDaysSelected, "No days selected");
            return;
        }

        if (!await ConfirmAsync(
                $"Remove the schedule for {selectedDates.Count} selected day(s)? This cannot be undone.",
                "Confirm remove", isWarning: true))
            return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _repository.ClearScheduleForDatesAsync(employee.Pin, selectedDates, cancellationToken);
            _dataVersion.BumpScheduleForEmployees([employee.Pin]);
            StatusBar.ShowSuccess($"Schedule removed for {selectedDates.Count} day(s).");
        },
        onError: ex => ShowFailure(ex, "Could not remove the schedule"));

        await ReloadCalendarIfStillShowingAsync(employee.Id);
    }

    /// <summary>
    /// The calendar-side counterpart to Manage Holidays: marks the one highlighted day a
    /// holiday (asking its name), or removes the holiday from every highlighted day that has
    /// one. Company-wide, so whoever is selected doesn't matter. Marking is one day at a time
    /// -- each holiday has its own name -- which the calendar's menu already enforces.
    ///
    /// The highlight is put back after the grid rebuilds, unlike after a schedule write: a
    /// toggle is a quick flip often followed by another, and the "H" appearing on the day still
    /// selected confirms it.
    /// </summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task ToggleHolidayForSelectionAsync()
    {
        var selectedDays = _calendar.CalendarDays.Where(d => d.IsSelected).ToList();
        if (selectedDays.Count == 0)
        {
            StatusBar.ShowCaution(NoDaysSelected, "No days selected");
            return;
        }

        var selectedDates = selectedDays.Select(d => d.Date).ToHashSet();
        var toRemove = selectedDays.Where(d => d.IsHoliday).Select(d => d.Date).ToList();
        var changed = false;

        if (toRemove.Count > 0)
        {
            if (!await ConfirmAsync(
                    $"Remove {toRemove.Count} holiday(s) from the company-wide list? This affects payroll for every employee.",
                    "Confirm remove", isWarning: true))
                return;

            await _busy.RunAsync(visibly: true, async cancellationToken =>
            {
                // Date -> Id now (Holiday.Date is unique) -- one read of a tiny table beats
                // threading Ids through every cell.
                var idsByDate = (await _holidayRepository.ListAsync(cancellationToken)).ToDictionary(h => h.Date, h => h.Id);
                foreach (var date in toRemove)
                {
                    if (idsByDate.TryGetValue(date, out var id))
                        await _holidayRepository.DeleteAsync(id, cancellationToken);
                }
                _dataVersion.BumpHoliday();
                changed = true;
                StatusBar.ShowSuccess($"Removed {toRemove.Count} holiday(s).");
            },
            onError: ex => ShowFailure(ex, "Could not remove the holiday"));
        }
        else
        {
            if (selectedDates.Count != 1)
            {
                StatusBar.ShowCaution("Select a single day to mark as a holiday.", "One day at a time");
                return;
            }

            var date = selectedDates.Single();
            var prompt = new TextPromptViewModel("Mark as Holiday", "Holiday name (applies company-wide):", "Holiday");
            if (!await ShowDialogAsync(prompt)) return;

            await _busy.RunAsync(visibly: true, async cancellationToken =>
            {
                try
                {
                    await _holidayRepository.AddAsync(new Holiday { Date = date, Name = prompt.Value }, cancellationToken);
                    _dataVersion.BumpHoliday();
                    StatusBar.ShowSuccess($"Marked {date:MMMM d, yyyy} as a holiday.");
                }
                catch (DuplicateHolidayDateException)
                {
                    // Someone else marked it meanwhile -- it's a holiday either way, as asked.
                }

                changed = true;
            },
            onError: ex => ShowFailure(ex, "Could not mark the holiday"));
        }

        if (!changed) return;

        // Just the holidays -- a toggle changes no schedule or attendance marker.
        await _calendar.RefreshHolidaysAsync();
        _calendar.SelectDates(selectedDates);
    }

    /// <summary>A calendar tile's "Add Manual Entry…" (offered for a single Partial/Absent
    /// day): the Attendance tab's own add, then the tile's marker recomputed straight away.
    /// Held under this page's busy state too, so no schedule write or payroll refresh races the
    /// save while the dialog is open.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task AddManualEntryForDayAsync(CalendarDayViewModel day)
    {
        if (_tree.SelectedEmployee is not { } employee) return;

        await _busy.RunAsync(visibly: true, _ => _manualEntryEditor.AddManualEntryForDayAsync(employee, day.Date),
            onError: ex => ShowFailure(ex, "Couldn't add the manual entry"));

        // Recomputed even if the dialog was cancelled -- there's no telling here, and a
        // redundant recompute is harmless.
        if (_tree.SelectedEmployee?.Id == employee.Id)
            await _calendar.RefreshCalendarAttendanceStatusesAsync();
    }

    /// <summary>A calendar tile's "Edit Punch Pairing…"/"View Punches…": the shared launcher's
    /// load/show/save, then the tile's marker recomputed -- only if something was saved. (The
    /// Attendance Summary picks a change up from PairingVersion; the calendar's markers don't
    /// follow it.)</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task EditPunchPairingForDayAsync(CalendarDayViewModel day)
    {
        if (_tree.SelectedEmployee is not { } employee) return;

        var saved = false;
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            saved = await _pairingLauncher.OpenAsync(employee, day.Date, day.AttendanceStatus, ShowDialog, cancellationToken);
        }, onError: ex => ShowFailure(ex, "Couldn't open the punch pairing"));

        if (saved && _tree.SelectedEmployee?.Id == employee.Id)
            await _calendar.RefreshCalendarAttendanceStatusesAsync();
    }
}
