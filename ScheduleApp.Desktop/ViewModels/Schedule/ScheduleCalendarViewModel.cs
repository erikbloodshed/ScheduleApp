using System.Collections.ObjectModel;
using System.Globalization;
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

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// Backs the Schedule tab's calendar grid (see MainViewModel-Split-Plan.md's component
/// list) -- month navigation, the 42-cell day grid itself, the schedule read behind it,
/// day-selection state, and the background attendance-completion markers decorating each
/// cell. Takes EmployeeTreeViewModel (phase 2) as a constructor dependency specifically so
/// it can subscribe to that class's own PropertyChanged on SelectedEmployee itself, rather
/// than EmployeeTreeViewModel reaching forward to a sibling it can't reference -- see that
/// class's OnSelectedEmployeeChanged doc comment, which already flagged this as this
/// class's job to pick up once it existed. Same "one child takes another child as a
/// constructor argument" shape ReportViewModel already establishes for ReportScopeViewModel
/// (see the split plan's "Two more precedents" section) -- not a new risk this plan is
/// introducing.
///
/// Also takes MultiSelectModeState (phase 1) as a constructor dependency, for the same
/// "leave/multi-select mode blanks the calendar" reasoning CalendarHeaderText's own doc
/// comment (still on MainViewModel today) describes -- this class only ever *reads* it
/// (RefreshScheduleForSelectedEmployeeAsync's multi-select branch,
/// RefreshCalendarAttendanceStatusesAsync's early-return); ScheduleAssignmentViewModel
/// (phase 4) is the sibling that also *writes* it back to false after a successful bulk
/// operation -- see MultiSelectModeState's own doc comment for the full "who reads, who
/// writes" breakdown.
///
/// Extracted per the split plan's phase 3 ("takes EmployeeTreeViewModel ... and
/// MultiSelectModeState as constructor dependencies") -- nothing outside this file
/// references ScheduleCalendarViewModel yet, and MainViewModel itself is untouched: it
/// still owns its own copy of every member here (including its own separate
/// EmployeeTreeViewModel-shaped tree state, per phase 2's own note). Phase 6 rebuilds
/// MainViewModel as the facade that constructs this class (after Tree and
/// MultiSelectModeState) and forwards its members flatly, the same way AttendanceViewModel
/// forwards ReportViewModel's.
///
/// Several members below are public even though nothing outside this file calls them
/// yet -- RefreshScheduleForSelectedEmployeeAsync, RequestScheduleRefresh, GetSelectedDates,
/// AnalyzeSelectionSchedule, and GroupIntoContiguousRanges -- same "phase N's own table
/// already needs it, so make it accessible now rather than re-visiting this file later"
/// reasoning EmployeeTreeViewModel.GetCheckedEmployees' own doc comment used for the same
/// situation in phase 2: the split plan's component list has ScheduleAssignmentViewModel
/// (phase 4) calling all five once it exists, and the facade (phase 6) calling
/// RequestScheduleRefresh explicitly from ToggleMultiSelectMode. RefreshCalendarAttendanceStatusesAsync
/// itself joined this list a sixth member late, once phase 4 actually landed and needed it
/// too -- see that method's own doc comment for the gap this closes.
///
/// Also takes AttendanceDataVersion as a constructor dependency now -- the same shared,
/// scoped instance MainViewModel/PayrollViewModel/AttendanceViewModel already all receive
/// (see App.xaml.cs's registration) -- added once this class's own repeated,
/// unconditional-blank-then-refetch shape in RefreshCalendarAttendanceStatusesAsync turned
/// out to be the actual root cause behind "switching between employees loses some days'
/// completion markers": every single call blanked every marker before ever checking
/// whether _busy was already in use, so a quick switch back to a recently-viewed employee
/// while something else (typically PayrollViewModel's own refresh) still held the shared
/// ScheduleDbContext left the calendar blank until a deferred re-run eventually got its
/// turn -- and a further switch before that turn came just superseded the deferred flag
/// with another blank. _attendanceStatusCache (see that field's own doc comment) fixes
/// this by keying already-computed markers on (Employee.Id, DisplayedMonth) and serving a
/// still-valid entry synchronously, with no blank-then-refetch and no dependency on
/// _busy's state at all -- valid meaning none of AttendanceDataVersion's three counters
/// have moved since that entry was computed, which is what tells a schedule edit, a
/// device import/fetch, or a manual-entry change to correctly invalidate it again.
/// </summary>
public partial class ScheduleCalendarViewModel : ObservableObject
{
    private readonly IScheduleRepository _repository;

    /// <summary>Reads the company-wide Holidays table (see Holiday/IHolidayRepository) so
    /// each calendar cell can show whether its date is a holiday, and
    /// ScheduleAssignmentViewModel's Mark/Remove Holiday command can write to it. The
    /// whole table is pulled at once (ListAsync, not the period-scoped
    /// ListDatesForPeriodAsync) and kept in _holidaysByDate -- a company files a handful
    /// of holidays a year (see IHolidayRepository.ListAsync's own "never grows large
    /// enough to need paging" note), so re-fetching all of them alongside the schedule is
    /// cheaper than tracking which months have been loaded, and month navigation
    /// (OnDisplayedMonthChanged -> RebuildCalendar, with no schedule re-fetch) then has
    /// every month's holidays already on hand.</summary>
    private readonly IHolidayRepository _holidayRepository;

    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceSettings _attendanceSettings;
    private readonly IAttendanceRunner _attendanceRunner;

    /// <summary>Every holiday currently on file, keyed by date, name as the value -- the
    /// source RebuildCalendar stamps each CalendarDayViewModel.IsHoliday/HolidayName
    /// from. Refreshed inside RefreshScheduleForSelectedEmployeeAsync's own _busy.RunAsync
    /// block (so an employee switch, a schedule write's follow-up refresh, and the Mark/
    /// Remove Holiday command's follow-up refresh all pick up holiday changes for free),
    /// and empty until that first runs -- the calendar is blank before an employee is
    /// selected anyway, so there's nothing for a holiday marker to decorate yet.</summary>
    private Dictionary<DateOnly, string> _holidaysByDate = new();

    /// <summary>The *same* instance MainViewModel receives today (see App.xaml.cs's
    /// existing registration and MainViewModel._busy's own doc comment for why -- shared
    /// with PayrollViewModel too, since both react to the same SelectedEmployee change
    /// against the same shared, app-lifetime-scoped ScheduleDbContext). Not a new
    /// registration of its own: phase 6's facade passes this class the exact same _busy
    /// field it already holds.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Read once, in this class's own constructor, for the initial DisplayedMonth
    /// -- same direct read MainViewModel.ctor does today (`_viewStateStore.Schedule.
    /// DisplayedMonth ?? ...`), not routed through a restore-style method the way
    /// EmployeeTreeViewModel.RestoreSelectionFromViewState is, since there's only the one
    /// value to restore here rather than an employee-or-department choice to resolve
    /// against a freshly-built tree.</summary>
    private readonly ViewStateStore _viewStateStore;

    /// <summary>MainViewModel's own SaveViewState, passed in as a delegate -- same shape as
    /// EmployeeTreeViewModel._saveViewState (see that field's own doc comment for the
    /// precedent, which itself points back to ManualEntriesViewModel/PunchRecordsViewModel).
    /// Called from OnDisplayedMonthChanged, same call site MainViewModel.
    /// OnDisplayedMonthChanged already has today; still a no-op until Tree.HasRestoredSelection
    /// flips true, so this class's own constructor-time DisplayedMonth assignment below
    /// can't clobber a saved view state either.</summary>
    private readonly Action _saveViewState;

    /// <summary>For SelectedEmployee only -- see this class's own summary above for why
    /// this is a constructor dependency rather than this class reaching back out to a
    /// shared facade. Subscribed to in this class's own constructor (SelectedEmployee only
    /// -- this class has no reason to react to Tree.SelectedDepartment, SearchText, or any
    /// of Tree's own tree-editing commands).</summary>
    private readonly EmployeeTreeViewModel _tree;

    /// <summary>See this class's own summary above for the read-only role this class plays
    /// against it (ScheduleAssignmentViewModel, phase 4, is the sibling that also writes
    /// it).</summary>
    private readonly MultiSelectModeState _multiSelectMode;

    /// <summary>The *same* instance every other database-touching ViewModel on this page
    /// (and on the Attendance page) shares -- see App.xaml.cs's registration comment and
    /// ScheduleAssignmentViewModel's own _dataVersion field, which is what actually calls
    /// BumpScheduleForEmployees(...) after a Set/Clear/Leave Schedule write, an Import
    /// Schedule, or an employee delete. This class only ever reads it, as the staleness half of
    /// _attendanceStatusCache below -- see that field's own doc comment for why a write
    /// here (as opposed to the ScheduleEntries/CalendarDays it's read against) needs all
    /// three counters, not just ScheduleVersion.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>Guards RefreshCalendarAttendanceStatusesAsync's own database round trip --
    /// cancelled and replaced on every cache-miss call so a slow run for a
    /// since-abandoned employee/month can't land after a newer one already has. Not
    /// touched at all on a cache hit (see RefreshCalendarAttendanceStatusesAsync's own
    /// doc comment) -- a still in-flight run from a moment ago is harmless to leave
    /// running, since the identity/month guard on its own eventual completion stops it
    /// from overwriting whatever's on screen by then, and letting it finish naturally
    /// still warms _attendanceStatusCache for whichever employee/month it was for.</summary>
    private CancellationTokenSource? _attendanceStatusCts;

    /// <summary>Already-computed completion markers for a given employee/month, so
    /// switching back to someone recently viewed (the common case -- set a schedule, flip
    /// to the next employee, then back) doesn't have to wait on the shared, app-lifetime-
    /// scoped ScheduleDbContext at all, and doesn't have to sit through the
    /// blank-then-refill flicker that DB round trip causes. Keyed on (Employee.Id,
    /// DisplayedMonth) -- DisplayedMonth is always the 1st of the month (see
    /// OnDisplayedMonthChanged/PreviousMonth/NextMonth), so it's a stable, directly
    /// comparable dictionary key rather than needing a separate Year/Month tuple.
    ///
    /// Each entry's own CachedAttendanceStatus additionally snapshots all three
    /// AttendanceDataVersion counters at the moment it was computed (see that record's own
    /// doc comment) -- an entry is only actually served on a hit when every counter still
    /// matches _dataVersion's current values, which is what lets a schedule edit
    /// (ScheduleVersion), a device import/fetch (DeviceLogsVersion), or a manual entry
    /// add/edit/delete (ManualLogsVersion) correctly invalidate every cached
    /// employee/month at once rather than only the one just edited -- any of the three can
    /// change what AttendanceWorkflowService computes for days that weren't the one just
    /// touched (e.g. a manual entry backfilling an earlier Absent day changes that day's
    /// own marker for every employee whose cache entry predates it). Entries for a
    /// since-invalidated key are simply left in place and overwritten the next time that
    /// key is actually requested again, rather than proactively swept on a bump -- there's
    /// no unbounded growth risk to guard against (the key space is bounded by however many
    /// distinct employee/month pairs get viewed in one running session, the same bound
    /// ScheduleEntries/CalendarDays are already implicitly under), so there's nothing a
    /// sweep would buy over just letting TryGetValue's own version check reject a stale
    /// entry on next use.</summary>
    private readonly Dictionary<(int EmployeeId, DateTime Month), CachedAttendanceStatus> _attendanceStatusCache = new();

    /// <summary>Set by RefreshCalendarAttendanceStatusesAsync when an employee/month change
    /// arrives while _busy.IsRunning is already true, and cleared by the _busy.PropertyChanged
    /// handler in this class's own constructor once it uses this to decide whether to re-run
    /// RefreshCalendarAttendanceStatusesAsync after IsRunning drops back to false -- same
    /// "queue instead of race" pattern as PayrollViewModel's own _refreshPending. Without
    /// this, the calendar's completion markers were computed via _attendanceRunner completely
    /// outside the _busy window, so they could -- and did -- start a second, concurrent
    /// operation against the shared, app-lifetime-scoped ScheduleDbContext while
    /// PayrollViewModel's own RequestRefresh/RefreshAsync was already using it.</summary>
    private bool _calendarStatusRefreshPending;

    /// <summary>Set by RequestScheduleRefresh() when an employee-selection change (or a
    /// multi-select toggle) arrives while _busy.IsRunning is already true, and cleared by
    /// the _busy.PropertyChanged handler in this class's own constructor once it uses this
    /// to decide whether to re-run RefreshScheduleForSelectedEmployeeAsync after IsRunning
    /// drops back to false -- same "queue instead of race" pattern as this class's own
    /// _calendarStatusRefreshPending above. See RequestScheduleRefresh's own doc comment for
    /// the fuller story of the race this closes.</summary>
    private bool _scheduleRefreshPending;

    /// <summary>Whichever visibly value the most recent deferred RequestScheduleRefresh call
    /// asked for -- OR'd together if more than one arrives before IsRunning clears, so an
    /// explicit multi-select toggle (visibly: true) deferred behind a silent
    /// employee-selection change (visibly: false) still shows the progress bar once it
    /// actually runs.</summary>
    private bool _scheduleRefreshPendingVisibly;

    public ScheduleCalendarViewModel(
        IScheduleRepository repository,
        IHolidayRepository holidayRepository,
        IStatusBarService statusBarService,
        AttendanceSettings attendanceSettings,
        IAttendanceRunner attendanceRunner,
        AttendanceBusyState busy,
        ViewStateStore viewStateStore,
        Action saveViewState,
        EmployeeTreeViewModel tree,
        MultiSelectModeState multiSelectMode,
        AttendanceDataVersion dataVersion)
    {
        _repository = repository;
        _holidayRepository = holidayRepository;
        _statusBarService = statusBarService;
        _attendanceSettings = attendanceSettings;
        _attendanceRunner = attendanceRunner;
        _busy = busy;
        _viewStateStore = viewStateStore;
        _saveViewState = saveViewState;
        _tree = tree;
        _multiSelectMode = multiSelectMode;
        _dataVersion = dataVersion;

        // Picks up an employee/month change that arrived while
        // RefreshCalendarAttendanceStatusesAsync deferred instead of racing whatever else
        // was using _busy (a PayrollViewModel refresh, most commonly), and an
        // employee-selection or multi-select-toggle change that arrived while
        // RefreshScheduleForSelectedEmployeeAsync deferred the same way -- see
        // _calendarStatusRefreshPending/_scheduleRefreshPending's own doc comments. Mirrors
        // MainViewModel's own _busy.PropertyChanged(IsRunning) handler, minus the
        // NotifyCanExecuteChanged calls for SetScheduleForSelectionCommand/
        // SetLeaveForSelectionCommand/ClearScheduleForSelectionCommand/
        // AddManualEntryForDayCommand -- those are ScheduleAssignmentViewModel's own
        // commands (phase 4), which is expected to add its own _busy.PropertyChanged
        // subscription for them, the same "each class subscribes to what it needs" layering
        // this class's own subscription to Tree below follows.
        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsVisiblyRunning))
            {
                // See RefreshOrCancelGlyph/RefreshOrCancelToolTip's own doc comment --
                // this is what flips the calendar header's icon button between Refresh
                // and Cancel, same mechanism PayrollSummaryViewModel's own analogous
                // handler uses for the payslip header's button.
                OnPropertyChanged(nameof(RefreshOrCancelGlyph));
                OnPropertyChanged(nameof(RefreshOrCancelToolTip));
                RefreshOrCancelScheduleCommand.NotifyCanExecuteChanged();
                return;
            }

            if (e.PropertyName != nameof(AttendanceBusyState.IsRunning)) return;

            // Disable RecalculateScheduleCommand the instant _busy.IsRunning goes true,
            // re-enable it the instant it goes false -- same "PropertyChanged ->
            // NotifyCanExecuteChanged, immediately, every time" behavior every other
            // _busy-gated command elsewhere in this app follows (see e.g.
            // PayrollSummaryViewModel's own _busy.PropertyChanged handler).
            RecalculateScheduleCommand.NotifyCanExecuteChanged();
            RefreshOrCancelScheduleCommand.NotifyCanExecuteChanged();

            if (!_busy.IsRunning && _scheduleRefreshPending)
            {
                _scheduleRefreshPending = false;
                var visibly = _scheduleRefreshPendingVisibly;
                _scheduleRefreshPendingVisibly = false;
                _ = RefreshScheduleForSelectedEmployeeAsync(visibly);
            }

            if (!_busy.IsRunning && _calendarStatusRefreshPending)
            {
                _calendarStatusRefreshPending = false;
                _ = RefreshCalendarAttendanceStatusesAsync();
            }
        };

        // The other half of the "child reacts to the sibling it depends on" layering
        // EmployeeTreeViewModel.OnSelectedEmployeeChanged's own doc comment describes --
        // that method deliberately stopped calling RequestScheduleRefresh()/touching
        // CalendarHeaderText itself, leaving this class (and, for CalendarHeaderText, the
        // phase 6 facade) to pick each up via this subscription instead. Same "child
        // subscribes to the sibling it depends on" shape ReportViewModel's own
        // _reportScope.PropertyChanged subscription already establishes. Also notifies
        // RecalculateScheduleCommand -- its own CanExecute reads _tree.SelectedEmployee
        // directly (see CanRecalculateSchedule), the same dependency CalendarHeaderText's
        // own facade-level subscription reacts to.
        _tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EmployeeTreeViewModel.SelectedEmployee)) return;

            RequestScheduleRefresh();
            RecalculateScheduleCommand.NotifyCanExecuteChanged();
            RefreshOrCancelScheduleCommand.NotifyCanExecuteChanged();
        };

        // Goes through the generated property setter (not the backing field directly) so
        // OnDisplayedMonthChanged fires and the empty calendar grid is built immediately.
        // ViewStateStore is in-memory/session-only (see its own doc comment), so this falls
        // back to the current month on every launch now, not just the first-ever run --
        // identical to MainViewModel.ctor's own DisplayedMonth assignment today.
        DisplayedMonth = _viewStateStore.Schedule.DisplayedMonth
            ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    }

    public ObservableCollection<ScheduleEntry> ScheduleEntries { get; } = new();
    public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = new();

    [ObservableProperty]
    private DateTime displayedMonth;

    public string DisplayedMonthText => DisplayedMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"Edit..." once any day currently highlighted in the calendar already has a
    /// schedule (there's something to potentially overwrite), "Set..." when none do. Kept
    /// up to date reactively -- see the PropertyChanged subscription wired up in
    /// RebuildCalendar -- so it's right before the button is ever clicked, not just when
    /// ScheduleAssignmentViewModel.SetScheduleForSelectionAsync (phase 4) runs. In
    /// multi-select mode the calendar's ScheduleEntries are always empty (see
    /// RefreshScheduleForSelectedEmployeeAsync below), so this naturally stays "Set..."
    /// there too -- "what's already set" has no single answer across several employees, so
    /// there's nothing to flip it to "Edit..." for.</summary>
    [ObservableProperty]
    private string setScheduleButtonText = "Set Schedule for Selected Days";

    partial void OnDisplayedMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(DisplayedMonthText));
        // Fire-and-forget here, same as always -- OnDisplayedMonthChanged is a partial
        // property-changed hook and can't be async, so this can't await RebuildCalendar's
        // own returned Task the way RefreshScheduleForSelectedEmployeeAsync below does.
        _ = RebuildCalendar();
        _saveViewState();
    }

    [RelayCommand]
    private void PreviousMonth() => DisplayedMonth = DisplayedMonth.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => DisplayedMonth = DisplayedMonth.AddMonths(1);

    /// <summary>Gate-and-defer counterpart to RefreshScheduleForSelectedEmployeeAsync, for
    /// its two fire-and-forget callers: this class's own Tree.PropertyChanged(SelectedEmployee)
    /// subscription above, and (once phase 6 lands) the facade's ToggleMultiSelectMode,
    /// which is expected to call this explicitly after flipping MultiSelectModeState.
    /// IsMultiSelectMode -- see the split plan's phase 6 bullet ("keeps ToggleMultiSelectMode
    /// itself, now calling Calendar.RequestScheduleRefresh(visibly: true) explicitly since
    /// that stays Calendar-owned"). Same "check _busy.IsRunning before calling RunAsync,
    /// defer via a pending flag otherwise" shape RefreshCalendarAttendanceStatusesAsync
    /// below already follows; see _scheduleRefreshPending's own doc comment for the race
    /// this closes.
    ///
    /// NOT used by the five awaited call sites ScheduleAssignmentViewModel (phase 4) owns
    /// (SetScheduleForSelectionAsync, AssignScheduleToCheckedEmployeesAsync,
    /// SetLeaveForSelectionAsync, SetLeaveForCheckedEmployeesAsync,
    /// ClearScheduleForSelectionAsync) -- those close the same race a different way, by the
    /// time one of those would write, the person has already gone through a confirmation
    /// dialog and made a decision, so silently queuing that decision behind whatever's
    /// running elsewhere would be a worse experience than just not letting the click start
    /// in the first place. Each of those five calls RefreshScheduleForSelectedEmployeeAsync
    /// directly instead, immediately AFTER its own _busy.RunAsync call returns rather than
    /// nested inside it -- see that method's own doc comment below for why nesting it
    /// there defers RebuildCalendar's own attendance-marker recompute into a second,
    /// hidden busy cycle instead of running it in the same one.</summary>
    public void RequestScheduleRefresh(bool visibly = false)
    {
        if (_busy.IsRunning)
        {
            _scheduleRefreshPending = true;
            _scheduleRefreshPendingVisibly |= visibly;
            return;
        }

        _ = RefreshScheduleForSelectedEmployeeAsync(visibly);
    }

    /// <summary>visibly matches AttendanceBusyState.RunAsync's own parameter: true for an
    /// explicit action the person just took and is waiting on (multi-select toggle, or
    /// right after a schedule write), false for the auto-triggered case (selecting a
    /// different employee/department node). Called both via RequestScheduleRefresh's gate
    /// above and directly from ScheduleAssignmentViewModel's five schedule-write commands
    /// (phase 4), immediately after their own _busy.RunAsync call returns -- see
    /// RequestScheduleRefresh's own doc comment for why those five are gated differently.
    /// RecalculateScheduleAsync below is a sixth direct caller, added once that command
    /// existed -- see its own doc comment for why it also calls this directly rather than
    /// through RequestScheduleRefresh's gate.
    ///
    /// Deliberately called AFTER those five's own _busy.RunAsync, not nested inside it --
    /// nested there, this method's own internal _busy.RunAsync call below would just ride
    /// along on the outer call's Token (harmless on its own -- see AttendanceBusyState.
    /// RunAsync's own reentrancy branch), but this method also awaits RebuildCalendar's own
    /// returned Task (see that method's own doc comment), which in turn runs
    /// RefreshCalendarAttendanceStatusesAsync -- a second, genuinely separate
    /// _busy.RunAsync window, only reachable once this method's own first window has fully
    /// closed. That matters beyond just IsRunning bookkeeping: the five callers this method
    /// serves are exactly what a person can navigate away from Schedule right after (Save
    /// on the schedule dialog closes once the calling command returns) -- awaiting all the
    /// way through here, rather than leaving RefreshCalendarAttendanceStatusesAsync to fire
    /// fire-and-forget the way RebuildCalendar's own callers elsewhere still do, is what
    /// guarantees this method's own database work has genuinely finished before any of
    /// those five callers return control to whoever's waiting on them -- including
    /// PayrollGroupViewModel.RecheckOnPageRevisitAsync, if the person's very next action is
    /// switching to the Payroll tab (see that method's own doc comment for the shared,
    /// app-lifetime-scoped ScheduleDbContext neither side can safely use concurrently).
    /// Public for the same forward-looking reason as this class's other members -- nothing
    /// outside this file calls it yet, but the split plan's component list already has
    /// phase 4 calling it.
    ///
    /// Returns whether the schedule fetch itself succeeded -- added for
    /// RecalculateScheduleAsync below, which needs to know whether to show a success
    /// message: _busy.RunAsync itself swallows the exception and reports it via onError
    /// rather than rethrowing (see that method's own doc comment), so without this the
    /// caller has no way to tell a clean run apart from one onError already reported. Every
    /// other caller (the two fire-and-forget `_ = RefreshScheduleForSelectedEmployeeAsync(...)`
    /// sites in this file, and ScheduleAssignmentViewModel's five `await
    /// _calendar.RefreshScheduleForSelectedEmployeeAsync(...)` sites) already discards the
    /// task without reading a return value, so this is a non-breaking signature change --
    /// same "Task&lt;bool&gt; discard compiles the same as Task discard" reasoning
    /// PayrollSummaryViewModel.RefreshAsync relies on for its own Recalculate button.
    /// Deliberately does NOT also fold in whether RebuildCalendar's own
    /// RefreshCalendarAttendanceStatusesAsync succeeded -- that one is explicitly a
    /// best-effort decoration on top of the calendar (see its own doc comment: "a slow or
    /// failed lookup ... just leaves markers off rather than interrupting whoever's setting
    /// a schedule"), so a marker-recompute hiccup shouldn't turn what was otherwise a
    /// perfectly successful schedule reload into a reported failure.</summary>
    public async Task<bool> RefreshScheduleForSelectedEmployeeAsync(bool visibly = false)
    {
        ScheduleEntries.Clear();
        var succeeded = true;

        await _busy.RunAsync(visibly, async cancellationToken =>
        {
            // In multi-select mode there's no single "the" employee whose schedule makes
            // sense to show, even if one happens to be highlighted in the tree -- leave the
            // calendar empty rather than imply you're editing just them. Same for an
            // employee with no Pin set -- per SetScheduleForDatesAsync's own doc comment,
            // they can't have any ScheduleEntries to find in the first place, so there's
            // nothing to fetch; ScheduleEntries is already empty from the Clear() above.
            if (_tree.SelectedEmployee is { Pin: { } pin } && !_multiSelectMode.IsMultiSelectMode)
            {
                foreach (var entry in await _repository.GetScheduleEntriesForEmployeeAsync(pin, cancellationToken))
                    ScheduleEntries.Add(entry);
            }

            // Holidays are company-wide (see _holidaysByDate's own doc comment), so this
            // runs regardless of which employee -- if any -- is selected, folded into the
            // same round trip so an employee switch also picks up a holiday change made
            // elsewhere. RefreshHolidaysAsync below is the standalone path for when there's
            // no employee to trigger this at all (page open on a fresh run) or the only
            // thing that changed is a holiday.
            await LoadHolidaysByDateAsync(cancellationToken);
        },
        onError: ex =>
        {
            succeeded = false;
            _statusBarService.ShowError($"Could not load the schedule. {ex.Message}", "Schedule load failed");
        });

        // Runs whether the fetch above succeeded, failed, or was cancelled -- either way
        // ScheduleEntries is now in a definite state (whatever was added before a failure,
        // or nothing) and CalendarDays needs to reflect it rather than keep showing
        // whichever employee/month it had before this call. Awaited all the way through --
        // see this method's own doc comment for why that's the actual point.
        await RebuildCalendar();

        return succeeded;
    }

    /// <summary>Loads the company-wide holiday set into _holidaysByDate. Its own try/catch
    /// rather than surfacing a failure -- a holiday-list hiccup should just leave the "H"
    /// markers off (a decoration, like the attendance markers), not report an error. A
    /// cancel/shutdown still propagates to whichever _busy.RunAsync is wrapping the call.
    /// Shared by RefreshScheduleForSelectedEmployeeAsync (folded into the employee/schedule
    /// round trip) and RefreshHolidaysAsync (the standalone reload).</summary>
    private async Task LoadHolidaysByDateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var holidays = await _holidayRepository.ListAsync(cancellationToken);
            _holidaysByDate = holidays.ToDictionary(h => h.Date, h => h.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _holidaysByDate = new();
        }
    }

    /// <summary>Reloads the company-wide holiday set and rebuilds the grid so the "H"
    /// markers reflect it -- without re-fetching the selected employee's schedule or forcing
    /// an attendance-marker recompute the way RefreshScheduleForSelectedEmployeeAsync does,
    /// since a holiday change touches neither. Called on Schedule-page navigation (so
    /// holidays show even before an employee is picked -- the employee/schedule refresh that
    /// normally carries the holiday load only runs once one is selected) and after
    /// ScheduleAssignmentViewModel.ToggleHolidayForSelectionAsync's own write.</summary>
    public async Task RefreshHolidaysAsync()
    {
        await _busy.RunAsync(visibly: false, LoadHolidaysByDateAsync);
        await RebuildCalendar();
    }

    /// <summary>Manual counterpart to RequestScheduleRefresh() -- bound to SchedulePage's
    /// icon-only Refresh button at the right edge of the calendar header (mirroring
    /// PayrollSummaryView's own per-employee payslip Refresh button), for reloading the
    /// selected employee's schedule and recomputing the calendar's completion markers on
    /// demand (e.g. after attendance for the period changed on the device side, or the
    /// person just isn't sure the calendar reflects the latest data) without needing to
    /// switch away to a different employee and back to trigger a reload indirectly. Runs
    /// the exact same RefreshScheduleForSelectedEmployeeAsync(visibly: true) compute the
    /// five schedule-write commands (ScheduleAssignmentViewModel) already call after their
    /// own writes -- there's no cheaper "what actually changed" signal to react to here for
    /// a manual click, the same reasoning PayrollSummaryViewModel's own RecalculatePayslipAsync
    /// gives for the payslip equivalent this mirrors.
    ///
    /// Reports success explicitly, unlike the automatic paths (RequestScheduleRefresh and
    /// the five schedule-write commands, both silent on success so an ordinary employee
    /// selection or a routine schedule edit doesn't spam the status bar) -- same "silent for
    /// a background reaction, a status message for a direct click" split
    /// PayrollSummaryViewModel.RecalculatePayslipAsync/PayrollRunViewModel's own
    /// NewPayrollRun/LoadPayrollGroupAsync already follow. Calls
    /// RefreshScheduleForSelectedEmployeeAsync() directly rather than through
    /// RequestScheduleRefresh()'s own guard/defer wrapper -- CanRecalculateSchedule below
    /// already keeps this command disabled unless an employee is actually selected outside
    /// multi-select mode and while _busy.IsRunning, so there's nothing left here for that
    /// wrapper to guard against, and awaiting the real work directly is what lets this
    /// method see the returned success flag at all.</summary>
    [RelayCommand(CanExecute = nameof(CanRecalculateSchedule))]
    private async Task RecalculateScheduleAsync()
    {
        if (await RefreshScheduleForSelectedEmployeeAsync(visibly: true))
            _statusBarService.ShowSuccess("Schedule recalculated.");
    }

    /// <summary>!_busy.IsRunning guard for the same shared-DbContext reason every other
    /// command on this page uses, plus an actual employee selected outside multi-select
    /// mode -- same two conditions CalendarHeaderText itself falls back from ("Select an
    /// employee"/"Multiple employees -- select days, then assign"), since there's nothing
    /// single-employee to recalculate in either of those states (the calendar is already
    /// blank -- see RefreshCalendarAttendanceStatusesAsync's own early-return for the same
    /// two checks).</summary>
    private bool CanRecalculateSchedule() =>
        !_busy.IsRunning && !_multiSelectMode.IsMultiSelectMode && _tree.SelectedEmployee is not null;

    /// <summary>What SchedulePage's calendar-header icon button is actually wired to now --
    /// same "one button, toggling in place" treatment as ReportViewModel.
    /// RefreshOrCancelSummary/PayrollSummaryViewModel.RefreshOrCancelPayslip (see either
    /// one's own doc comment for the fuller reasoning): Content/ToolTip swap to Cancel and
    /// Command switches to _busy.Cancel() while _busy.IsVisiblyRunning, instead of
    /// RecalculateScheduleAsync just sitting disabled with no way to stop it. This page
    /// never had a Cancel button of its own either, the same "adds cancellability here for
    /// the first time" situation as the payslip header's button.
    ///
    /// CanExecute is IsVisiblyRunning (always fine to try to cancel) OR
    /// CanRecalculateSchedule() -- needed because CanRecalculateSchedule() alone would leave
    /// the button disabled during a run it didn't itself start (e.g. an automatic
    /// RequestScheduleRefresh/RequestCalendarStatusRefresh, or one of
    /// ScheduleAssignmentViewModel's five schedule-write commands) at exactly the moment
    /// IsVisiblyRunning makes it look like a live Cancel button.</summary>
    private bool CanRefreshOrCancelSchedule() => _busy.IsVisiblyRunning || CanRecalculateSchedule();

    [RelayCommand(CanExecute = nameof(CanRefreshOrCancelSchedule))]
    private void RefreshOrCancelSchedule()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            _ = RecalculateScheduleAsync();
    }

    /// <summary>Segoe Fluent Icons glyphs for RefreshOrCancelScheduleCommand's button -- see
    /// ReportViewModel.RefreshOrCancelGlyph's own doc comment for why these two specific
    /// codepoints (Refresh/Cancel).</summary>
    public string RefreshOrCancelGlyph => _busy.IsVisiblyRunning ? "" : "";

    /// <summary>Generic on purpose, not "Stop this recalculation" -- _busy is the one
    /// AttendanceBusyState instance shared app-wide (Attendance, Schedule, and Payroll all
    /// construct their children from the same DI-scoped instance -- see App.xaml.cs), so
    /// this can be true because of literally anything currently running anywhere in the
    /// app, not only a click on this same button.</summary>
    public string RefreshOrCancelToolTip => _busy.IsVisiblyRunning
        ? "Stop whatever's currently running."
        : "Recalculate this employee's schedule and attendance markers using the latest data.";


    /// <summary>Builds CalendarDayViewModel for every visible cell and kicks off the
    /// completion-marker recompute for whichever employee/month is now showing --
    /// returns that recompute's own Task rather than firing it fire-and-forget itself, so
    /// a caller that genuinely needs it finished before continuing (RefreshScheduleForSelectedEmployeeAsync,
    /// when called with visibly: true right after a schedule write -- see that method's
    /// own doc comment for the race this closes) can await it. Every other caller
    /// (OnDisplayedMonthChanged, and RefreshScheduleForSelectedEmployeeAsync's own
    /// visibly: false/auto-triggered case) discards the returned Task the same way a bare
    /// `_ = RefreshCalendarAttendanceStatusesAsync();` already did before this existed --
    /// same fire-and-forget behavior for them, unchanged.</summary>
    private Task RebuildCalendar()
    {
        CalendarDays.Clear();

        var firstOfMonth = new DateOnly(DisplayedMonth.Year, DisplayedMonth.Month, 1);
        var firstCell = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);

        // At most one entry per date since entries are unique per (EmployeeId, Date).
        var entriesByDate = ScheduleEntries.ToDictionary(e => e.Date);

        for (var i = 0; i < 42; i++)
        {
            var date = firstCell.AddDays(i);
            entriesByDate.TryGetValue(date, out var entry);
            var isHoliday = _holidaysByDate.TryGetValue(date, out var holidayName);

            var day = new CalendarDayViewModel
            {
                Date = date,
                IsCurrentMonth = date.Month == firstOfMonth.Month,
                Entry = entry,
                IsHoliday = isHoliday,
                HolidayName = holidayName ?? string.Empty
            };
            day.PropertyChanged += OnCalendarDaySelectionChanged;
            CalendarDays.Add(day);
        }

        // Every day above starts unselected, so whatever the button said before this
        // rebuild (from the previous month, or the previous employee) is stale.
        UpdateSetScheduleButtonText();

        // Every path that lands here (employee change, month change, multi-select toggle,
        // and after a schedule set/clear via RefreshScheduleForSelectedEmployeeAsync) needs
        // the completion markers recomputed for whatever's now showing, so this is the one
        // place to trigger it rather than repeating the call at each caller.
        return RefreshCalendarAttendanceStatusesAsync();
    }

    /// <summary>
    /// Computes each visible day's schedule-vs-punches result (the same engine behind the
    /// Attendance tab's reports -- see AttendanceWorkflowService) for whichever
    /// employee/month the calendar is currently showing, and feeds it into each
    /// CalendarDayViewModel.AttendanceStatus for the bottom-left completion marker. Runs
    /// quietly in the background: a slow or failed lookup (e.g. punches not yet imported)
    /// just leaves markers off rather than interrupting whoever's setting a schedule, since
    /// this is a decoration on top of the calendar, not something the rest of the page
    /// depends on.
    ///
    /// Checks _attendanceStatusCache first (see that field's own doc comment), before
    /// touching _busy, the database, or even _attendanceStatusCts -- a hit applies
    /// synchronously and returns. This is what actually fixes the "markers go missing when
    /// switching between employees" bug: this method used to unconditionally blank every
    /// marker *before* even checking _busy.IsRunning, so revisiting an employee/month while
    /// something else (most often PayrollViewModel's own refresh, reacting to that exact
    /// same SelectedEmployee change) still had the shared, app-lifetime-scoped
    /// ScheduleDbContext busy left the calendar blank until the deferred re-run eventually
    /// got its turn -- and a *second* switch before that turn came superseded the deferred
    /// flag with yet another blank-and-defer, so clicking through several employees in a
    /// row could leave the calendar permanently blank for whichever one was landed on. A
    /// cache hit needs none of that: it doesn't touch _busy at all, so it can't get stuck
    /// behind whatever else is using it, and it never blanks CalendarDays in the first
    /// place, so there's no gap for a slower, unrelated operation to be seen through.
    ///
    /// On a miss, this is otherwise the same shape as before -- cancel-and-replace
    /// _attendanceStatusCts, blank the markers, defer behind _busy if it's already running,
    /// then run the actual lookup through _busy.RunAsync -- with two things added once the
    /// result comes back: the identity/month guard (see the comment at that check below,
    /// which _attendanceStatusCts's own cancellation can't fully cover on its own) and
    /// storing the freshly-computed result into _attendanceStatusCache before applying it,
    /// so the *next* visit to this employee/month doesn't have to repeat the round trip.
    ///
    /// Public -- gap found while landing phase 4: this class's own summary above (and
    /// RefreshScheduleForSelectedEmployeeAsync's doc comment) named five members phase 4's
    /// ScheduleAssignmentViewModel would need and made exactly those five public, but missed
    /// a sixth call site -- AddManualEntryForDayAsync (squarely ScheduleAssignmentViewModel's
    /// per the component list) calls this directly, right after saving a manual entry, the
    /// same call MainViewModel.AddManualEntryForDayAsync makes today, so that tile's own
    /// completion marker updates immediately rather than waiting for the next unrelated
    /// trigger to recompute it. Same "phase N's own table already needs it, so make it
    /// accessible now" reasoning as this file's other public members; flagging the fix here
    /// the way phase 2's own GetCheckedEmployees doc comment flagged its own gap, for
    /// whoever next reads this table against this file.
    /// </summary>
    public async Task RefreshCalendarAttendanceStatusesAsync()
    {
        // Multi-select mode has no single employee to compute against -- that, or no
        // employee being selected at all, leaves every marker off. Still cancels whatever
        // _attendanceStatusCts currently represents -- same as every other entry into this
        // method -- so a lookup started for an employee the person has since moved away
        // from can't land here once mode/selection has already moved on; there's no cache
        // key to look up without an employee, so that's skipped, but the cancellation isn't.
        if (_multiSelectMode.IsMultiSelectMode || _tree.SelectedEmployee is not { } employee || CalendarDays.Count == 0)
        {
            _attendanceStatusCts?.Cancel();
            foreach (var day in CalendarDays)
                day.AttendanceStatus = null;
            return;
        }

        var pin = employee.Pin;
        var employeeId = _tree.SelectedEmployee.Id;
        var month = DisplayedMonth;
        var cacheKey = (employeeId, month);

        // Cache hit: apply and return immediately. Deliberately does NOT cancel
        // _attendanceStatusCts -- a still in-flight lookup for a different employee/month
        // is harmless to leave running: the identity/month guard on its own eventual
        // completion (below) stops it from overwriting whatever's on screen by then, and
        // letting it finish naturally still warms this cache for whichever employee/month
        // it was actually for, in case that's revisited too.
        if (_attendanceStatusCache.TryGetValue(cacheKey, out var cached) && cached.IsCurrentFor(_dataVersion))
        {
            ApplyAttendanceStatusByDate(cached.StatusByDate);
            return;
        }

        _attendanceStatusCts?.Cancel();
        var cts = new CancellationTokenSource();
        _attendanceStatusCts = cts;

        foreach (var day in CalendarDays)
            day.AttendanceStatus = null;

        // Defer to whatever's already using the shared, app-lifetime-scoped
        // ScheduleDbContext -- most commonly PayrollViewModel's own RequestRefresh,
        // reacting to this exact same SelectedEmployee change -- rather than starting a
        // second, concurrent operation against it. The _busy.PropertyChanged handler in
        // this class's own constructor re-runs this once that clears, so the markers still
        // end up correct for whatever's currently showing, just slightly delayed rather
        // than silently lost -- and, by the time that re-run happens, quite possibly served
        // straight from cache instead of triggering yet another database round trip.
        if (_busy.IsRunning)
        {
            _calendarStatusRefreshPending = true;
            return;
        }

        var request = new AttendanceRunRequest
        {
            Policy = _attendanceSettings.Policy,
            PeriodStart = CalendarDays.Min(d => d.Date),
            PeriodEnd = CalendarDays.Max(d => d.Date),
            TargetPins = [pin]
        };

        // Routed through _busy.RunAsync (rather than calling _attendanceRunner directly)
        // so this now holds IsRunning for its own duration too -- the other half of the fix
        // above: guarding entry with `if (_busy.IsRunning)` stops this method from racing
        // an *already-running* Payroll refresh, and running this itself under _busy stops
        // a Payroll refresh that starts *after* this one from racing it right back.
        // visibly: false since this is the calendar's own silent decoration, not something
        // the person explicitly asked to wait on -- same reasoning as
        // RefreshScheduleForSelectedEmployeeAsync's default.
        AttendanceRunResult? result = null;
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            result = await _attendanceRunner.RunAsync(request, new Progress<string>(), cancellationToken);
        },
        // TEMPORARY DIAGNOSTIC -- normally no onError here (a failed or cancelled run is
        // swallowed silently and just leaves markers off, same "best-effort decoration"
        // reasoning as before -- result stays null in that case, and nothing gets cached
        // for this key). Wired to the status bar for now so the exception that's actually
        // killing the markers after a manual punch is added shows up somewhere instead of
        // only going to Debug.WriteLine (invisible outside a debugger). Revert to the old
        // 2-argument RunAsync call once the real cause is found.
        onError: ex => _statusBarService.ShowError($"Calendar marker refresh failed: {ex}"));

        if (result is null || cts.IsCancellationRequested) return;

        var statusByDate = ComputeAttendanceStatusByDate(result);

        // Cached under the employee/month this request was actually for -- not whatever's
        // currently selected -- and stamped with _dataVersion's counters as of right now
        // rather than as of when the request started, so a version bump that landed while
        // this was in flight correctly makes the entry look stale again on its very next
        // lookup instead of being trusted one call too long. Stored even if the
        // identity/month guard just below is about to skip applying it to CalendarDays:
        // switching away mid-lookup doesn't waste the result, it just means the *current*
        // view isn't the one that gets to use it yet.
        _attendanceStatusCache[cacheKey] = new CachedAttendanceStatus(
            _dataVersion.DeviceLogsVersion, _dataVersion.ManualLogsVersion, _dataVersion.ScheduleVersion, statusByDate);

        // Identity/month guard: the person may have switched to a different employee, or
        // paged the calendar to a different month, while the lookup above was still
        // running. cts.IsCancellationRequested (checked above) only catches a *newer
        // cache-miss* call superseding this one -- it says nothing about a switch that
        // landed on a cache *hit* instead, which never touches _attendanceStatusCts at all
        // (see that branch's own comment). Applying a late result for an employee/month
        // that isn't current anymore would stomp on whatever's correctly showing now with
        // data for someone -- or somewhen -- no longer on screen.
        if (_tree.SelectedEmployee?.Id != employeeId || DisplayedMonth != month) return;

        ApplyAttendanceStatusByDate(statusByDate);
    }

    /// <summary>The status-computation half of what RefreshCalendarAttendanceStatusesAsync
    /// used to do inline -- turns one AttendanceRunResult into the final, already-resolved
    /// per-day marker set (Leave dates dropped entirely, a Rest Day with a fulfilled duty
    /// already promoted to PunchStatus.Complete) that both a fresh database lookup and a
    /// cache hit apply identically, via ApplyAttendanceStatusByDate below. Extracted so
    /// this is also exactly what a CachedAttendanceStatus stores -- a cache hit is then a
    /// straight dictionary lookup into CalendarDayViewModel.AttendanceStatus with none of
    /// this method's own business logic to re-run.</summary>
    private static Dictionary<DateOnly, DayMarker> ComputeAttendanceStatusByDate(AttendanceRunResult result)
    {
        // A Flexible day with several segments produces one AttendanceSummary per segment
        // (see FlexibleShiftCalculationStrategy), so a date can map to more than one status
        // -- if any segment that day is Absent, the day shouldn't read as Complete just
        // because another segment went fine, so the worst status among a date's summaries
        // wins. Leave and Official Business are never mixed with the other three (each is a
        // single whole-day ScheduleType, not a segment), so neither competes here -- see
        // the day-resolution loop below for how they're handled instead.
        var worstFirst = new[]
        {
            PunchStatus.Absent, PunchStatus.Partial, PunchStatus.Complete,
            PunchStatus.Leave, PunchStatus.OfficialBusiness,
        };
        var statusByDate = result.Summaries
            .GroupBy(s => s.ShiftDate)
            .ToDictionary(
                g => g.Key,
                g => new DayMarker(
                    g.Select(s => s.Status).OrderBy(s => Array.IndexOf(worstFirst, s)).First(),
                    // Any segment/summary that day whose picked clock-in or clock-out is a
                    // manual entry -- see AttendanceSummary.ClockInIsManual. Keeps the
                    // tile's punch menu editable so that hand-entered punch can still be
                    // corrected or removed even once the day reads Complete.
                    g.Any(s => s.ClockInIsManual || s.ClockOutIsManual)));

        // RestDayShiftCalculationStrategy's own Status is always
        // PunchStatus.RestDay, whether or not a duty was actually recognized
        // that day (see its own doc comment) -- Payroll/Reports/Excel all key
        // off WorkedHours instead of Status for that distinction, so statusByDate
        // above can't tell a worked Rest Day apart from a plain day off. The
        // Complete checkmark -- Normal/Flexible-only until now -- reuses that
        // same WorkedHours > 0 signal (the exact test PayrollCalculator already
        // uses to decide whether the Rest Day Pay premium applies) rather than
        // introducing a new one: WorkedHours can only be positive coming out of
        // CalculateWindowedDay with both a clock-in and clock-out punch found,
        // never out of CalculateUnscheduledDay's plain-day-off branch, so a
        // date only lands here when a duty was both scheduled and fulfilled.
        // This only changes what gets written to the UI-only
        // CalendarDayViewModel.AttendanceStatus below -- the summary's own
        // Status stays PunchStatus.RestDay everywhere else (payroll, the
        // Attendance report, Excel export), so none of those are affected.
        var restDayDutyFulfilledDates = result.Summaries
            .Where(s => s.ScheduleType == ScheduleType.RestDay && s.WorkedHours > 0)
            .Select(s => s.ShiftDate)
            .ToHashSet();

        var resolved = new Dictionary<DateOnly, DayMarker>();
        foreach (var (date, marker) in statusByDate)
        {
            var status = marker.Status;
            // Leave keeps the cell's own background color (see CalendarDayToBrushConverter)
            // instead of also getting a marker here -- a second "this was Leave" indicator
            // on top of that would just be noise, so it's dropped from the resolved set
            // entirely rather than carried through as a value ApplyAttendanceStatusByDate
            // would just skip anyway. Official Business gets its own purple star marker
            // (see OfficialBusinessMarker in MonthCalendarControl.xaml) in addition to its
            // background color, since the star is also what the Summary report's own OB
            // count/conditional-formatting uses to flag it. A Rest Day with a fulfilled
            // duty (see restDayDutyFulfilledDates above) gets the same Complete checkmark a
            // Normal/Flexible day would, on top of its own RestDayBrush background -- an
            // unscheduled Rest Day, or one where the duty wasn't fulfilled, keeps reading
            // as plain PunchStatus.RestDay (no marker).
            if (status is PunchStatus.Leave) continue;

            var resolvedStatus = restDayDutyFulfilledDates.Contains(date) ? PunchStatus.Complete : status;
            resolved[date] = marker with { Status = resolvedStatus };
        }

        return resolved;
    }

    /// <summary>One day's resolved calendar marker: the completion status shown as the
    /// bottom-left glyph, plus whether a hand-entered punch is part of what produced it
    /// (<see cref="CalendarDayViewModel.HasManualPunch"/> -- keeps the tile's punch menu
    /// editable on an otherwise-Complete non-Flexible day). Cached per employee/month in
    /// <see cref="CachedAttendanceStatus"/> so both flags survive a cache hit.</summary>
    private sealed record DayMarker(PunchStatus Status, bool HasManualPunch);

    /// <summary>The marker-assignment half of what RefreshCalendarAttendanceStatusesAsync
    /// used to do inline -- writes an already-resolved per-day marker set (see
    /// ComputeAttendanceStatusByDate above, or a CachedAttendanceStatus's own StatusByDate)
    /// onto the currently-built CalendarDays, null for any date with no entry (no schedule
    /// that day, a Rest Day/Leave day resolved to "no marker," or a day this run's
    /// PeriodStart/PeriodEnd never covered). Shared by both the fresh-lookup path and the
    /// cache-hit path in RefreshCalendarAttendanceStatusesAsync -- the whole point of
    /// resolving Leave/Rest-Day handling ahead of time in ComputeAttendanceStatusByDate
    /// rather than here is that a cache hit has no AttendanceRunResult left to re-derive
    /// that from, only the already-resolved dictionary.</summary>
    private void ApplyAttendanceStatusByDate(IReadOnlyDictionary<DateOnly, DayMarker> markersByDate)
    {
        foreach (var day in CalendarDays)
        {
            if (markersByDate.TryGetValue(day.Date, out var marker))
            {
                day.AttendanceStatus = marker.Status;
                day.HasManualPunch = marker.HasManualPunch;
            }
            else
            {
                day.AttendanceStatus = null;
                day.HasManualPunch = false;
            }
        }
    }

    private void OnCalendarDaySelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarDayViewModel.IsSelected))
            UpdateSetScheduleButtonText();
    }

    private void UpdateSetScheduleButtonText()
    {
        var hasAnyExisting = CalendarDays.Any(d => d.IsSelected && d.Entry is not null);
        SetScheduleButtonText = hasAnyExisting ? "Edit Schedule for Selected Days" : "Set Schedule for Selected Days";
    }

    /// <summary>
    /// Looks at the ScheduleEntry (if any) currently on each given date and reports two
    /// things: whether any of them has one at all, and -- only when every single one does,
    /// and they're all the same shape (type, hours, time-in, and for SplitShift, the same
    /// set of punching windows, or for Flexible, the same restricted window) -- what that
    /// shared schedule is, for ApplyScheduleDialog to prefill. Any day with nothing set, or
    /// any difference between days, means no single answer to prefill with -- see
    /// ScheduleAssignmentViewModel.SetScheduleForSelectionAsync (phase 4). Public for the
    /// same "phase 4's own table already needs it" reason as RefreshScheduleForSelectedEmployeeAsync
    /// above -- that method is the only caller named in the split plan (the bulk-assign
    /// branch deliberately skips prefill/edit framing, since "what's currently set" has no
    /// single answer across several employees).
    /// </summary>
    public (bool HasAnyExisting, ScheduleEntry? UniformEntry) AnalyzeSelectionSchedule(IReadOnlyCollection<DateOnly> dates)
    {
        var entries = dates.Select(d => ScheduleEntries.FirstOrDefault(e => e.Date == d)).ToList();
        var hasAnyExisting = entries.Any(e => e is not null);

        if (!hasAnyExisting || entries.Any(e => e is null))
            return (hasAnyExisting, null);

        var first = entries[0]!;
        var isUniform = entries.All(e => IsSameSchedule(e!, first));

        return (hasAnyExisting, isUniform ? first : null);
    }

    private static bool IsSameSchedule(ScheduleEntry a, ScheduleEntry b)
    {
        if (a.ScheduleType != b.ScheduleType || a.WorkTimeHours != b.WorkTimeHours || a.TimeIn != b.TimeIn)
            return false;

        // Flexible's own restricted window -- SplitShift never carries these (see
        // ScheduleEntry.ValidateScheduleTypeShape), so this is only ever a live comparison
        // for Flexible days, but it's cheap to check unconditionally rather than gating it
        // on ScheduleType here too.
        if (a.RestrictedTimeIn != b.RestrictedTimeIn || a.RestrictedTimeOut != b.RestrictedTimeOut)
            return false;

        if (a.FlexibleSegments.Count != b.FlexibleSegments.Count)
            return false;

        var aSorted = a.FlexibleSegments.OrderBy(s => s.TimeIn)
            .Select(s => (s.TimeIn, s.TimeOut, s.ClockInBufferHours, s.ClockOutBufferHours));
        var bSorted = b.FlexibleSegments.OrderBy(s => s.TimeIn)
            .Select(s => (s.TimeIn, s.TimeOut, s.ClockInBufferHours, s.ClockOutBufferHours));
        return aSorted.SequenceEqual(bSorted);
    }

    /// <summary>Days currently highlighted in the calendar -- the target set for
    /// Set/Clear/Leave Schedule. Public (unlike MainViewModel's own private copy) --
    /// ScheduleAssignmentViewModel (phase 4) is the caller once it exists (see
    /// SetScheduleForSelectionAsync/AssignScheduleToCheckedEmployeesAsync/
    /// SetLeaveForSelectionAsync/SetLeaveForCheckedEmployeesAsync/ClearScheduleForSelectionAsync
    /// in MainViewModel today) -- same "GetCheckedEmployees" precedent EmployeeTreeViewModel
    /// already set in phase 2.</summary>
    public List<DateOnly> GetSelectedDates() =>
        CalendarDays.Where(d => d.IsSelected).Select(d => d.Date).Distinct().OrderBy(d => d).ToList();

    /// <summary>Re-applies a day selection onto the current CalendarDays -- used by
    /// ScheduleAssignmentViewModel.ToggleHolidayForSelectionAsync to put the highlight back
    /// after its own RefreshScheduleForSelectedEmployeeAsync rebuilds the grid with fresh
    /// CalendarDayViewModel instances (which otherwise start unselected). Deliberately not
    /// done for the Set/Clear/Leave Schedule commands -- see that method's own comment for
    /// why the holiday toggle keeps the selection where those don't.</summary>
    public void SelectDates(IReadOnlySet<DateOnly> dates)
    {
        foreach (var day in CalendarDays)
            day.IsSelected = dates.Contains(day.Date);
    }

    [RelayCommand]
    private void ClearCalendarSelection()
    {
        foreach (var day in CalendarDays)
            day.IsSelected = false;
    }

    /// <summary>Ranges are only computed for a readable summary in ApplyScheduleDialog --
    /// the schedule itself is applied per-day, one upsert call per date in
    /// ScheduleAssignmentViewModel (phase 4). Static (doesn't touch any instance state, so
    /// there's nothing to share by taking this class as a constructor dependency for) and
    /// public for the same forward-looking reason as this class's other members above --
    /// phase 4 calls this by type name (ScheduleCalendarViewModel.GroupIntoContiguousRanges),
    /// not through its own Calendar reference, since a static member can't be reached
    /// through an instance.</summary>
    public static List<(DateOnly Start, DateOnly End)> GroupIntoContiguousRanges(List<DateOnly> sortedDates)
    {
        var ranges = new List<(DateOnly Start, DateOnly End)>();
        var rangeStart = sortedDates[0];
        var previous = sortedDates[0];

        foreach (var date in sortedDates.Skip(1))
        {
            if (date == previous.AddDays(1))
            {
                previous = date;
                continue;
            }

            ranges.Add((rangeStart, previous));
            rangeStart = date;
            previous = date;
        }

        ranges.Add((rangeStart, previous));
        return ranges;
    }

    /// <summary>One employee/month's already-resolved completion markers, as stored in
    /// _attendanceStatusCache -- see that field's own doc comment for the cache this
    /// backs. The three counters mirror exactly what ReportViewModel/PunchRecordsViewModel/
    /// ManualEntriesViewModel already snapshot for their own single-slot staleness checks
    /// (see e.g. PunchRecordsViewModel.ShouldAutoReload's `_dataVersion.DeviceLogsVersion`
    /// comparison) -- the same three matter here for the same reason ReportViewModel cares
    /// about all three rather than just ScheduleVersion: this class's own
    /// AttendanceRunRequest merges the schedule against both punch tables (see
    /// AttendanceWorkflowService), so a device import/fetch or a manual-entry add/edit/
    /// delete changes what these markers should read just as much as a schedule edit does,
    /// even though neither writes to ScheduleEntries itself. Per cache entry rather than
    /// one shared field -- unlike those three tabs, which only ever need their own most
    /// recent load, this class needs to keep more than one employee/month's result warm at
    /// once, so each entry snapshots its own three counters independently rather than the
    /// class comparing against a single shared "last loaded" tuple.</summary>
    private sealed record CachedAttendanceStatus(
        int DeviceLogsVersion, int ManualLogsVersion, int ScheduleVersion,
        IReadOnlyDictionary<DateOnly, DayMarker> StatusByDate)
    {
        public bool IsCurrentFor(AttendanceDataVersion dataVersion) =>
            DeviceLogsVersion == dataVersion.DeviceLogsVersion
            && ManualLogsVersion == dataVersion.ManualLogsVersion
            && ScheduleVersion == dataVersion.ScheduleVersion;
    }
}
