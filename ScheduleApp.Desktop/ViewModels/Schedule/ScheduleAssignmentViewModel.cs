using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// Backs the Schedule tab's Set/Clear Schedule and Set Leave commands -- both the
/// single-employee flow (targets EmployeeTreeViewModel.SelectedEmployee, the calendar's
/// highlighted days) and the multi-select bulk flow (targets every employee checked in
/// EmployeeTreeViewModel's tree instead) -- plus the calendar's right-click "Add Manual
/// Entry…" command (see MainViewModel-Split-Plan.md's component list). Every write here
/// goes through the same repository/ScheduleDbContext the tree and calendar already share,
/// which is why this class needs a reference to both rather than owning any tree or
/// calendar state of its own.
///
/// Takes EmployeeTreeViewModel (phase 2) and ScheduleCalendarViewModel (phase 3) as
/// constructor dependencies -- same "one child depends on another child" shape
/// ReportViewModel already establishes for ReportScopeViewModel (see the split plan's "Two
/// more precedents" section), not a new risk this plan is introducing. Reads
/// Tree.SelectedEmployee/GetCheckedEmployees() for who a write targets, and
/// Calendar.GetSelectedDates()/AnalyzeSelectionSchedule()/RefreshScheduleForSelectedEmployeeAsync()
/// for which days and how the calendar reacts afterward -- see each of those members' own
/// doc comments (on EmployeeTreeViewModel/ScheduleCalendarViewModel respectively) for why
/// they're public despite nothing outside their own file calling them until now.
/// GroupIntoContiguousRanges is called by type name (ScheduleCalendarViewModel.
/// GroupIntoContiguousRanges(...)), not through the Calendar reference, since it's static
/// and a static member can't be reached through an instance -- see that method's own doc
/// comment.
///
/// Also takes MultiSelectModeState (phase 1) as a constructor dependency -- unlike
/// ScheduleCalendarViewModel, which only ever reads it, this class both reads it
/// (CanSetScheduleForSelection, CanClearScheduleForSelection, and the Set/Leave commands'
/// own multi-select branching) and *writes* it back to false after a successful bulk Set/
/// Leave operation (AssignScheduleToCheckedEmployeesAsync/SetLeaveForCheckedEmployeesAsync),
/// relying on the real property setter to eventually fire the existing
/// OnIsMultiSelectModeChanged-shaped cascade -- see MultiSelectModeState's own doc comment
/// for the full "who reads, who writes" breakdown, and the note below for why that cascade
/// doesn't actually fire yet.
///
/// Pure extraction as planned -- MainViewModel itself is untouched, still owns its own copy
/// of every member moved here (including its own separate EmployeeTreeViewModel/
/// ScheduleCalendarViewModel/MultiSelectModeState-shaped state, none of which it actually
/// shares with this class or its phase 2/3 siblings yet), and no other class references
/// this one yet. One practical consequence of that: setting
/// _multiSelectMode.IsMultiSelectMode = false below is currently inert beyond flipping the
/// raw flag -- nothing subscribes to MultiSelectModeState.PropertyChanged yet to clear
/// Tree's checked employees or refresh MultiSelectButtonText/CalendarHeaderText the way
/// OnIsMultiSelectModeChanged does on today's MainViewModel. That cascade is deliberately
/// cross-cutting facade behavior per MultiSelectModeState's own doc comment, so it's phase
/// 6's job to add the subscription that makes this write actually do something, not this
/// class's.
///
/// Extracted per the split plan's phase 4 ("takes EmployeeTreeViewModel,
/// ScheduleCalendarViewModel, and MultiSelectModeState") -- nothing in this table's own
/// "Depends on" column for this class was missing (IScheduleRepository, IStatusBarService,
/// AttendanceSettings, PayrollPolicy, the shared _busy/_dataVersion/ManualEntryEditorViewModel
/// instances, references to EmployeeTreeViewModel and ScheduleCalendarViewModel, and the
/// shared MultiSelectModeState instance get-and-set are all accounted for below), though see
/// _payrollPolicy's own doc comment for a shape clarification the table's plain "PayrollPolicy"
/// entry glossed over, and see ScheduleCalendarViewModel.RefreshCalendarAttendanceStatusesAsync's
/// own doc comment for one gap this phase found and fixed in that file. Phase 6 rebuilds
/// MainViewModel as the facade that constructs this class (last of the four, after Tree,
/// Calendar, and MultiSelectModeState all exist) and forwards its members flatly, the same
/// way AttendanceViewModel forwards ReportViewModel's.
///
/// A second gap surfaced later, once this class's five schedule-write commands (and
/// AddManualEntryForDayAsync) were actually exercised: each called
/// Calendar.RefreshScheduleForSelectedEmployeeAsync (or, for AddManualEntryForDayAsync,
/// Calendar.RefreshCalendarAttendanceStatusesAsync directly) nested inside its own
/// _busy.RunAsync, which deferred the calendar's attendance-marker recompute into a
/// second, hidden busy cycle instead of running it in the first -- see
/// SetScheduleForSelectionAsync's and AddManualEntryForDayAsync's own doc comments below,
/// and ScheduleCalendarViewModel.RefreshScheduleForSelectedEmployeeAsync's own doc
/// comment, for the fix.
///
/// Not yet compiled against the real project (same no-SDK caveat as phases 1-3) -- flagging
/// this again for phase 5's author, same as phases 1 through 3 each did for the phase right
/// after them.
/// </summary>
public partial class ScheduleAssignmentViewModel : ObservableObject
{
    private readonly IScheduleRepository _repository;

    /// <summary>The company-wide Holidays table (see Holiday/IHolidayRepository) --
    /// written by ToggleHolidayForSelectionAsync below, the calendar-side counterpart to
    /// ManageHolidaysDialog's Add/Delete. Same scoped instance ManageHolidaysDialog and
    /// PayrollComputationService already receive (see App.xaml.cs). ScheduleCalendarViewModel
    /// holds its own reference for the read side (each cell's "H" marker) -- this class only
    /// writes, then leans on Calendar.RefreshScheduleForSelectedEmployeeAsync to reload the
    /// marker set, the same follow-up-refresh shape the schedule-write commands here already
    /// use.</summary>
    private readonly IHolidayRepository _holidayRepository;

    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceSettings _attendanceSettings;

    /// <summary>Only used to seed ApplyScheduleDialog's grayed-out placeholder text for the
    /// Overtime/Night Diff rate-percentage override boxes -- the same role _attendanceSettings.
    /// Policy plays for the buffer-override boxes (see MainViewModel._payrollPolicy's own doc
    /// comment on today's still-unmodified MainViewModel for the fuller story, including why
    /// this is a different policy object from AttendanceSettings.Policy).
    ///
    /// Constructor-injected as PayrollSettings, not PayrollPolicy directly, and this field
    /// holds payrollSettings.Policy read out in the constructor -- same shape MainViewModel's
    /// own constructor already uses today. The component-list table's "Depends on" column
    /// just says "PayrollPolicy", but there's no App.xaml.cs registration for that type on
    /// its own (only `services.AddSingleton(payrollSettings)` -- see that file's registration
    /// list), so a constructor parameter of PayrollPolicy itself wouldn't resolve through DI.
    /// Flagging this the way phase 2's own AttendanceDataVersion doc comment flagged a gap in
    /// this same table -- not a functional gap here, just a shape clarification for whoever
    /// wires this into MainViewModel's constructor in phase 6.</summary>
    private readonly PayrollPolicy _payrollPolicy;

    /// <summary>The *same* instance MainViewModel receives today (see App.xaml.cs's existing
    /// registration and MainViewModel._busy's own doc comment for why -- shared with
    /// ScheduleCalendarViewModel and PayrollViewModel too, all reacting to the same
    /// SelectedEmployee change against the same shared, app-lifetime-scoped
    /// ScheduleDbContext). Not a new registration of its own: phase 6's facade passes this
    /// class the exact same _busy field it already holds and already hands Calendar.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with EmployeeTreeViewModel, AttendanceViewModel, and PayrollViewModel
    /// (same instance -- see App.xaml.cs's registration and AttendanceDataVersion.
    /// ScheduleVersion's own doc comment). Bumped here after every successful
    /// schedule-mutating write this class makes (Set/Clear Schedule, Set Leave, single or
    /// bulk) so the Attendance Summary tab's own auto-reload-on-tab-select picks the change
    /// up next time it's viewed, without this class needing to know ReportViewModel
    /// exists.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>The *same* instance AttendanceViewModel built for itself and MainViewModel
    /// already receives today (see App.xaml.cs's registration comment and MainViewModel.
    /// _manualEntryEditor's own doc comment for the fuller "why this instance, not a second
    /// one" story). AddManualEntryForDayAsync below wraps the call into this in this class's
    /// own _busy.RunAsync, exactly as MainViewModel's own equivalent does today, for the same
    /// "gate both AttendanceBusyState instances" reasoning that doc comment describes.</summary>
    private readonly ManualEntryEditorViewModel _manualEntryEditor;

    /// <summary>For SelectedEmployee and GetCheckedEmployees() -- see this class's own
    /// summary above for why this is a constructor dependency rather than this class reaching
    /// back out to a shared facade. Unlike ScheduleCalendarViewModel, this class doesn't
    /// subscribe to Tree.PropertyChanged(SelectedEmployee) itself (it has no need to react to
    /// an employee-selection change on its own), but it does subscribe to
    /// Tree.PropertyChanged(SelectedEmployeeCount) below -- see EmployeeTreeViewModel.
    /// LoadAsync/OnEmployeeNodeSelectionChanged's own doc comments, which already named this
    /// class as the one expected to pick that up once it existed.</summary>
    private readonly EmployeeTreeViewModel _tree;

    /// <summary>For GetSelectedDates(), AnalyzeSelectionSchedule(), and
    /// RefreshScheduleForSelectedEmployeeAsync() -- see this class's own summary above for why
    /// this is a constructor dependency. This class does not subscribe to Calendar's own
    /// PropertyChanged for anything -- unlike Calendar's own subscription to Tree, there's no
    /// property on ScheduleCalendarViewModel this class needs to react to; it only ever calls
    /// into Calendar synchronously, from inside its own commands.</summary>
    private readonly ScheduleCalendarViewModel _calendar;

    /// <summary>See this class's own summary above for the get-and-set role this class plays
    /// against it (ScheduleCalendarViewModel, phase 3, is the sibling that only ever reads
    /// it).</summary>
    private readonly MultiSelectModeState _multiSelectMode;

    /// <summary>Backs the calendar's right-click "Edit Punch Pairing…" (see
    /// EditPunchPairingForDayAsync below). The same shared launcher instance
    /// ReportViewModel receives for the Attendance Summary grid's identical item, so both
    /// entry points load, show, and save a day's pairing through one path -- see
    /// DayPunchPairingEditorLauncher.</summary>
    private readonly IDayPunchPairingEditorLauncher _pairingLauncher;

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
    {
        _repository = repository;
        _holidayRepository = holidayRepository;
        _statusBarService = statusBarService;
        _attendanceSettings = attendanceSettings;
        _payrollPolicy = payrollSettings.Policy;
        _busy = busy;
        _dataVersion = dataVersion;
        _manualEntryEditor = manualEntryEditor;
        _tree = tree;
        _calendar = calendar;
        _multiSelectMode = multiSelectMode;
        _pairingLauncher = pairingLauncher;

        // Keeps the Set Schedule/Set Leave/Clear Schedule buttons in sync with
        // _busy.IsRunning -- mirrors MainViewModel's own _busy.PropertyChanged(IsRunning)
        // handler today, minus the RefreshScheduleForSelectedEmployeeAsync/
        // RefreshCalendarAttendanceStatusesAsync re-run logic, which stays on
        // ScheduleCalendarViewModel's own _busy.PropertyChanged subscription -- that class's
        // own doc comment on the constructor already predicted this class would add its own
        // subscription just for its own commands' CanExecute, the same "each class subscribes
        // to what it needs" layering that class's own subscription to Tree already follows.
        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AttendanceBusyState.IsRunning)) return;

            SetScheduleForSelectionCommand.NotifyCanExecuteChanged();
            SetLeaveForSelectionCommand.NotifyCanExecuteChanged();
            ClearScheduleForSelectionCommand.NotifyCanExecuteChanged();
            AddManualEntryForDayCommand.NotifyCanExecuteChanged();
            ToggleHolidayForSelectionCommand.NotifyCanExecuteChanged();
        };

        // Closes the same gap MainViewModel's own equivalent subscription closes today --
        // see CanAddManualEntryForDay's own doc comment below for the race this avoids.
        _manualEntryEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ManualEntryEditorViewModel.IsAttendanceBusy)) return;
            AddManualEntryForDayCommand.NotifyCanExecuteChanged();
        };

        // The other half of the "child subscribes to the sibling it depends on" layering
        // EmployeeTreeViewModel.LoadAsync/OnEmployeeNodeSelectionChanged's own doc comments
        // already assigned to this class -- CanSetScheduleForSelection reads
        // Tree.SelectedEmployeeCount, so a checkbox toggle in the tree needs to re-query it,
        // the same way ScheduleCalendarViewModel's own Tree.PropertyChanged(SelectedEmployee)
        // subscription reacts to a different Tree property for a different reason.
        _tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EmployeeTreeViewModel.SelectedEmployeeCount)) return;

            SetScheduleForSelectionCommand.NotifyCanExecuteChanged();
            SetLeaveForSelectionCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Also requires !_busy.IsRunning -- SetScheduleForSelectionAsync,
    /// SetLeaveForSelectionAsync, and (via AssignScheduleToCheckedEmployeesAsync/
    /// SetLeaveForCheckedEmployeesAsync) their bulk-checked-employee equivalents all write
    /// through the shared, app-lifetime-scoped ScheduleDbContext, same as
    /// ScheduleCalendarViewModel.RefreshScheduleForSelectedEmployeeAsync -- see that class's
    /// own RequestScheduleRefresh doc comment for the "second operation started on this
    /// context" exception this guards against, and for why disabling the button (rather than
    /// deferring the click the way RequestScheduleRefresh does) is the deliberate choice here:
    /// by the time one of these commands would write, the person has already gone through a
    /// confirmation dialog and made a decision, so silently queuing that decision behind
    /// whatever's running elsewhere -- with no visible sign anything is waiting -- would be a
    /// worse experience than just not letting the click start in the first place. Notified via
    /// the _busy.PropertyChanged handler in this class's own constructor, same as every other
    /// _busy-gated command in the app.</summary>
    private bool CanSetScheduleForSelection() =>
        (!_multiSelectMode.IsMultiSelectMode || _tree.SelectedEmployeeCount > 0) && !_busy.IsRunning;

    /// <summary>
    /// Sets one schedule (type/hours/time-in, chosen once) on every day currently
    /// highlighted in the calendar -- contiguous or not, it doesn't matter, since each day is
    /// just its own independent (EmployeeId, Date) row. Replaces whatever was there before
    /// for each of those days. Same button drives both flows: outside multi-select mode it
    /// targets Tree.SelectedEmployee (with prefill/edit framing, since "what's currently set"
    /// has one clear answer for a single employee); in multi-select mode it targets every
    /// checked employee instead (see AssignScheduleToCheckedEmployeesAsync).
    ///
    /// presetType is null for the plain button and the calendar's right-click "Set/Edit
    /// Schedule..." item (unchanged behavior -- ApplyScheduleDialog falls back to Normal for
    /// a new entry, or the existing entry's own type for an edit). It's a specific
    /// ScheduleType when reached through one of the right-click "Set Schedule As" submenu
    /// items instead, which pre-selects that type in the dialog rather than leaving it to the
    /// usual default/prefill.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSetScheduleForSelection))]
    private async Task SetScheduleForSelectionAsync(ScheduleType? presetType)
    {
        if (_multiSelectMode.IsMultiSelectMode)
        {
            await AssignScheduleToCheckedEmployeesAsync(presetType);
            return;
        }

        if (_tree.SelectedEmployee is null)
        {
            _statusBarService.ShowCaution("Select an employee first.", "No employee selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.",
                "No days selected");
            return;
        }

        // Ranges are only computed for a readable summary in the dialog -- the schedule
        // itself is applied per-day, one upsert call below.
        var ranges = ScheduleCalendarViewModel.GroupIntoContiguousRanges(selectedDates);
        var (isEditing, prefill) = _calendar.AnalyzeSelectionSchedule(selectedDates);

        var dialog = new ApplyScheduleDialog(
            [_tree.SelectedEmployee], ranges, isEditing, prefill, presetType,
            _attendanceSettings.Policy.FlexibleSegmentClockInBuffer, _attendanceSettings.Policy.FlexibleSegmentClockOutBuffer,
            _attendanceSettings.Policy.ClockInBufferBefore, _attendanceSettings.Policy.ClockInBufferAfter,
            _attendanceSettings.Policy.ClockOutBufferBefore, _attendanceSettings.Policy.ClockOutBufferAfter,
            _attendanceSettings.DefaultWorkTimeHours,
            _payrollPolicy.OvertimeRatePercentage, _payrollPolicy.NightDiffRatePercentage)
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        // Captured now (see MainViewModel.DeleteEmployeeAsync's own local for the same
        // reason, still true of today's unmodified MainViewModel) rather than read as
        // Tree.SelectedEmployee.Id/Pin inside the lambda below -- nullable analysis
        // doesn't carry the null-check above across a closure the way it does within a
        // single method body. employeeId is only used below to check whether this is
        // still the same employee once the write finishes -- that's a question about
        // identity, which Id answers just as well as Pin would; employeePin is what
        // actually gets written.
        var employeeId = _tree.SelectedEmployee.Id;
        var employeePin = _tree.SelectedEmployee.Pin;

        // Wrapped in _busy.RunAsync for the same "disable the button, don't defer the
        // click" reasoning as CanSetScheduleForSelection's own doc comment -- the button
        // is already disabled while _busy.IsRunning, so this only ever starts as the
        // outermost call.
        //
        // Calendar.RefreshScheduleForSelectedEmployeeAsync is called AFTER this returns,
        // not nested inside it -- nested here, it would ride along on this call's own
        // Token via AttendanceBusyState.RunAsync's reentrancy branch (harmless on its
        // own), but its own RebuildCalendar() fire-and-forgets
        // RefreshCalendarAttendanceStatusesAsync(), which opens with an
        // `if (_busy.IsRunning)` check -- still true, since this outer call hasn't
        // returned yet -- and defers instead of computing, only actually running a
        // moment later off the _busy.PropertyChanged handler, as its own silent, second
        // busy cycle. That's the exact gap AddManualEntryForDayAsync's own doc comment
        // above found and fixed for its direct call to RefreshCalendarAttendanceStatusesAsync;
        // this is the same fix for the same trap reached through
        // RefreshScheduleForSelectedEmployeeAsync/RebuildCalendar instead -- see
        // ScheduleCalendarViewModel.RefreshScheduleForSelectedEmployeeAsync's own doc
        // comment. Called unconditionally, even if the write below failed or was
        // cancelled -- same "harmless redundant recompute, not a mutating write"
        // reasoning as that fix, and it means the calendar still reflects whatever's
        // actually in the database now rather than staying stale.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _repository.SetScheduleForDatesAsync(
                employeePin, selectedDates, dialog.ScheduleType, dialog.WorkTimeHours, dialog.TimeIn,
                dialog.FlexibleSegments,
                dialog.ClockInBufferBeforeHours, dialog.ClockInBufferAfterHours,
                dialog.ClockOutBufferBeforeHours, dialog.ClockOutBufferAfterHours,
                dialog.IsPaidLeave,
                dialog.OvertimeEligibleOverride, dialog.NightDiffEligibleOverride,
                dialog.ApplyOvertimeRatePercentageOverride,
                dialog.OvertimeRatePercentageOverride, dialog.NightDiffRatePercentageOverride,
                dialog.RestrictedTimeIn, dialog.RestrictedTimeOut,
                cancellationToken: cancellationToken);
            _dataVersion.BumpScheduleForEmployees([employeePin]);
        },
        onError: ex => _statusBarService.ShowError($"Could not save the schedule. {ex.Message}", "Save failed"));

        // A second, independent race sits underneath the "hidden busy cycle" one described
        // above: if the person switches to a different employee while the write above is
        // still in flight, Calendar's own Tree.PropertyChanged(SelectedEmployee) handler
        // fires RequestScheduleRefresh(), which sees _busy.IsRunning and defers itself via
        // _scheduleRefreshPending rather than running immediately -- but that deferred
        // refresh and this method's own unconditional call below aren't coordinated with
        // each other at all. Both ultimately target whatever Tree.SelectedEmployee is by
        // the time they run, and the _busy.PropertyChanged handler that releases the
        // deferred one fires synchronously the instant the write's own finally sets
        // IsRunning back to false -- before this method's own continuation (posted to the
        // WPF dispatcher) gets a turn -- so the deferred refresh's own _busy.RunAsync(...)
        // call below claims the gate first.
        //
        // AttendanceBusyState.RunAsync's own hardening (see its doc comment) is what
        // actually prevents this from crashing now: this call and the deferred one are on
        // different, unrelated async call chains, so neither reads as "nested" to the
        // other, and RunAsync makes whichever one loses the race wait for _gate instead of
        // running unserialized alongside the other. Without that hardening, the loser used
        // to fall into the old reentrancy branch and fire a second, concurrent
        // GetScheduleEntriesForEmployeeAsync against the same shared ScheduleDbContext the
        // winner was already mid-read on -- the "second operation was started on this
        // context instance before a previous operation completed" exception App.xaml.cs
        // already names -- and both loads also stomped on the same ScheduleEntries.Clear()
        // regardless of whether the DbContext threw first.
        //
        // The guard below is still worth keeping even with RunAsync hardened: without it,
        // this call would just queue harmlessly behind the deferred one instead of racing
        // it, but it would still run once its turn came, redundantly reloading and
        // rebuilding the calendar (a second, visible busy blip) for an employee who isn't
        // even selected anymore by the time it does. Guarding on whether the selection
        // actually moved on skips that wasted, stale refresh and leaves the already-
        // deferred one -- which reads whatever's current at the moment it finally runs --
        // as the only one that does anything; if the selection didn't move, nothing else
        // has any reason to have refreshed for employeeId, so this call is still the only
        // one that will.
        if (_tree.SelectedEmployee?.Id == employeeId)
            await _calendar.RefreshScheduleForSelectedEmployeeAsync(visibly: true);
    }

    /// <summary>
    /// Same idea as the single-employee branch of SetScheduleForSelectionAsync above, but
    /// applies one schedule (chosen once in the dialog) to every employee currently checked
    /// in Tree's tree instead of just Tree.SelectedEmployee. Each employee/day pair is still
    /// just its own independent upsert -- the bulk part is purely "loop the same values over
    /// more employees," nothing about the data model changes. Reached through
    /// SetScheduleForSelectionAsync when MultiSelectModeState.IsMultiSelectMode is on, rather
    /// than through a button of its own. presetType is just threaded straight through to
    /// ApplyScheduleDialog -- see SetScheduleForSelectionAsync's own doc comment for what it
    /// means.
    /// </summary>
    private async Task AssignScheduleToCheckedEmployeesAsync(ScheduleType? presetType)
    {
        var employees = _tree.GetCheckedEmployees();
        if (employees.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Check one or more employees in the tree first (each has a checkbox).",
                "No employees selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.",
                "No days selected");
            return;
        }

        var ranges = ScheduleCalendarViewModel.GroupIntoContiguousRanges(selectedDates);

        // Deliberately no prefill/edit framing here, unlike the single-employee branch above
        // -- "what's currently set" has no single answer across several employees.
        var dialog = new ApplyScheduleDialog(
            employees, ranges, isEditing: false, prefill: null, presetType,
            _attendanceSettings.Policy.FlexibleSegmentClockInBuffer, _attendanceSettings.Policy.FlexibleSegmentClockOutBuffer,
            _attendanceSettings.Policy.ClockInBufferBefore, _attendanceSettings.Policy.ClockInBufferAfter,
            _attendanceSettings.Policy.ClockOutBufferBefore, _attendanceSettings.Policy.ClockOutBufferAfter,
            _attendanceSettings.DefaultWorkTimeHours,
            _payrollPolicy.OvertimeRatePercentage, _payrollPolicy.NightDiffRatePercentage)
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        // Captured now, before the write loop below -- not because this write targets
        // Tree.SelectedEmployee (it targets the checked employees instead), but because
        // the follow-up refresh call at the end of this method redisplays whichever
        // employee is selected once the bulk write finishes, and needs to know whether
        // that's still the same one it was when the write started -- see this method's own
        // guard comment below for why.
        var selectedEmployeeIdBeforeWrite = _tree.SelectedEmployee?.Id;

        // See CanSetScheduleForSelection's own doc comment for why this whole per-employee
        // write loop is wrapped in one _busy.RunAsync call rather than left ungated;
        // SetScheduleForSelectionAsync (the only caller) has already confirmed the button
        // was enabled, so this only ever starts as the outermost call.
        //
        // The reload -- Calendar.RefreshScheduleForSelectedEmployeeAsync -- happens AFTER
        // this returns, not nested inside it; see SetScheduleForSelectionAsync's own doc
        // comment above for why nesting it here would defer RebuildCalendar's own
        // attendance-marker recompute into a second, hidden busy cycle instead of running
        // it in this one. _multiSelectMode.IsMultiSelectMode = false stays inside, though
        // -- the refresh call reads it (see RefreshScheduleForSelectedEmployeeAsync's own
        // multi-select branch) to decide whether to pick the calendar back up as a normal
        // single-employee view or leave it blank, so it needs to already be false by the
        // time the refresh actually runs, which it is either way now that the flag write
        // and the refresh are no longer on the same line.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // Sequential, not parallel -- the repository's DbContext isn't safe to use
            // concurrently from multiple in-flight awaits.
            foreach (var employee in employees)
            {
                await _repository.SetScheduleForDatesAsync(
                    employee.Pin, selectedDates, dialog.ScheduleType, dialog.WorkTimeHours, dialog.TimeIn,
                    dialog.FlexibleSegments,
                    dialog.ClockInBufferBeforeHours, dialog.ClockInBufferAfterHours,
                    dialog.ClockOutBufferBeforeHours, dialog.ClockOutBufferAfterHours,
                    dialog.IsPaidLeave,
                    dialog.OvertimeEligibleOverride, dialog.NightDiffEligibleOverride,
                    dialog.ApplyOvertimeRatePercentageOverride,
                    dialog.OvertimeRatePercentageOverride, dialog.NightDiffRatePercentageOverride,
                    dialog.RestrictedTimeIn, dialog.RestrictedTimeOut,
                    cancellationToken: cancellationToken);
            }
            _dataVersion.BumpScheduleForEmployees(employees.Select(e => e.Pin).ToList());

            // Leaving multi-select mode hides the checkboxes again and clears them -- once
            // phase 6 wires the facade's own MultiSelectModeState.PropertyChanged
            // subscription (see this class's own summary above for why that cascade doesn't
            // fire yet).
            _multiSelectMode.IsMultiSelectMode = false;

            _statusBarService.ShowSuccess(
                $"Schedule applied to {employees.Count} employee(s) across {selectedDates.Count} day(s).",
                "Bulk assign complete");
        },
        onError: ex => _statusBarService.ShowError($"Could not assign the schedule. {ex.Message}", "Bulk assign failed"));

        // Same race SetScheduleForSelectionAsync's own guard comment above describes in
        // full (including why AttendanceBusyState.RunAsync's own hardening, not this
        // guard, is what actually stops it from crashing), applied here to the employee
        // the calendar is about to redisplay rather than to one of the employees this
        // write actually targeted: if the person changes Tree.SelectedEmployee while the
        // bulk write above is still running, Calendar's own deferred refresh (via
        // _scheduleRefreshPending) already claims the gate for the new selection the
        // instant this write's _busy.RunAsync finishes -- before this method's own
        // continuation gets a turn -- so this call below would just queue harmlessly
        // behind it rather than doing anything useful. Skipping it when the selection
        // moved on leaves that already-deferred refresh as the only one that runs, for
        // whichever employee is actually current by the time it does.
        if (_tree.SelectedEmployee?.Id == selectedEmployeeIdBeforeWrite)
            await _calendar.RefreshScheduleForSelectedEmployeeAsync(visibly: true);
    }

    /// <summary>
    /// Sets every highlighted day straight to Leave -- no dialog. Leave is the one
    /// ScheduleType with nothing else to collect (no hours, no time-in, no segments; see
    /// ApplyScheduleDialog.OkButton_Click, which skips all of that for Leave), so unlike the
    /// other three entries in the calendar's right-click "Set Schedule As" submenu, there's
    /// nothing a dialog would actually be for here -- picking Leave just applies it
    /// immediately. The plain Set/Edit Schedule flow (and manually choosing Leave from its
    /// dropdown) still goes through ApplyScheduleDialog as before; this is only reached from
    /// that one submenu item. Same multi-select branching as SetScheduleForSelectionAsync.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSetScheduleForSelection))]
    private async Task SetLeaveForSelectionAsync()
    {
        if (_multiSelectMode.IsMultiSelectMode)
        {
            await SetLeaveForCheckedEmployeesAsync();
            return;
        }

        if (_tree.SelectedEmployee is null)
        {
            _statusBarService.ShowCaution("Select an employee first.", "No employee selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.",
                "No days selected");
            return;
        }

        // Captured now (see SetScheduleForSelectionAsync's own local for the same reason)
        // rather than read as Tree.SelectedEmployee.Id/Pin inside the lambda below.
        var employeeId = _tree.SelectedEmployee.Id;
        var employeePin = _tree.SelectedEmployee.Pin;

        // See CanSetScheduleForSelection's own doc comment for why this is wrapped in
        // _busy.RunAsync, and SetScheduleForSelectionAsync's own doc comment above for why
        // the reload -- Calendar.RefreshScheduleForSelectedEmployeeAsync -- happens after
        // this returns rather than nested inside it.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _repository.SetScheduleForDatesAsync(
                employeePin, selectedDates, ScheduleType.Leave, workTimeHours: null, timeIn: null,
                cancellationToken: cancellationToken);
            _dataVersion.BumpScheduleForEmployees([employeePin]);

            _statusBarService.ShowSuccess($"Set to Leave for {selectedDates.Count} day(s).");
        },
        onError: ex => _statusBarService.ShowError($"Could not set Leave. {ex.Message}", "Save failed"));

        // See SetScheduleForSelectionAsync's own guard comment above for the race this
        // closes -- same shape here, against employeeId.
        if (_tree.SelectedEmployee?.Id == employeeId)
            await _calendar.RefreshScheduleForSelectedEmployeeAsync(visibly: true);
    }

    /// <summary>Same idea as SetLeaveForSelectionAsync above, but for every checked employee
    /// -- the bulk-assign equivalent of AssignScheduleToCheckedEmployeesAsync, minus the
    /// dialog for the same reason.</summary>
    private async Task SetLeaveForCheckedEmployeesAsync()
    {
        var employees = _tree.GetCheckedEmployees();
        if (employees.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Check one or more employees in the tree first (each has a checkbox).",
                "No employees selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.",
                "No days selected");
            return;
        }

        // Captured now -- see AssignScheduleToCheckedEmployeesAsync's own local of the same
        // shape for why (this write targets the checked employees, not Tree.SelectedEmployee,
        // but the follow-up refresh below redisplays whichever employee is selected once
        // the write finishes).
        var selectedEmployeeIdBeforeWrite = _tree.SelectedEmployee?.Id;

        // See CanSetScheduleForSelection's own doc comment for why this whole per-employee
        // write loop is wrapped in one _busy.RunAsync call, and
        // AssignScheduleToCheckedEmployeesAsync's own doc comment above for why the reload
        // -- Calendar.RefreshScheduleForSelectedEmployeeAsync -- happens after this returns
        // rather than nested inside it, and why _multiSelectMode.IsMultiSelectMode = false
        // still stays inside.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // Sequential, not parallel -- same DbContext-concurrency reason as
            // AssignScheduleToCheckedEmployeesAsync above.
            foreach (var employee in employees)
            {
                await _repository.SetScheduleForDatesAsync(
                    employee.Pin, selectedDates, ScheduleType.Leave, workTimeHours: null, timeIn: null,
                    cancellationToken: cancellationToken);
            }
            _dataVersion.BumpScheduleForEmployees(employees.Select(e => e.Pin).ToList());

            _multiSelectMode.IsMultiSelectMode = false;

            _statusBarService.ShowSuccess(
                $"Set to Leave for {employees.Count} employee(s) across {selectedDates.Count} day(s).",
                "Bulk assign complete");
        },
        onError: ex => _statusBarService.ShowError($"Could not set Leave. {ex.Message}", "Bulk assign failed"));

        // See AssignScheduleToCheckedEmployeesAsync's own guard comment above for the race
        // this closes -- same shape here, against selectedEmployeeIdBeforeWrite.
        if (_tree.SelectedEmployee?.Id == selectedEmployeeIdBeforeWrite)
            await _calendar.RefreshScheduleForSelectedEmployeeAsync(visibly: true);
    }

    /// <summary>Also requires !_busy.IsRunning -- same "disable the click rather than defer
    /// it" reasoning as CanSetScheduleForSelection's own doc comment (ClearScheduleForSelectionAsync
    /// writes through the same shared ScheduleDbContext). No bulk-checked-employee equivalent
    /// exists for Clear Schedule -- !MultiSelectModeState.IsMultiSelectMode above already keeps
    /// this command unavailable whenever there'd be checked employees to bulk-clear for.
    ///
    /// Unlike CanSetScheduleForSelection, this doesn't also check Tree.SelectedEmployeeCount
    /// -- there's no bulk branch here for a count to gate, so this class's own
    /// Tree.PropertyChanged(SelectedEmployeeCount) subscription only re-queries
    /// SetScheduleForSelectionCommand/SetLeaveForSelectionCommand, not this one.</summary>
    private bool CanClearScheduleForSelection() => !_multiSelectMode.IsMultiSelectMode && !_busy.IsRunning;

    /// <summary>Removes the schedule entirely (back to "nothing set") for every highlighted day.</summary>
    [RelayCommand(CanExecute = nameof(CanClearScheduleForSelection))]
    private async Task ClearScheduleForSelectionAsync()
    {
        if (_tree.SelectedEmployee is null)
        {
            _statusBarService.ShowCaution("Select an employee first.", "No employee selected");
            return;
        }

        var selectedDates = _calendar.GetSelectedDates();
        if (selectedDates.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.",
                "No days selected");
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove the schedule for {selectedDates.Count} selected day(s)? This cannot be undone.",
            "Confirm remove", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        // Captured now (see SetScheduleForSelectionAsync's own local for the same reason)
        // rather than read as Tree.SelectedEmployee.Id/Pin inside the lambda below.
        var employeeId = _tree.SelectedEmployee.Id;
        var employeePin = _tree.SelectedEmployee.Pin;

        // See CanSetScheduleForSelection's own doc comment (CanClearScheduleForSelection
        // shares its reasoning) for why this is wrapped in _busy.RunAsync, and
        // SetScheduleForSelectionAsync's own doc comment above for why the reload --
        // Calendar.RefreshScheduleForSelectedEmployeeAsync -- happens after this returns
        // rather than nested inside it.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _repository.ClearScheduleForDatesAsync(employeePin, selectedDates, cancellationToken);
            _dataVersion.BumpScheduleForEmployees([employeePin]);
            _statusBarService.ShowSuccess($"Schedule removed for {selectedDates.Count} day(s).");
        },
        onError: ex => _statusBarService.ShowError($"Could not remove the schedule. {ex.Message}", "Remove failed"));

        // See SetScheduleForSelectionAsync's own guard comment above for the race this
        // closes -- same shape here, against employeeId.
        if (_tree.SelectedEmployee?.Id == employeeId)
            await _calendar.RefreshScheduleForSelectedEmployeeAsync(visibly: true);
    }

    /// <summary>Gate for Mark/Remove Holiday -- !_busy.IsRunning only (a holiday write goes
    /// through the same scoped ScheduleDbContext as everything else on this page), with no
    /// employee-selection or multi-select-count check: holidays are company-wide (see
    /// Holiday's own doc comment), so this is available whenever the calendar has days
    /// highlighted, regardless of whether an employee is selected or which mode the tree is
    /// in. Notified from the _busy.PropertyChanged handler in this class's constructor, same
    /// as every other command here.</summary>
    private bool CanToggleHolidayForSelection() => !_busy.IsRunning;

    /// <summary>
    /// The calendar-side counterpart to ManageHolidaysDialog's Add/Delete: marks the one
    /// highlighted day as a holiday (prompting for its name), or removes the holiday from
    /// every highlighted day that has one. Company-wide, so it deliberately ignores
    /// Tree.SelectedEmployee entirely; the per-employee schedule calendar is just a
    /// convenient surface to reach it from.
    ///
    /// Marking is deliberately single-day only (the context menu -- see
    /// MonthCalendarControl.BuildDayContextMenu -- only offers "Mark as Holiday…" when
    /// exactly one non-holiday day is selected), the same one-at-a-time shape ManageHolidaysDialog's
    /// own Add has: a holiday needs its own name, and a shared name across a multi-day range
    /// is rarely what's actually wanted. Removal isn't restricted that way -- no name is
    /// involved -- so "Remove Holiday(s)" acts on every selected day that currently has one
    /// (a mixed selection's non-holiday days are left alone).
    ///
    /// Bumps AttendanceDataVersion.HolidayVersion after a successful write, the same bump
    /// ManageHolidaysDialog makes, so an already-open Payroll tab recomputes Holiday Pay on
    /// its next revisit -- see that counter's own doc comment.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleHolidayForSelection))]
    private async Task ToggleHolidayForSelectionAsync()
    {
        var selectedDays = _calendar.CalendarDays.Where(d => d.IsSelected).ToList();
        if (selectedDays.Count == 0)
        {
            _statusBarService.ShowCaution(
                "Click a day, Shift+click or drag for a range, or Ctrl+click to pick several days -- then try again.",
                "No days selected");
            return;
        }

        // Captured before the write so the highlight can be put back after
        // RefreshScheduleForSelectedEmployeeAsync rebuilds the grid below -- unlike the
        // Set/Clear/Leave Schedule commands, which let the rebuild drop the selection. A
        // holiday toggle is a quick "flip this day" that's natural to follow with another
        // (mark, then mark the next), and seeing the "H" appear on the day you still have
        // selected is useful confirmation.
        var selectedDates = selectedDays.Select(d => d.Date).ToHashSet();

        var toRemove = selectedDays.Where(d => d.IsHoliday).Select(d => d.Date).ToList();
        var changed = false;

        if (toRemove.Count > 0)
        {
            var confirm = MessageBox.Show(
                $"Remove {toRemove.Count} holiday(s) from the company-wide list? This affects payroll for every employee.",
                "Confirm remove", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            await _busy.RunAsync(visibly: true, async cancellationToken =>
            {
                // Resolve date -> Id at write time (Holiday.Date is unique) rather than
                // threading Ids through CalendarDayViewModel -- the table is tiny (see
                // IHolidayRepository.ListAsync) so one extra read here beats the plumbing.
                var idsByDate = (await _holidayRepository.ListAsync(cancellationToken))
                    .ToDictionary(h => h.Date, h => h.Id);
                foreach (var date in toRemove)
                {
                    if (idsByDate.TryGetValue(date, out var id))
                        await _holidayRepository.DeleteAsync(id, cancellationToken);
                }
                _dataVersion.BumpHoliday();
                changed = true;
                _statusBarService.ShowSuccess($"Removed {toRemove.Count} holiday(s).");
            },
            onError: ex => _statusBarService.ShowError($"Could not remove the holiday. {ex.Message}", "Remove failed"));
        }
        else
        {
            // Reached only via the single-day "Mark as Holiday…" item; guard anyway.
            if (selectedDates.Count != 1)
            {
                _statusBarService.ShowCaution("Select a single day to mark as a holiday.", "One day at a time");
                return;
            }

            var date = selectedDates.First();
            var dialog = new InputDialog("Mark as Holiday", "Holiday name (applies company-wide):", "Holiday")
            { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true) return;
            var name = dialog.Value.Trim();

            await _busy.RunAsync(visibly: true, async cancellationToken =>
            {
                try
                {
                    await _holidayRepository.AddAsync(new Holiday { Date = date, Name = name }, cancellationToken);
                    _dataVersion.BumpHoliday();
                    changed = true;
                    _statusBarService.ShowSuccess($"Marked {date:MMMM d, yyyy} as a holiday.");
                }
                catch (DuplicateHolidayDateException)
                {
                    // Raced another writer (ManageHolidaysDialog open elsewhere, or a second
                    // toggle) -- the date is a holiday now either way, which is the outcome
                    // asked for, so treat it as done.
                    changed = true;
                }
            },
            onError: ex => _statusBarService.ShowError($"Could not mark the holiday. {ex.Message}", "Save failed"));
        }

        if (!changed) return;

        // Reload just the holiday set + rebuild the grid so the "H" markers update (no need
        // to re-fetch the selected employee's schedule or recompute attendance markers -- a
        // holiday toggle changes neither), then put the day selection back (see
        // selectedDates above).
        await _calendar.RefreshHolidaysAsync();
        _calendar.SelectDates(selectedDates);
    }

    /// <summary>Also requires !_busy.IsRunning -- same "disable the click rather than defer
    /// it" reasoning as CanSetScheduleForSelection's own doc comment (this command opens a
    /// dialog and then writes, same as Set/Clear Schedule do), just without that one's
    /// multi-select-count check: the calendar's context menu only ever offers this item for a
    /// single right-clicked tile in the first place (see MonthCalendarControl.
    /// BuildDayContextMenu), so there's no bulk case to gate on here.
    ///
    /// Also requires !_manualEntryEditor.IsAttendanceBusy -- see that property's own doc
    /// comment and AddManualEntryForDayAsync's own doc comment below for the race this closes:
    /// without it, this menu item stayed clickable (and its reentrant call rode along,
    /// unserialized, on whatever else already had AttendanceViewModel's own busy state
    /// running) any time the Attendance page's own Import/Fetch/Generate Reports/Manual Entry
    /// edit was already in flight when the person right-clicked a calendar tile.</summary>
    private bool CanAddManualEntryForDay(CalendarDayViewModel day) => !_busy.IsRunning && !_manualEntryEditor.IsAttendanceBusy;

    /// <summary>Bound to the calendar's right-click "Add Manual Entry…" item (see
    /// MonthCalendarControl.BuildDayContextMenu, which only ever offers it for a single
    /// Partial/Absent tile). Delegates the actual dialog + save entirely to
    /// ManualEntryEditorViewModel.AddManualEntryForDayAsync -- the exact same
    /// Add/BumpManualLogs/status-message/Manual-Entries-grid-refresh path the Attendance tab's
    /// own Add Manual Entry button already uses -- then re-runs
    /// Calendar.RefreshCalendarAttendanceStatusesAsync() so the tile's own completion marker
    /// updates immediately rather than waiting for the next unrelated trigger to recompute it
    /// -- see that method's own doc comment on ScheduleCalendarViewModel for the gap phase 4
    /// found and fixed to make this call possible. Runs unconditionally after the call
    /// returns, even if the dialog was cancelled -- unlike Set/Clear Schedule's own early
    /// `if (dialog.ShowDialog() != true) return;`, there's no cheap signal available here to
    /// skip it on cancel (AddManualEntryForDayAsync's return type doesn't say whether
    /// anything was actually saved), and a redundant recompute is harmless: this is the same
    /// best-effort background decoration Calendar.RebuildCalendar's own unconditional call
    /// already is, not a mutating write.
    ///
    /// Wrapped in this class's own _busy.RunAsync (visibly: true, same as
    /// SetScheduleForSelectionAsync/ClearScheduleForSelectionAsync -- see
    /// CanSetScheduleForSelection's own doc comment for why an explicit-click command wraps
    /// rather than defers) -- see _manualEntryEditor's own doc comment for why this needs to
    /// hold *this* class's busy state too, not just ManualEntryEditorViewModel's own: without
    /// it, this class's other schedule-writing commands (and PayrollViewModel's own refresh,
    /// which shares this exact _busy instance) would stay free to run -- and race the manual
    /// entry's own AddAsync call against the shared, app-lifetime-scoped ScheduleDbContext --
    /// for the entire time the dialog is open and saving.
    ///
    /// CanAddManualEntryForDay's own !_manualEntryEditor.IsAttendanceBusy check is what closes
    /// the other direction of that same race: this method's body still only ever touches
    /// *this* class's own _busy (via the RunAsync wrapper here) and ManualEntryEditorViewModel.
    /// AddManualEntryForDayAsync's own separate AttendanceBusyState.RunAsync internally -- it
    /// does not, and should not, wait on or set AttendanceViewModel's busy state itself, since
    /// that would mean either merging the two AttendanceBusyState instances or reaching across
    /// pages to hold one open from here, both bigger changes than this fix makes. Disabling
    /// the click instead -- the same choice CanSetScheduleForSelection already makes for its
    /// own dialog -- means the Attendance page's own Import/Fetch/Generate Reports/Manual
    /// Entry edit being in flight when the person right-clicks a calendar tile now just leaves
    /// this item greyed out (and re-enables itself the moment that operation finishes, via
    /// ManualEntryEditorViewModel.IsAttendanceBusy's PropertyChanged) rather than letting the
    /// click through to race it.</summary>
    [RelayCommand(CanExecute = nameof(CanAddManualEntryForDay))]
    private async Task AddManualEntryForDayAsync(CalendarDayViewModel day)
    {
        // Shouldn't happen -- the menu item this is bound to only ever shows up for a tile
        // whose AttendanceStatus was actually computed, which itself requires a non-null
        // Tree.SelectedEmployee -- but checked here too rather than trusting the
        // CommandParameter binding alone, same as EditManualEntryAsync/DeleteManualEntryAsync
        // re-check row.IsManual.
        if (_tree.SelectedEmployee is null) return;

        // Captured now -- same reasoning as SetScheduleForSelectionAsync's own local of the
        // same shape -- so the follow-up call below can tell whether the selection this
        // entry was added for is still current.
        var employeeId = _tree.SelectedEmployee.Id;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _manualEntryEditor.AddManualEntryForDayAsync(_tree.SelectedEmployee, day.Date);
        });

        // Unlike the five Set/Clear/Leave Schedule call sites above, this particular call
        // was never independently crash-vulnerable to the race their own guard comments
        // describe: RefreshCalendarAttendanceStatusesAsync (unlike
        // RefreshScheduleForSelectedEmployeeAsync) already checks `if (_busy.IsRunning)`
        // itself and defers via _calendarStatusRefreshPending BEFORE ever calling
        // _busy.RunAsync -- and that check-then-call has no await between them, so on this
        // single-threaded UI dispatcher nothing can flip IsRunning in between. If a
        // deferred schedule refresh (from a mid-write selection change) is still holding
        // IsRunning true when this call's own continuation runs, this method correctly
        // defers instead of racing it; if that refresh has already finished, this becomes
        // a new, non-overlapping outermost call instead. The guard below is added anyway,
        // for the same reason as the other five: if the selection moved on, RebuildCalendar
        // already recomputed markers for whoever's now current (see
        // RefreshScheduleForSelectedEmployeeAsync), making a second recompute here -- for
        // an employee no longer even displayed -- redundant work rather than a correctness
        // fix.
        if (_tree.SelectedEmployee?.Id == employeeId)
            await _calendar.RefreshCalendarAttendanceStatusesAsync();
    }

    /// <summary>Same two-part gate, for the same two reasons, as
    /// CanAddManualEntryForDay just above -- this command likewise opens a dialog and then
    /// writes through the shared, app-lifetime-scoped ScheduleDbContext, and the launcher
    /// it delegates to reads that context before the dialog even opens.</summary>
    private bool CanEditPunchPairingForDay(CalendarDayViewModel day) =>
        !_busy.IsRunning && !_manualEntryEditor.IsAttendanceBusy;

    /// <summary>Bound to the calendar's right-click "Edit Punch Pairing…" item (see
    /// MonthCalendarControl.BuildDayContextMenu, which offers it for a single tile that
    /// is either Flexible or came out Partial/Absent on another type -- the launcher
    /// persists a pairing only for the Flexible case, see DayPunchPairingEditorLauncher).
    /// Delegates the whole load/show/save to the shared
    /// DayPunchPairingEditorLauncher -- the exact same path the Attendance Summary grid's
    /// identical item uses -- then re-runs Calendar.RefreshCalendarAttendanceStatusesAsync()
    /// so the tile's own completion marker updates immediately, exactly as
    /// AddManualEntryForDayAsync above does.
    ///
    /// Unlike that method, the refresh is skipped when nothing was written: OpenAsync
    /// returns whether it actually saved or reset, so a cancelled dialog (or a day the
    /// launcher declined to open at all) doesn't pay for a recompute. The Summary grid
    /// needs no equivalent call -- it picks the change up from
    /// AttendanceDataVersion.PairingVersion, which the launcher bumps; the calendar's
    /// markers aren't driven by that counter, which is why this call is here.
    ///
    /// Wrapped in this class's own _busy.RunAsync (visibly: true) for the same
    /// "hold both busy states" reasoning AddManualEntryForDayAsync's doc comment sets out
    /// -- with one difference: the launcher has no AttendanceBusyState of its own to
    /// serialize against (it isn't a ViewModel), so this wrapper is the only thing
    /// guarding its reads and writes on this side.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPunchPairingForDay))]
    private async Task EditPunchPairingForDayAsync(CalendarDayViewModel day)
    {
        // Shouldn't happen -- BuildDayContextMenu only offers this item when an employee
        // is selected -- but checked here too rather than trusting the CommandParameter
        // binding alone, same as AddManualEntryForDayAsync above.
        if (_tree.SelectedEmployee is null) return;

        var employee = _tree.SelectedEmployee;
        var employeeId = employee.Id;

        bool saved = false;
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            saved = await _pairingLauncher.OpenAsync(employee, day.Date, cancellationToken);
        });

        // Same "did the selection move on while the dialog was open" guard as
        // AddManualEntryForDayAsync -- see its own comment for why a recompute for an
        // employee no longer displayed is redundant rather than a correctness fix.
        if (saved && _tree.SelectedEmployee?.Id == employeeId)
            await _calendar.RefreshCalendarAttendanceStatusesAsync();
    }
}
