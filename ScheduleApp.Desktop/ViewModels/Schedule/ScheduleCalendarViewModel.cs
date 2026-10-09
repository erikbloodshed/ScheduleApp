using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// Backs the Schedule tab's calendar: month navigation, the 42-cell day grid, the selected
/// employee's schedule behind it, day selection, and each cell's attendance-completion marker.
///
/// Follows <see cref="EmployeeTreeViewModel.SelectedEmployee"/> and
/// <see cref="MultiSelectModeState"/> for itself: a new employee reloads the schedule, and in
/// multi-select mode (or with nobody selected) the grid stays blank -- there's no single
/// employee whose schedule it could show. ScheduleAssignmentViewModel writes schedules and
/// calls back into <see cref="RefreshScheduleForSelectedEmployeeAsync"/> afterwards.
///
/// Every database read goes through the app-wide <see cref="AttendanceBusyState"/>, since the
/// ScheduleDbContext is shared: one that arrives while something else is running is deferred
/// and replayed once it's idle, rather than racing it.
/// </summary>
public partial class ScheduleCalendarViewModel : ViewModelBase
{
    private readonly IScheduleRepository _repository;

    /// <summary>The company-wide Holidays table -- a handful of rows a year, so it's read
    /// whole rather than per month, and month navigation has every month's holidays on
    /// hand.</summary>
    private readonly IHolidayRepository _holidayRepository;

    private readonly AttendanceSettings _attendanceSettings;
    private readonly IAttendanceRunner _attendanceRunner;
    private readonly AttendanceBusyState _busy;

    /// <summary>MainViewModel's SaveViewState, called whenever the month changes.</summary>
    private readonly Action _saveViewState;

    private readonly EmployeeTreeViewModel _tree;
    private readonly MultiSelectModeState _multiSelectMode;

    /// <summary>Only read, as the staleness half of <see cref="_attendanceStatusCache"/>.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    private readonly IObservable<bool> _canRecalculateSchedule;
    private readonly IObservable<bool> _canRefreshOrCancelSchedule;

    /// <summary>Every holiday on file, by date -- what each cell's IsHoliday/HolidayName is
    /// stamped from. Reloaded with every schedule load, and on its own by
    /// <see cref="RefreshHolidaysAsync"/>.</summary>
    private Dictionary<DateOnly, string> _holidaysByDate = [];

    /// <summary>The selected employee's schedule -- empty in multi-select mode, with nobody
    /// selected, or for an employee with no Pin (who can't have one).</summary>
    private List<ScheduleEntry> _scheduleEntries = [];

    /// <summary>Bumped by every marker lookup that misses the cache (and every blanking): a
    /// lookup that finishes after a newer one started leaves its result in the cache but
    /// doesn't apply it.</summary>
    private int _attendanceStatusGeneration;

    /// <summary>
    /// Already-computed markers per (Employee.Id, DisplayedMonth), so switching back to
    /// someone recently viewed applies them straight away -- no wait on the shared
    /// ScheduleDbContext, and no blank-then-refill flicker. That wait used to be the cause of
    /// markers going missing when clicking quickly through employees: every switch blanked
    /// them before deferring behind whatever else was running.
    ///
    /// Each entry snapshots the three AttendanceDataVersion counters it was computed under and
    /// is only served while all three still match -- a schedule edit, a device import, or a
    /// manual entry can each change any day's marker. Stale entries are simply overwritten on
    /// their next use; the key space is bounded by what one session views.
    /// </summary>
    private readonly Dictionary<(int EmployeeId, DateTime Month), CachedAttendanceStatus> _attendanceStatusCache = [];

    /// <summary>A marker refresh that arrived while _busy was running, replayed once it's
    /// idle.</summary>
    private bool _calendarStatusRefreshPending;

    /// <summary>A schedule refresh that arrived while _busy was running (see
    /// <see cref="RequestScheduleRefresh"/>), and whether any of the deferred requests asked
    /// to be visible -- OR'd, so an explicit request deferred behind a silent one still shows
    /// progress when it runs.</summary>
    private bool _scheduleRefreshPending;

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
        : base(statusBarService)
    {
        _repository = repository;
        _holidayRepository = holidayRepository;
        _attendanceSettings = attendanceSettings;
        _attendanceRunner = attendanceRunner;
        _busy = busy;
        _saveViewState = saveViewState;
        _tree = tree;
        _multiSelectMode = multiSelectMode;
        _dataVersion = dataVersion;

        // ViewStateStore is session-only, so a fresh launch opens on the current month.
        DisplayedMonth = viewStateStore.Schedule.DisplayedMonth ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        _displayedMonthTextHelper = this.WhenAnyValue(x => x.DisplayedMonth)
            .Select(month => month.ToString("MMMM yyyy", CultureInfo.CurrentCulture))
            .ToProperty(this, x => x.DisplayedMonthText);

        // Every selection change across the grid now showing; a rebuilt grid starts unselected.
        _setScheduleButtonTextHelper = Observable.Switch(this.WhenAnyValue(x => x.ShownDays)
                .Select(days => days.Select(day => day.WhenAnyValue(d => d.IsSelected).Skip(1)).Merge()
                    .Select(_ => days)
                    .StartWith(days)))
            .Select(days => days.Any(d => d.IsSelected && d.Entry is not null)
                ? "Edit Schedule for Selected Days"
                : "Set Schedule for Selected Days")
            .DistinctUntilChanged()
            .ToProperty(this, x => x.SetScheduleButtonText);

        var visiblyRunning = _busy.WhenAnyValue(b => b.IsVisiblyRunning);
        _refreshOrCancelGlyphHelper = visiblyRunning
            .Select(running => running ? "" : "")
            .ToProperty(this, x => x.RefreshOrCancelGlyph);
        _refreshOrCancelToolTipHelper = visiblyRunning
            .Select(running => running
                ? "Stop whatever's currently running."
                : "Recalculate this employee's schedule and attendance markers using the latest data.")
            .ToProperty(this, x => x.RefreshOrCancelToolTip);

        // The same two states the calendar is blank in -- nothing single-employee to
        // recalculate there.
        _canRecalculateSchedule = Observable.CombineLatest(
                _busy.WhenAnyValue(b => b.IsRunning),
                _multiSelectMode.WhenAnyValue(m => m.IsMultiSelectMode),
                _tree.WhenAnyValue(t => t.SelectedEmployee),
                (running, multiSelect, employee) => !running && !multiSelect && employee is not null)
            .DistinctUntilChanged();
        // Cancel is always fine to try -- including during a run this button didn't start.
        _canRefreshOrCancelSchedule = Observable.CombineLatest(visiblyRunning, _canRecalculateSchedule,
            (running, canRecalculate) => running || canRecalculate);

        ReportFailuresOf(PreviousMonthCommand, NextMonthCommand, ClearCalendarSelectionCommand,
            RecalculateScheduleCommand, RefreshOrCancelScheduleCommand);

        // Builds the grid for the opening month straight away. SaveViewState is a no-op until
        // the tree has restored its selection, so this can't clobber the saved state.
        this.WhenAnyValue(x => x.DisplayedMonth).Subscribe(month =>
        {
            _ = RebuildCalendar();
            _saveViewState();
        });

        _tree.WhenAnyValue(t => t.SelectedEmployee).Skip(1).Subscribe(_ => RequestScheduleRefresh());

        // Whatever was deferred while something else held _busy.
        _busy.WhenAnyValue(b => b.IsRunning)
            .Skip(1)
            .Where(running => !running)
            .Subscribe(idle =>
            {
                if (_scheduleRefreshPending)
                {
                    _scheduleRefreshPending = false;
                    var visibly = _scheduleRefreshPendingVisibly;
                    _scheduleRefreshPendingVisibly = false;
                    _ = RefreshScheduleForSelectedEmployeeAsync(visibly);
                }

                if (_calendarStatusRefreshPending)
                {
                    _calendarStatusRefreshPending = false;
                    _ = RefreshCalendarAttendanceStatusesAsync();
                }
            });
    }

    /// <summary>The selected employee's schedule, as last loaded.</summary>
    public IReadOnlyList<ScheduleEntry> ScheduleEntries => _scheduleEntries;

    /// <summary>The 42 cells now showing -- replaced in one change per rebuild.</summary>
    public RangeObservableCollection<CalendarDayViewModel> CalendarDays { get; } = [];

    /// <summary>The cells the last rebuild made -- what the selection-driven button text
    /// follows.</summary>
    [Reactive]
    internal partial IReadOnlyList<CalendarDayViewModel> ShownDays { get; private set; } = [];

    /// <summary>Always the 1st of a month. Changing it rebuilds the grid (no schedule
    /// re-fetch: the employee's whole schedule is already loaded).</summary>
    [Reactive]
    public partial DateTime DisplayedMonth { get; set; }

    [ObservableAsProperty]
    public partial string DisplayedMonthText { get; }

    /// <summary>"Edit..." once any highlighted day already has a schedule (something to
    /// overwrite), "Set..." otherwise -- always "Set..." in multi-select mode, whose grid
    /// carries no schedule.</summary>
    [ObservableAsProperty(InitialValue = "Set Schedule for Selected Days")]
    public partial string SetScheduleButtonText { get; }

    /// <summary>Refresh/Cancel (Segoe Fluent Icons), as ReportViewModel.RefreshOrCancelGlyph.</summary>
    [ObservableAsProperty]
    public partial string RefreshOrCancelGlyph { get; }

    /// <summary>Generic on purpose: _busy is shared app-wide, so what's running may be
    /// anything, not this button's own recalculation.</summary>
    [ObservableAsProperty]
    public partial string RefreshOrCancelToolTip { get; }

    [ReactiveCommand]
    private void PreviousMonth() => DisplayedMonth = DisplayedMonth.AddMonths(-1);

    [ReactiveCommand]
    private void NextMonth() => DisplayedMonth = DisplayedMonth.AddMonths(1);

    /// <summary>
    /// Reloads the selected employee's schedule now, or once _busy is idle if something else
    /// is using the shared ScheduleDbContext -- for the fire-and-forget triggers: an employee
    /// selected, multi-select mode toggled (visibly).
    ///
    /// ScheduleAssignmentViewModel's schedule writes call
    /// <see cref="RefreshScheduleForSelectedEmployeeAsync"/> directly instead: their command
    /// already refuses to start while _busy runs, so there's nothing to defer, and queuing a
    /// confirmed decision behind unrelated work would be worse than not starting it.
    /// </summary>
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

    /// <summary>
    /// Reloads the selected employee's schedule and the holidays, rebuilds the grid, and
    /// recomputes its markers -- all awaited, so a schedule write that calls this has finished
    /// every database touch before it returns (the person may switch straight to Payroll,
    /// which uses the same ScheduleDbContext). Call it after, not inside, your own
    /// _busy.RunAsync: the marker recompute is a second busy window of its own.
    ///
    /// <paramref name="visibly"/> as AttendanceBusyState.RunAsync: true for something the
    /// person is waiting on. Returns whether the schedule load itself succeeded -- a marker
    /// failure is only a missing decoration, not a failed reload.
    /// </summary>
    public async Task<bool> RefreshScheduleForSelectedEmployeeAsync(bool visibly = false)
    {
        _scheduleEntries = [];
        var succeeded = true;

        await _busy.RunAsync(visibly, async cancellationToken =>
        {
            if (_tree.SelectedEmployee is { Pin: { } pin } && !_multiSelectMode.IsMultiSelectMode)
                _scheduleEntries = await _repository.GetScheduleEntriesForEmployeeAsync(pin, cancellationToken);

            // Company-wide, so whoever is (or isn't) selected; folded into the same round trip.
            await LoadHolidaysByDateAsync(cancellationToken);
        },
        onError: ex =>
        {
            succeeded = false;
            ShowFailure(ex, "Schedule load failed");
        });

        // Whatever happened above, the grid has to reflect what's loaded now rather than keep
        // showing the previous employee.
        await RebuildCalendar();

        return succeeded;
    }

    /// <summary>A failed holiday read just leaves the "H" markers off; a cancel still
    /// propagates.</summary>
    private async Task LoadHolidaysByDateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var holidays = await _holidayRepository.ListAsync(cancellationToken);
            _holidaysByDate = holidays.ToDictionary(h => h.Date, h => h.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _holidaysByDate = [];
        }
    }

    /// <summary>Reloads just the holidays and rebuilds the grid -- on Schedule-page navigation
    /// (so holidays show before anyone is selected) and after a holiday is toggled.</summary>
    public async Task RefreshHolidaysAsync()
    {
        await _busy.RunAsync(visibly: false, LoadHolidaysByDateAsync);
        await RebuildCalendar();
    }

    /// <summary>The calendar header's Recalculate: the same reload a schedule write runs, on
    /// demand, reporting success as a direct click should.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRecalculateSchedule))]
    private async Task RecalculateScheduleAsync()
    {
        if (await RefreshScheduleForSelectedEmployeeAsync(visibly: true))
            StatusBar.ShowSuccess("Schedule recalculated.");
    }

    /// <summary>The calendar header's one button: Cancel while anything's visibly running,
    /// Recalculate otherwise. Synchronous, so it stays enabled -- as Cancel -- while the
    /// recalculation it starts runs.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRefreshOrCancelSchedule))]
    private void RefreshOrCancelSchedule()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            RecalculateScheduleCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>Builds the 42 cells for DisplayedMonth from what's loaded, and returns the
    /// marker recompute for them -- awaited by a reload, discarded by month navigation.</summary>
    private Task RebuildCalendar()
    {
        var firstOfMonth = new DateOnly(DisplayedMonth.Year, DisplayedMonth.Month, 1);
        var firstCell = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);

        // At most one entry per date (unique per EmployeeId + Date).
        var entriesByDate = _scheduleEntries.ToDictionary(e => e.Date);

        var days = new CalendarDayViewModel[42];
        for (var i = 0; i < days.Length; i++)
        {
            var date = firstCell.AddDays(i);
            entriesByDate.TryGetValue(date, out var entry);
            var isHoliday = _holidaysByDate.TryGetValue(date, out var holidayName);

            days[i] = new CalendarDayViewModel
            {
                Date = date,
                IsCurrentMonth = date.Month == firstOfMonth.Month,
                Entry = entry,
                IsHoliday = isHoliday,
                HolidayName = holidayName ?? string.Empty,
            };
        }

        CalendarDays.ReplaceAll(days);
        ShownDays = days;

        return RefreshCalendarAttendanceStatusesAsync();
    }

    /// <summary>
    /// Feeds each visible cell's completion marker from the attendance engine
    /// (AttendanceWorkflowService) for the employee/month showing. Best effort: a slow or
    /// failed lookup just leaves markers off rather than interrupting anyone.
    ///
    /// A cache hit (see <see cref="_attendanceStatusCache"/>) applies synchronously without
    /// touching _busy. A miss blanks the markers, defers behind _busy if it's running, then
    /// looks them up under _busy (silently) -- and applies the result only if it's still the
    /// latest lookup and the employee/month hasn't moved on since. Also called directly after
    /// a manual entry is saved, so that day's marker updates straight away.
    /// </summary>
    public async Task RefreshCalendarAttendanceStatusesAsync()
    {
        if (_multiSelectMode.IsMultiSelectMode || _tree.SelectedEmployee is not { } employee || CalendarDays.Count == 0)
        {
            _attendanceStatusGeneration++;
            ClearAttendanceStatuses();
            return;
        }

        var month = DisplayedMonth;
        var cacheKey = (employee.Id, month);

        // A lookup still in flight for someone else is left to finish: it can't apply over
        // this (the guard below), and it still warms the cache for whoever it was for.
        if (_attendanceStatusCache.TryGetValue(cacheKey, out var cached) && cached.IsCurrentFor(_dataVersion))
        {
            ApplyAttendanceStatusByDate(cached.StatusByDate);
            return;
        }

        var generation = ++_attendanceStatusGeneration;
        ClearAttendanceStatuses();

        if (_busy.IsRunning)
        {
            _calendarStatusRefreshPending = true;
            return;
        }

        var request = new AttendanceRunRequest
        {
            Policy = _attendanceSettings.Policy,
            PeriodStart = CalendarDays[0].Date,
            PeriodEnd = CalendarDays[^1].Date,
            TargetPins = [employee.Pin],
        };

        AttendanceRunResult? result = null;
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            result = await _attendanceRunner.RunAsync(request, new Progress<string>(), cancellationToken);
        },
        // TEMPORARY DIAGNOSTIC -- a failed lookup would normally be swallowed (markers just
        // stay off); reported for now so whatever kills the markers after a manual punch is
        // added shows up (and is logged with its stack). Drop the onError once it's found.
        onError: ex => ShowFailure(ex, "Calendar marker refresh failed"));

        if (result is null || generation != _attendanceStatusGeneration) return;

        var statusByDate = ComputeAttendanceStatusByDate(result);

        // Cached for the employee/month it was for, stamped with the counters as of now -- a
        // bump that landed mid-lookup makes it stale on its very next use.
        _attendanceStatusCache[cacheKey] = new CachedAttendanceStatus(
            _dataVersion.DeviceLogsVersion, _dataVersion.ManualLogsVersion, _dataVersion.ScheduleVersion, statusByDate);

        // A switch that landed on a cache hit never bumps the generation, so check identity too.
        if (_tree.SelectedEmployee?.Id != employee.Id || DisplayedMonth != month) return;

        ApplyAttendanceStatusByDate(statusByDate);
    }

    private void ClearAttendanceStatuses()
    {
        foreach (var day in CalendarDays)
            day.AttendanceStatus = null;
    }

    /// <summary>Turns one run into the final per-day markers both a fresh lookup and a cache
    /// hit apply: Leave dropped, a fulfilled Rest Day duty promoted to Complete.</summary>
    private static Dictionary<DateOnly, DayMarker> ComputeAttendanceStatusByDate(AttendanceRunResult result)
    {
        // A Flexible day has one summary per segment; the worst status among them wins, so one
        // Absent segment keeps the day from reading Complete. Leave and Official Business are
        // whole-day types and never mix with the other three.
        PunchStatus[] worstFirst =
        [
            PunchStatus.Absent, PunchStatus.Partial, PunchStatus.Complete,
            PunchStatus.Leave, PunchStatus.OfficialBusiness,
        ];
        var statusByDate = result.Summaries
            .GroupBy(s => s.ShiftDate)
            .ToDictionary(
                g => g.Key,
                g => new DayMarker(
                    g.Select(s => s.Status).OrderBy(s => Array.IndexOf(worstFirst, s)).First(),
                    // A hand-entered punch behind the day keeps the tile's punch menu editable
                    // even once it reads Complete.
                    g.Any(s => s.ClockInIsManual || s.ClockOutIsManual)));

        // A Rest Day's Status is always RestDay, worked or not; WorkedHours > 0 (the same test
        // PayrollCalculator uses for the Rest Day premium) is what says a scheduled duty was
        // fulfilled. Only this UI marker is promoted -- payroll, reports and Excel keep RestDay.
        var restDayDutyFulfilledDates = result.Summaries
            .Where(s => s.ScheduleType == ScheduleType.RestDay && s.WorkedHours > 0)
            .Select(s => s.ShiftDate)
            .ToHashSet();

        var resolved = new Dictionary<DateOnly, DayMarker>();
        foreach (var (date, marker) in statusByDate)
        {
            // Leave already shows as the cell's background; a marker on top would be noise.
            // Official Business keeps its star marker as well as its background.
            if (marker.Status is PunchStatus.Leave) continue;

            var status = restDayDutyFulfilledDates.Contains(date) ? PunchStatus.Complete : marker.Status;
            resolved[date] = marker with { Status = status };
        }

        return resolved;
    }

    /// <summary>One day's marker: the status glyph, and whether a hand-entered punch is part
    /// of it (<see cref="CalendarDayViewModel.HasManualPunch"/>).</summary>
    private sealed record DayMarker(PunchStatus Status, bool HasManualPunch);

    /// <summary>Writes resolved markers onto the cells; a date without one shows none.</summary>
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

    /// <summary>
    /// Whether any of <paramref name="dates"/> has a schedule, and -- only when every one does
    /// and they're all the same shape (type, hours, time-in, Flexible window and segments) --
    /// that shared schedule, for ApplyScheduleDialog to prefill.
    /// </summary>
    public (bool HasAnyExisting, ScheduleEntry? UniformEntry) AnalyzeSelectionSchedule(IReadOnlyCollection<DateOnly> dates)
    {
        var entries = dates.Select(d => _scheduleEntries.Find(e => e.Date == d)).ToList();
        var hasAnyExisting = entries.Exists(e => e is not null);

        if (!hasAnyExisting || entries.Exists(e => e is null))
            return (hasAnyExisting, null);

        var first = entries[0]!;
        var isUniform = entries.TrueForAll(e => IsSameSchedule(e!, first));

        return (hasAnyExisting, isUniform ? first : null);
    }

    private static bool IsSameSchedule(ScheduleEntry a, ScheduleEntry b)
    {
        if (a.ScheduleType != b.ScheduleType || a.WorkTimeHours != b.WorkTimeHours || a.TimeIn != b.TimeIn)
            return false;

        // Only ever set on Flexible days, but cheap to compare unconditionally.
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

    /// <summary>The highlighted days, in order -- what Set/Clear/Leave Schedule apply to.</summary>
    public List<DateOnly> GetSelectedDates() =>
        [.. CalendarDays.Where(d => d.IsSelected).Select(d => d.Date).Distinct().Order()];

    /// <summary>Re-highlights <paramref name="dates"/> on the current cells -- the holiday
    /// toggle uses it to keep its selection across the rebuild its reload causes.</summary>
    public void SelectDates(IReadOnlySet<DateOnly> dates)
    {
        foreach (var day in CalendarDays)
            day.IsSelected = dates.Contains(day.Date);
    }

    [ReactiveCommand]
    private void ClearCalendarSelection()
    {
        foreach (var day in CalendarDays)
            day.IsSelected = false;
    }

    /// <summary>Contiguous runs within <paramref name="sortedDates"/> -- only for
    /// ApplyScheduleDialog's readable summary; schedules are written per day.</summary>
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

    /// <summary>One employee/month's resolved markers, with the three AttendanceDataVersion
    /// counters they were computed under -- the run merges the schedule with both punch
    /// tables, so any of the three moving makes them stale.</summary>
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
