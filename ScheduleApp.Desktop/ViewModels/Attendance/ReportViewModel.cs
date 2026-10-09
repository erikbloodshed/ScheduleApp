using System.Collections.Specialized;
using System.Reactive.Linq;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs the Summary tab's report and "Export Summary…" -- runs the
/// schedule-vs-punches comparison for a period entirely from ScheduleApp's own database
/// (schedule, employees, and punches all come from it -- see ScheduleDbAttendanceRunner)
/// and holds the result in memory; Export Summary… then writes whatever's currently held
/// out to wherever the person chooses. Generating a report never reads the ZKTeco .dat
/// file itself or talks to the device -- punches need to already be on file via
/// AttendanceImportViewModel or DeviceFetchViewModel first. Scoped by whichever employees
/// are checked in ReportScopeViewModel's tree.
///
/// There's no explicit "Generate Reports" action -- a run fires on its own whenever
/// something it depends on changes while the Summary is showing: a Period date edit, a
/// report-scope tree check/uncheck, or the punches/schedule/pairings underneath (see
/// TryAutoRun); and when the Summary page is (re)visited after something changed elsewhere
/// (see OnIsSummaryTabSelectedChanged and RecheckOnPageRevisit).</summary>
public partial class ReportViewModel : ViewModelBase
{
    private readonly IAttendanceRunner _attendanceRunner;
    private readonly AttendancePolicy _policy;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly ReportScopeViewModel _reportScope;
    private readonly Action _saveViewState;
    private readonly AttendanceTabActivationGate _tabActivationGate;

    /// <summary>Backs the Summary grid's own right-click "Add Manual Entry…" -- the exact same
    /// save path the calendar's right-click and the Manual Entries page's Add use, the same
    /// instance AttendanceViewModel hands everyone (and the same shared busy state
    /// underneath).</summary>
    private readonly ManualEntryEditorViewModel _manualEntryEditor;

    /// <summary>Backs the Summary grid's own right-click "Edit Punch Pairing…" -- one launcher
    /// serves both this grid and the Schedule page's calendar tiles, so neither has to grow
    /// the four repositories the editor needs to load a day.</summary>
    private readonly IDayPunchPairingEditorLauncher _pairingLauncher;

    /// <summary>The most recent report, held in memory so Export Summary… can write it on
    /// demand without re-running the comparison. Null until a run completes, and dropped when
    /// a run for a different period fails, so a stale result can't be exported under a new
    /// period's label.</summary>
    private AttendanceRunResult? _lastResult;

    /// <summary>The period/scope/data-version combination _lastResult was computed for, as of
    /// the last successful run -- null until then. Compared against the current state to
    /// decide whether an auto-reload would pick up anything new (ShouldAutoReload), and, on a
    /// failed run, whether its period still matches what's on screen. Only ever written on
    /// success -- a failed run doesn't change what _lastResult is still valid for.</summary>
    private (DateTime? Start, DateTime? End, int SelectionVersion, int DeviceLogsVersion, int ManualLogsVersion, int ScheduleVersion, int PairingVersion)? _loadedSnapshot;

    /// <summary>Set while a deferred auto-run is queued (see TryAutoRun) -- so every flip in a
    /// synchronous bulk selection change (a department checkbox, Select All) queues one run
    /// between them, not one each.</summary>
    private bool _autoRunPending;

    /// <summary>A change that wanted an auto-run while something else was running -- replayed
    /// once that finishes. Only a change that actually arrived mid-run is replayed: replaying
    /// unconditionally made a run that kept failing (a database that's down) reschedule
    /// itself forever, hundreds of times a second.</summary>
    private bool _autoRunDeferred;

    private readonly IObservable<bool> _notBusy;
    private readonly IObservable<bool> _canUseResult;
    private readonly IObservable<bool> _canRun;
    private readonly IObservable<bool> _canRefreshOrCancel;

    public ReportViewModel(
        IAttendanceRunner attendanceRunner,
        IStatusBarService statusBarService,
        AttendancePolicy policy,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        ReportScopeViewModel reportScope,
        DateTime? initialPeriodStart,
        DateTime? initialPeriodEnd,
        Action saveViewState,
        AttendanceTabActivationGate tabActivationGate,
        ManualEntryEditorViewModel manualEntryEditor,
        IDayPunchPairingEditorLauncher pairingLauncher)
        : base(statusBarService)
    {
        _attendanceRunner = attendanceRunner;
        _policy = policy;
        _busy = busy;
        _dataVersion = dataVersion;
        _reportScope = reportScope;
        _saveViewState = saveViewState;
        _tabActivationGate = tabActivationGate;
        _manualEntryEditor = manualEntryEditor;
        _pairingLauncher = pairingLauncher;

        PeriodStart = initialPeriodStart;
        PeriodEnd = initialPeriodEnd;

        // The grid shows SummaryRows narrowed to whoever's checked in the scope tree and the
        // selected status tile -- without touching SummaryRows itself, which Export Summary…
        // always writes whole.
        SummaryRowsView = new FilteredCollection<AttendanceSummaryRow>(SummaryRows)
        {
            Filter = item => item is AttendanceSummaryRow row
                && _reportScope.IsChecked(row.EmployeeId)
                && (SelectedStatusFilter is null || row.Status == SelectedStatusFilter),
        };

        var rowCount = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => SummaryRows.CollectionChanged += handler,
                handler => SummaryRows.CollectionChanged -= handler)
            .Select(_ => SummaryRows.Count)
            .StartWith(0);
        _hasSummaryRowsHelper = Observable.CombineLatest(this.WhenAnyValue(x => x.HasResults), rowCount,
                (hasResults, rows) => hasResults && rows > 0)
            .ToProperty(this, x => x.HasSummaryRows);

        _refreshOrCancelGlyphHelper = _busy.WhenAnyValue(b => b.IsVisiblyRunning)
            .Select(running => running ? "" : "")
            .ToProperty(this, x => x.RefreshOrCancelGlyph);
        _refreshOrCancelToolTipHelper = _busy.WhenAnyValue(b => b.IsVisiblyRunning)
            .Select(running => running
                ? "Stop whatever's currently running."
                : "Re-run this report -- useful after editing the schedule elsewhere.")
            .ToProperty(this, x => x.RefreshOrCancelToolTip);

        _notBusy = _busy.WhenAnyValue(b => b.IsRunning).Select(running => !running);
        _canUseResult = Observable.CombineLatest(_notBusy, this.WhenAnyValue(x => x.HasResults), (idle, hasResults) => idle && hasResults);

        // Something to run for: the whole company when the tree's empty, otherwise at least
        // one employee checked.
        _canRun = Observable.CombineLatest(_notBusy,
            _reportScope.WhenAnyValue(s => s.TotalEmployeeCount, s => s.SelectedEmployeeCount,
                (total, selected) => total == 0 || selected > 0),
            (idle, hasScope) => idle && hasScope);

        // Always fine to try to cancel; otherwise only when a run could start -- not disabled
        // during a run something else started, at exactly the moment the button reads Cancel.
        _canRefreshOrCancel = Observable.CombineLatest(_busy.WhenAnyValue(b => b.IsVisiblyRunning), _canRun,
            (visiblyRunning, canRun) => visiblyRunning || canRun);

        ReportFailuresOf(PreviousPeriodCommand, NextPeriodCommand, ExportSummaryCommand, ShowStatusDetailCommand,
            ClearStatusFilterCommand, ShowOrphanedDetailCommand, ShowUnscheduledDetailCommand, AddManualEntryForRowCommand,
            EditPunchPairingForRowCommand, RefreshSummaryCommand, RefreshOrCancelSummaryCommand);

        this.WhenAnyValue(x => x.SelectedStatusFilter).Skip(1).Subscribe(_ => SummaryRowsView.Refresh());
        this.WhenAnyValue(x => x.PeriodStart, x => x.PeriodEnd)
            .Skip(1)
            .Subscribe(_ =>
            {
                _saveViewState();
                TryAutoRun();
            });
        this.WhenAnyValue(x => x.IsSummaryTabSelected).Skip(1).Subscribe(OnIsSummaryTabSelectedChanged);

        // Every change to what's checked, including one that keeps the count the same: the
        // grid narrows or widens at once over what's already fetched, and a run catches the
        // counts up.
        _reportScope.SelectionChanges.Subscribe(_ =>
        {
            SummaryRowsView.Refresh();
            TryAutoRun();
        });

        // New punches, a manual entry, a schedule or pairing edit -- what the report shows
        // changed underneath it.
        _dataVersion.WhenAnyValue(v => v.DeviceLogsVersion, v => v.ManualLogsVersion, v => v.ScheduleVersion, v => v.PairingVersion,
                (device, manual, schedule, pairing) => (device, manual, schedule, pairing))
            .Skip(1)
            .Subscribe(_ => TryAutoRun());

        _busy.WhenAnyValue(b => b.IsRunning)
            .Skip(1)
            .Where(running => !running)
            .Subscribe(_ =>
            {
                if (!_autoRunDeferred) return;
                _autoRunDeferred = false;
                TryAutoRun();
            });
    }

    /// <summary>What the grid binds to -- see the constructor.</summary>
    public FilteredCollection<AttendanceSummaryRow> SummaryRowsView { get; }

    /// <summary>The status tile currently narrowing the grid, or null for every status -- a
    /// client-side view filter only (see ShowStatusDetail).</summary>
    [Reactive]
    public partial PunchStatus? SelectedStatusFilter { get; private set; }

    [Reactive]
    public partial DateTime? PeriodStart { get; set; }

    [Reactive]
    public partial DateTime? PeriodEnd { get; set; }

    /// <summary>Whether the Summary page is the one showing.</summary>
    [Reactive]
    public partial bool IsSummaryTabSelected { get; set; }

    /// <summary>A report is held (see _lastResult).</summary>
    [Reactive]
    public partial bool HasResults { get; private set; }

    [ObservableAsProperty]
    public partial bool HasSummaryRows { get; }

    [Reactive]
    public partial int TotalLogs { get; private set; }

    [Reactive]
    public partial int CompleteCount { get; private set; }

    [Reactive]
    public partial int PartialCount { get; private set; }

    [Reactive]
    public partial int AbsentCount { get; private set; }

    [Reactive]
    public partial int LeaveCount { get; private set; }

    [Reactive]
    public partial int OfficialBusinessCount { get; private set; }

    [Reactive]
    public partial int RestDayCount { get; private set; }

    /// <summary>Punches near a schedule's window but not picked as its clock-in/out (see
    /// AttendanceRunResult.OrphanedPunches) -- distinct from UnscheduledCount, which never came
    /// near any schedule at all.</summary>
    [Reactive]
    public partial int OrphanedCount { get; private set; }

    /// <summary>Punches no schedule entry this run even considered (see
    /// AttendanceRunResult.UnscheduledPunches).</summary>
    [Reactive]
    public partial int UnscheduledCount { get; private set; }

    /// <summary>Replaced in one change per run (see RunCoreAsync).</summary>
    public RangeObservableCollection<AttendanceSummaryRow> SummaryRows { get; } = [];

    /// <summary>Segoe Fluent Icons Refresh, or Cancel while anything on the Attendance page is
    /// visibly running -- the Period row's icon button (see RefreshOrCancelSummary).</summary>
    [ObservableAsProperty(InitialValue = "")]
    public partial string RefreshOrCancelGlyph { get; }

    /// <summary>Generic on purpose, not "Stop this report": IsVisiblyRunning can be true because
    /// of anything on the Attendance page, and there's no telling which from here.</summary>
    [ObservableAsProperty]
    public partial string RefreshOrCancelToolTip { get; }

    /// <summary>The Period row's ◀ button: steps to the previous semi-monthly cut-off (see
    /// AttendancePeriodNavigation.AdjacentCutoffPeriod). Both dates change one after the
    /// other, and TryAutoRun's deferral coalesces them into one run.</summary>
    [ReactiveCommand]
    private void PreviousPeriod() => StepPeriod(forward: false);

    /// <summary>The ▶ button -- see PreviousPeriod.</summary>
    [ReactiveCommand]
    private void NextPeriod() => StepPeriod(forward: true);

    private void StepPeriod(bool forward)
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(PeriodStart ?? PeriodEnd ?? DateTime.Today, forward);
        PeriodStart = start;
        PeriodEnd = end;
    }

    /// <summary>Saves the summary to Excel, then opens it.</summary>
    [ReactiveCommand(CanExecute = nameof(_canUseResult))]
    private async Task ExportSummaryAsync()
    {
        if (await PickFileToSaveAsync("Excel Workbook (*.xlsx)|*.xlsx", $"Attendance_Summary_{DateTime.Now:MMddyy}.xlsx",
                "Save Attendance Summary") is not { } path)
        {
            return;
        }

        try
        {
            AttendanceExcelExporter.ExportSummaryToExcel(path, _lastResult!.Summaries, _policy);
            await OpenFileAsync(path);
            StatusBar.ShowSuccess($"Saved attendance summary to {path}.");
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    /// <summary>A status tile in the counts strip: narrows the grid to that status in place,
    /// a second click on the same tile shows every status again, and a click on another tile
    /// switches straight to it.</summary>
    [ReactiveCommand(CanExecute = nameof(_canUseResult))]
    private void ShowStatusDetail(PunchStatus status) =>
        SelectedStatusFilter = SelectedStatusFilter == status ? null : status;

    /// <summary>"Clear Filter" -- the same as clicking the active tile again.</summary>
    [ReactiveCommand]
    private void ClearStatusFilter() => SelectedStatusFilter = null;

    /// <summary>The Orphaned tile: lists the punches near a schedule but not picked as its
    /// clock-in/out.</summary>
    [ReactiveCommand(CanExecute = nameof(_canUseResult))]
    private async Task ShowOrphanedDetailAsync() =>
        await ShowDialogAsync(new PunchListDetailViewModel("Orphaned", _lastResult!.OrphanedPunches, _lastResult.Employees));

    /// <summary>The Unscheduled tile: lists the punches no schedule entry even
    /// considered.</summary>
    [ReactiveCommand(CanExecute = nameof(_canUseResult))]
    private async Task ShowUnscheduledDetailAsync() =>
        await ShowDialogAsync(new PunchListDetailViewModel("Unscheduled", _lastResult!.UnscheduledPunches, _lastResult.Employees));

    /// <summary>The Summary grid's right-click "Add Manual Entry…" for a Partial or Absent row
    /// (the same restriction the calendar tiles' item has). The row only carries the PIN, so
    /// the employee is resolved from the run that produced it -- every employee it covered,
    /// not just those with punches, so an Absent row resolves like a Partial one. A miss (the
    /// PIN cleared elsewhere since) fails soft. No busy wrap of its own: the editor's own run
    /// serializes it, and the data-version bump it makes re-runs the report.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private Task AddManualEntryForRowAsync(AttendanceSummaryRow row) =>
        ResolveEmployee(row) is { } employee
            ? _manualEntryEditor.AddManualEntryForDayAsync(employee, DateOnly.FromDateTime(row.ShiftDate))
            : Task.CompletedTask;

    /// <summary>The Summary grid's right-click "Edit Punch Pairing…"/"View Punches…": that day's
    /// punches in the Day Punch Pairing editor -- editable for a Flexible row, or a Partial/Absent
    /// row of any type; read-only otherwise (the row's Status tells the launcher which). Wrapped
    /// in the busy state, unlike Add Manual Entry: the launcher reads and writes the shared
    /// ScheduleDbContext with no busy state of its own. visibly: false while the dialog is open
    /// -- none of that is time the person is waiting on the app. The pairing bump it makes
    /// re-runs the report.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task EditPunchPairingForRowAsync(AttendanceSummaryRow row)
    {
        if (ResolveEmployee(row) is not { } employee) return;

        await _busy.RunAsync(visibly: false,
            ct => _pairingLauncher.OpenAsync(employee, DateOnly.FromDateTime(row.ShiftDate), row.Status, ShowDialog, ct),
            onError: ex => ShowFailure(ex));
    }

    private Core.Models.Employee? ResolveEmployee(AttendanceSummaryRow row)
    {
        if (_lastResult?.Employees.FirstOrDefault(e => e.Pin == row.EmployeeId) is { } employee)
            return employee;

        StatusBar.ShowCaution(
            $"Couldn't find {row.EmployeeName} in the current summary. Refresh Summary and try again.",
            "Employee not found");
        return null;
    }

    /// <summary>Switching to the Summary page reloads it, silently, if anything it shows has
    /// changed since (see ShouldAutoReload) -- not otherwise, so an ordinary revisit leaves
    /// the grid, and its scroll position, alone.</summary>
    private void OnIsSummaryTabSelectedChanged(bool selected)
    {
        if (!_tabActivationGate.IsReady) return;

        if (selected && CanRun() && ShouldAutoReload())
            _ = RunCoreAsync(showFeedback: false);

        _saveViewState();
    }

    /// <summary>Whether a reload would show anything new: the period, the scope selection, or
    /// the punches/manual entries/schedule/pairings underneath have changed since the report
    /// last loaded -- or nothing has loaded yet. A schedule edit counts even though neither
    /// punch table moved: it changes what the report should show.</summary>
    private bool ShouldAutoReload() =>
        _loadedSnapshot != (PeriodStart, PeriodEnd, _reportScope.SelectionVersion,
            _dataVersion.DeviceLogsVersion, _dataVersion.ManualLogsVersion, _dataVersion.ScheduleVersion,
            _dataVersion.PairingVersion);

    /// <summary>An unconditional, visible re-run -- for after a change no trigger reports, such
    /// as a schedule edit on the Schedule page while the Summary was already the page showing
    /// here.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRun))]
    private Task RefreshSummaryAsync() => RunCoreAsync(showFeedback: true);

    /// <summary>The Period row's one icon button, toggling in place: Cancel while anything on
    /// the Attendance page is visibly running, Refresh otherwise -- rather than a separate
    /// Cancel bar that shifted the page down while it showed. Synchronous, so it stays enabled,
    /// as Cancel, while the run it starts goes.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRefreshOrCancel))]
    private void RefreshOrCancelSummary()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            _ = RunCoreAsync(showFeedback: true);
    }

    /// <summary>Called on every visit to the Summary page: a schedule edit made on the Schedule
    /// page never toggles IsSummaryTabSelected if the Summary was already showing before the
    /// person navigated away, so this is the trigger that notices it. Silent, and a no-op when
    /// nothing relevant changed.</summary>
    internal void RecheckOnPageRevisit()
    {
        if (!_tabActivationGate.IsReady) return;

        if (CanRun() && ShouldAutoReload())
            _ = RunCoreAsync(showFeedback: false);
    }

    /// <summary>
    /// A visible re-run for a change to what the report covers -- a Period edit, a scope check,
    /// or the data underneath -- while the Summary is showing; otherwise the next visit's
    /// recheck picks it up. A change that lands while something else is running is replayed
    /// once that finishes (see _autoRunDeferred).
    ///
    /// Deferred until the current work settles (UiDispatch.AfterCurrentWork) rather than
    /// started inline, so a bulk selection change gets exactly one run, scoped to the final
    /// selection, instead of one per flip with the grid rebuilding twice -- and so the
    /// checkboxes repaint before the run's progress bar appears. GetSelectedPins() is read
    /// inside the run, not here, so it sees the settled selection.
    /// </summary>
    private void TryAutoRun()
    {
        if (!_tabActivationGate.IsReady || !IsSummaryTabSelected) return;
        if (!ShouldAutoReload()) return;
        if (_busy.IsRunning)
        {
            _autoRunDeferred = true;
            return;
        }

        if (!CanRun() || _autoRunPending) return;

        _autoRunPending = true;
        UiDispatch.AfterCurrentWork(() =>
        {
            _autoRunPending = false;

            // Re-checked: a run may already have started, or a further change made this one
            // redundant, while this was queued.
            if (_busy.IsRunning)
                _autoRunDeferred = true;
            else if (CanRun() && ShouldAutoReload())
                _ = RunCoreAsync(showFeedback: true);
        });
    }

    private async Task RunCoreAsync(bool showFeedback)
    {
        var validationErrors = Validate();
        if (validationErrors.Count > 0)
        {
            if (showFeedback)
                StatusBar.ShowCaution(string.Join(" ", validationErrors));
            return;
        }

        // Only a change the person made brings up the progress bar -- not the silent reload on
        // a revisit. A cancelled run leaves whatever was on screen as it was.
        await _busy.RunAsync(visibly: showFeedback, async cancellationToken =>
        {
            var progress = new Progress<string>(message =>
            {
                if (showFeedback)
                    StatusBar.ShowInfo(message);
            });

            var request = new AttendanceRunRequest
            {
                Policy = _policy,
                PeriodStart = DateOnly.FromDateTime(PeriodStart!.Value),
                PeriodEnd = DateOnly.FromDateTime(PeriodEnd!.Value),
                TargetPins = _reportScope.GetSelectedPins(),
            };

            // Captured with the request, before the await: a checkbox flipped while this runs
            // still bumps SelectionVersion, and stamping the version read *after* would claim
            // the grid reflects a selection the request never covered -- so the catch-up
            // re-run would never happen.
            var requestSnapshot = (PeriodStart, PeriodEnd, _reportScope.SelectionVersion,
                _dataVersion.DeviceLogsVersion, _dataVersion.ManualLogsVersion, _dataVersion.ScheduleVersion,
                _dataVersion.PairingVersion);

            var result = await _attendanceRunner.RunAsync(request, progress, cancellationToken);
            _lastResult = result;

            TotalLogs = result.RawLogs.Count;
            var dayStatuses = AttendanceDayStatus.ByDay(result.Summaries).ToList();
            CompleteCount = dayStatuses.Count(s => s == PunchStatus.Complete);
            PartialCount = dayStatuses.Count(s => s == PunchStatus.Partial);
            AbsentCount = dayStatuses.Count(s => s == PunchStatus.Absent);
            LeaveCount = dayStatuses.Count(s => s == PunchStatus.Leave);
            OfficialBusinessCount = dayStatuses.Count(s => s == PunchStatus.OfficialBusiness);
            RestDayCount = dayStatuses.Count(s => s == PunchStatus.RestDay);
            OrphanedCount = result.OrphanedPunches.Count;
            UnscheduledCount = result.UnscheduledPunches.Count;

            // Swapped in once the new rows are ready, with HasResults staying true throughout,
            // so the grid and counts never collapse and reappear for an ordinary refresh.
            SummaryRows.ReplaceAll(AttendanceSummaryRow.BuildRows(result.Summaries));
            HasResults = true;
            _loadedSnapshot = requestSnapshot;
            _saveViewState();

            if (showFeedback)
                StatusBar.ShowSuccess(
                    $"Report generated: {CompleteCount} complete, {PartialCount} partial, " +
                    $"{AbsentCount} absent, {LeaveCount} leave, {OfficialBusinessCount} official business, " +
                    $"{RestDayCount} rest day.",
                    "Report ready");
        },
        onError: ex =>
        {
            // A same-period refresh can fail transiently without blanking results still valid
            // for the period on screen; but a failed run for a *different* period drops them,
            // or Export would offer the previous period's data under the new label.
            var periodChanged = _loadedSnapshot is null
                || _loadedSnapshot.Value.Start != PeriodStart
                || _loadedSnapshot.Value.End != PeriodEnd;

            if (periodChanged)
            {
                _lastResult = null;
                HasResults = false;
                SummaryRows.Clear();
            }

            if (showFeedback)
                ShowFailure(ex);
        });
    }

    /// <summary>Whether a run could start now -- AttendanceViewModel's initial-page check reads
    /// it too.</summary>
    internal bool CanRun() =>
        !_busy.IsRunning && (_reportScope.TotalEmployeeCount == 0 || _reportScope.SelectedEmployeeCount > 0);

    private List<string> Validate()
    {
        var errors = new List<string>();

        if (PeriodStart is null || PeriodEnd is null)
            errors.Add("⚠ Select both a period start and end date.");
        else if (PeriodStart > PeriodEnd)
            errors.Add("⚠ Period start must not be after period end.");

        if (_reportScope.TotalEmployeeCount > 0 && _reportScope.SelectedEmployeeCount == 0)
            errors.Add("⚠ Select at least one employee in Report Scope, or use \"Select All\" for the whole company.");

        return errors;
    }
}
