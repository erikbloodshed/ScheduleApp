using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.Views;
using System.ComponentModel;

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
/// There's no explicit "Generate Reports" action anymore -- a run instead fires on its
/// own whenever something it depends on changes: a Period date edit or a report-scope
/// tree check/uncheck (see TryAutoRun), or this page being (re)selected -- including its
/// own first-ever navigation, which OnIsSummaryTabSelectedChanged's ShouldAutoReload
/// check already treats as "stale" since nothing has loaded yet -- after something
/// changed elsewhere (see OnIsSummaryTabSelectedChanged and
/// AttendanceViewModel.ActivateSummaryTab).</summary>
public partial class ReportViewModel : ObservableObject
{
    private readonly IAttendanceRunner _attendanceRunner;
    private readonly IStatusBarService _statusBarService;
    private readonly AttendancePolicy _policy;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly ReportScopeViewModel _reportScope;
    private readonly Action _saveViewState;
    private readonly AttendanceTabActivationGate _tabActivationGate;

    /// <summary>Backs the Summary grid's own right-click "Add Manual Entry…" (see
    /// AddManualEntryForRowAsync below) -- the exact same Add/BumpManualLogs/
    /// status-message/Manual-Entries-grid-refresh path the calendar's right-click and
    /// the Attendance tab's own Add Manual Entry button already use, injected here
    /// rather than `new()`'d so AttendanceViewModel hands this class the exact same
    /// instance it hands ManualEntryEditor itself (same reasoning, and same shared
    /// _busy instance underneath, as AttendanceDataVersion above).</summary>
    private readonly ManualEntryEditorViewModel _manualEntryEditor;

    /// <summary>Backs the Summary grid's own right-click "Edit Punch Pairing…" (see
    /// EditPunchPairingForRowAsync below). Same shared-collaborator shape as
    /// _manualEntryEditor above -- one launcher instance serves both this grid and the
    /// Schedule page's calendar tiles (see MainViewModel), so neither has to grow the
    /// four repositories the editor needs to load a day.</summary>
    private readonly IDayPunchPairingEditorLauncher _pairingLauncher;

    /// <summary>The most recent Generate Reports result, held in memory so Export
    /// Summary… can write from it on demand without re-running the comparison. Null until
    /// a run completes, and cleared at the start of a new run so a stale result can't be
    /// exported under a new period's label if the new run fails partway through.</summary>
    private AttendanceRunResult? _lastResult;

    /// <summary>The period/scope/data-version combination _lastResult was actually
    /// computed for, as of the last time a run succeeded -- null until the first success.
    /// Two separate jobs: (1) RunCoreAsync's catch block compares just the Start/End
    /// fields against the *current* PeriodStart/PeriodEnd to decide whether a failed run
    /// is a same-period refresh (keep showing the still-valid _lastResult) or a
    /// different-period failure (drop it -- see that catch block's own comment); (2)
    /// ShouldAutoReload compares the whole tuple to decide whether OnIsSummaryTabSelectedChanged's
    /// silent auto-reload needs to actually re-run at all. Deliberately only ever written
    /// from the success path, never the catch block -- a failed run doesn't change what
    /// _lastResult is still valid for.</summary>
    private (DateTime? Start, DateTime? End, int SelectionVersion, int DeviceLogsVersion, int ManualLogsVersion, int ScheduleVersion, int PairingVersion)? _loadedSnapshot;

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
    {
        _attendanceRunner = attendanceRunner;
        _statusBarService = statusBarService;
        _policy = policy;
        _busy = busy;
        _dataVersion = dataVersion;
        _reportScope = reportScope;
        _saveViewState = saveViewState;
        _tabActivationGate = tabActivationGate;
        _manualEntryEditor = manualEntryEditor;
        _pairingLauncher = pairingLauncher;

        // Set via the property (not the backing field) deliberately -- this does fire
        // OnPeriodStartChanged/OnPeriodEndChanged below, but SaveViewState is a no-op
        // until ReportScopeViewModel.HasRestoredScope flips true, which it isn't yet at
        // construction time, so this can't clobber a saved view state with these
        // just-computed defaults.
        PeriodStart = initialPeriodStart;
        PeriodEnd = initialPeriodEnd;

        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsRunning))
            {
                ExportSummaryCommand.NotifyCanExecuteChanged();
                ShowStatusDetailCommand.NotifyCanExecuteChanged();
                ShowOrphanedDetailCommand.NotifyCanExecuteChanged();
                ShowUnscheduledDetailCommand.NotifyCanExecuteChanged();
                RefreshSummaryCommand.NotifyCanExecuteChanged();
                RefreshOrCancelSummaryCommand.NotifyCanExecuteChanged();
                AddManualEntryForRowCommand.NotifyCanExecuteChanged();
                EditPunchPairingForRowCommand.NotifyCanExecuteChanged();

                // Picks up a date/tree change that arrived while a previous run was
                // still in flight -- TryAutoRun's own CanRun() check blocks a second
                // concurrent run outright, so that change's own call into TryAutoRun
                // (from OnPeriodStartChanged/OnPeriodEndChanged, or the ReportScope
                // handler below) was a no-op at the time. Checking again the moment
                // IsRunning drops back to false is what stops that change from just
                // going stale until something unrelated (e.g. a tab switch) happens to
                // trigger a reload. ShouldAutoReload() inside TryAutoRun is what stops
                // this from re-running pointlessly every time a run finishes normally
                // (the just-finished run already brought _loadedSnapshot up to date).
                if (!_busy.IsRunning)
                    TryAutoRun();
            }
            else if (e.PropertyName == nameof(AttendanceBusyState.IsVisiblyRunning))
            {
                // See RefreshOrCancelGlyph/RefreshOrCancelToolTip's own doc comment --
                // this is what actually flips the Period row's icon button between
                // Refresh and Cancel the moment anything on the Attendance page starts
                // or stops being visibly busy, not just a click on this button itself.
                OnPropertyChanged(nameof(RefreshOrCancelGlyph));
                OnPropertyChanged(nameof(RefreshOrCancelToolTip));
                RefreshOrCancelSummaryCommand.NotifyCanExecuteChanged();
            }
        };

        SummaryRowsView = CollectionViewSource.GetDefaultView(SummaryRows);
        SummaryRowsView.Filter = FilterSummaryRow;

        // Reacts to every check/uncheck in the report-scope tree (both of these still
        // raise SelectedEmployeeCount's own change notification on each flip, plus once
        // more on a tree (re)load -- see LoadEmployeeTreeAsync and
        // OnEmployeeNodeSelectionChanged) two ways: SummaryRowsView.Refresh() re-applies
        // FilterSummaryRow immediately against whatever's already been fetched, so the
        // grid narrows/widens the instant a box is (un)checked without waiting on a
        // fresh run at all; TryAutoRun() then re-runs the report itself, so the counts
        // strip and everything else that isn't just a client-side filter over
        // SummaryRows also catches up. Wired here (after SummaryRowsView exists), not up
        // where the rest of this constructor's early setup happens, since Refresh()
        // needs a real view to call it on.
        _reportScope.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReportScopeViewModel.SelectedEmployeeCount)
                or nameof(ReportScopeViewModel.TotalEmployeeCount))
            {
                SummaryRowsView.Refresh();
                TryAutoRun();
                RefreshSummaryCommand.NotifyCanExecuteChanged();
                RefreshOrCancelSummaryCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>The DataGrid binds to this instead of SummaryRows directly, so
    /// unchecking an employee/department in the report-scope tree (see FilterSummaryRow)
    /// hides those rows without touching the underlying data (needed intact for Export
    /// Summary…, which always exports everything regardless of what's currently filtered
    /// on-screen). Same story for SelectedStatusFilter below -- clicking a status tile
    /// narrows this view too, without touching SummaryRows itself.</summary>
    public ICollectionView SummaryRowsView { get; }

    private bool FilterSummaryRow(object obj) =>
        obj is AttendanceSummaryRow row
        && _reportScope.IsChecked(row.EmployeeId)
        && (SelectedStatusFilter is null || row.Status == SelectedStatusFilter);

    /// <summary>Which status tile in the counts strip is currently narrowing
    /// SummaryRowsView, or null when every status is showing -- see
    /// ShowStatusDetail/ClearStatusFilter below and FilterSummaryRow above. Purely a
    /// client-side view filter, same idea as _reportScope's own filter dimension: it
    /// never touches SummaryRows itself, so Export Summary… (which always reads
    /// _lastResult.Summaries, not the view) is unaffected by whatever's currently
    /// selected here.</summary>
    [ObservableProperty]
    private PunchStatus? selectedStatusFilter;

    partial void OnSelectedStatusFilterChanged(PunchStatus? value) => SummaryRowsView.Refresh();

    [ObservableProperty]
    private DateTime? periodStart;

    partial void OnPeriodStartChanged(DateTime? value)
    {
        _saveViewState();
        TryAutoRun();
    }

    [ObservableProperty]
    private DateTime? periodEnd;

    partial void OnPeriodEndChanged(DateTime? value)
    {
        _saveViewState();
        TryAutoRun();
    }

    /// <summary>Backs the Period row's "◀"/"▶" buttons -- steps PeriodStart/PeriodEnd to
    /// the adjacent semi-monthly cut-off on whichever side of the currently-set period
    /// the direction points (see AttendancePeriodNavigation.AdjacentCutoffPeriod, shared
    /// with PunchRecordsViewModel/ManualEntriesViewModel's own identical buttons).
    /// Reference is PeriodStart (falling back to PeriodEnd, then today, only if
    /// PeriodStart is somehow unset).
    ///
    /// Sets both properties one after another rather than computing-then-assigning a
    /// tuple in one shot -- each assignment still fires its own OnPeriodStartChanged/
    /// OnPeriodEndChanged (so SaveViewState/TryAutoRun behave exactly as if the person had
    /// edited each DatePicker by hand), but TryAutoRun's own _autoRunPending guard
    /// coalesces the pair into a single deferred run, same as a bulk report-scope
    /// selection change already does -- see that field's doc comment.</summary>
    [RelayCommand]
    private void PreviousPeriod()
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(PeriodStart ?? PeriodEnd ?? DateTime.Today, forward: false);
        PeriodStart = start;
        PeriodEnd = end;
    }

    /// <summary>See PreviousPeriodCommand's doc comment -- same step, the other
    /// direction.</summary>
    [RelayCommand]
    private void NextPeriod()
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(PeriodStart ?? PeriodEnd ?? DateTime.Today, forward: true);
        PeriodStart = start;
        PeriodEnd = end;
    }

    [ObservableProperty]
    private bool hasResults;

    partial void OnHasResultsChanged(bool value)
    {
        ExportSummaryCommand.NotifyCanExecuteChanged();
        ShowStatusDetailCommand.NotifyCanExecuteChanged();
        ShowOrphanedDetailCommand.NotifyCanExecuteChanged();
        ShowUnscheduledDetailCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSummaryRows));
    }

    [ObservableProperty]
    private int totalLogs;

    [ObservableProperty]
    private int completeCount;

    [ObservableProperty]
    private int partialCount;

    [ObservableProperty]
    private int absentCount;

    [ObservableProperty]
    private int leaveCount;

    [ObservableProperty]
    private int officialBusinessCount;

    [ObservableProperty]
    private int restDayCount;

    /// <summary>Punches near a schedule's window but not picked as its
    /// clock-in/out (see AttendanceRunResult.OrphanedPunches) -- distinct from
    /// UnscheduledCount below, which never came near any schedule at all.</summary>
    [ObservableProperty]
    private int orphanedCount;

    /// <summary>Punches no schedule entry this run even considered (see
    /// AttendanceRunResult.UnscheduledPunches).</summary>
    [ObservableProperty]
    private int unscheduledCount;

    public ObservableCollection<AttendanceSummaryRow> SummaryRows { get; } = new();

    public bool HasSummaryRows => HasResults && SummaryRows.Count > 0;

    /// <summary>Saves the attendance summary to Excel, then opens it immediately --
    /// there's no separate "Open" button anymore (see this method's own doc history:
    /// it used to just stash the written path in OutputSummaryPath for a person to click
    /// Open afterward). Same Process.Start/UseShellExecute call that button used to make,
    /// just fired right after a successful write instead of waiting for a second click.</summary>
    [RelayCommand(CanExecute = nameof(CanExportSummary))]
    private void ExportSummary()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Attendance Summary",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"Attendance_Summary_{DateTime.Now:MMddyy}.xlsx",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            AttendanceExcelExporter.ExportSummaryToExcel(dialog.FileName, _lastResult!.Summaries, _policy);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });

            var message = $"Saved attendance summary to {dialog.FileName}.";
            _statusBarService.ShowSuccess(message);
        }
        catch (Exception ex)
        {
            _statusBarService.ShowError(ex.Message);
        }
    }

    private bool CanExportSummary() => !_busy.IsRunning && _lastResult is not null;

    /// <summary>Backs each clickable status tile in the counts strip (Complete/
    /// Partial/Absent/Leave/Official Business/Rest Day) -- rather than opening a
    /// separate read-only dialog pre-filtered to that status, this narrows
    /// SummaryRowsView (the same grid the tiles sit above) down to just that one
    /// status in place, toggling back to every status on a second click of the
    /// same tile. A click on a *different* tile while one is already active just
    /// swaps the filter straight to the new status, same one-click feel as
    /// picking a different radio option rather than needing to clear the old one
    /// first. See SelectedStatusFilter/FilterSummaryRow for the actual
    /// filtering.</summary>
    [RelayCommand(CanExecute = nameof(CanShowStatusDetail))]
    private void ShowStatusDetail(PunchStatus status) =>
        SelectedStatusFilter = SelectedStatusFilter == status ? null : status;

    /// <summary>Backs the "Clear Filter" button that appears next to Refresh/Export
    /// Summary… once a status tile has narrowed the grid -- same effect as clicking
    /// the active tile a second time (see ShowStatusDetail above), just reachable
    /// without having to find and re-click that exact tile again.</summary>
    [RelayCommand]
    private void ClearStatusFilter() => SelectedStatusFilter = null;

    private bool CanShowStatusDetail() => !_busy.IsRunning && _lastResult is not null;

    /// <summary>Backs the Orphaned tile -- opens a read-only dialog listing
    /// every punch that fell within some schedule entry's buffer window but
    /// wasn't picked as its clock-in/out (see
    /// AttendanceRunResult.OrphanedPunches).</summary>
    [RelayCommand(CanExecute = nameof(CanShowStatusDetail))]
    private void ShowOrphanedDetail() => ShowPunchListDetail("Orphaned", _lastResult!.OrphanedPunches);

    /// <summary>Backs the Unscheduled tile -- same idea as ShowOrphanedDetail,
    /// but for AttendanceRunResult.UnscheduledPunches (punches no schedule
    /// entry this run even considered).</summary>
    [RelayCommand(CanExecute = nameof(CanShowStatusDetail))]
    private void ShowUnscheduledDetail() => ShowPunchListDetail("Unscheduled", _lastResult!.UnscheduledPunches);

    /// <summary>Shared by both commands above -- builds the same
    /// StoredPunchLogRow shape (and via the same factory) the Punch Records
    /// and Manual Entries tabs already use for a raw punch, rather than
    /// inventing a new row type just for this dialog.</summary>
    private void ShowPunchListDetail(string title, List<AttendanceLog> punches)
    {
        var employeeInfo = StoredPunchLogRowFactory.BuildEmployeeInfoByPin(_lastResult!.Employees);
        var rows = punches
            .OrderBy(p => p.Timestamp)
            .Select(p => StoredPunchLogRowFactory.BuildRow(p, employeeInfo))
            .ToList();

        var dialog = new PunchListDetailDialog(title, rows, punches, _lastResult!.Employees)
        {
            Owner = Application.Current.MainWindow,
        };
        dialog.ShowDialog();
    }

    /// <summary>Bound to the Summary grid's own right-click "Add Manual Entry…" (see
    /// AttendanceView.xaml.cs's SummaryRow_MouseRightButtonDown, which only ever builds
    /// that item for a row whose Status is Partial or Absent -- same restriction, and
    /// same reasoning, as MonthCalendarControl.BuildDayContextMenu's identical item on
    /// the Schedule page's calendar tiles). row here is whichever row was
    /// right-clicked, not a grid-wide selection -- the Summary grid has no multi-select
    /// concept the way the calendar's Shift/Ctrl+click does, so there's no bulk case to
    /// consider.
    ///
    /// AttendanceSummaryRow only carries EmployeeId (the PIN), not the Employee object
    /// ManualLogEntryDialog's calendar-tile constructor needs, so it's resolved against
    /// _lastResult.Employees here -- the same lookup ShowPunchListDetail already does
    /// via StoredPunchLogRowFactory.BuildEmployeeInfoByPin, just for one PIN instead of
    /// building a lookup table. _lastResult.Employees is every employee the run
    /// actually covered (see AttendanceWorkflowService's allEmployees -- the whole
    /// roster when request.TargetPins was null/"everyone", or just the requested pins
    /// otherwise), not just ones with a punch on file, so an Absent row's employee
    /// resolves here exactly the same way a Partial row's does. A miss should be rare -- row itself only exists
    /// because this same _lastResult already produced a summary for that PIN -- but is
    /// still possible if the employee's PIN was cleared elsewhere (Edit Employee) in
    /// the gap between that run and this click, so it fails soft with a status message
    /// rather than throwing, the same shape as AddManualEntryForDayAsync's own
    /// SelectedEmployee.Pin check on the Schedule page.
    ///
    /// Deliberately does not wrap this call in its own _busy.RunAsync the way
    /// MainViewModel.AddManualEntryForDayAsync wraps its call into
    /// ManualEntryEditorViewModel -- MainViewModel has its own, separate
    /// AttendanceBusyState instance to hold open for the reasons that command's own doc
    /// comment explains, but this class already shares the one AttendanceViewModel
    /// constructs for the whole Attendance page with ManualEntryEditorViewModel itself
    /// (see _manualEntryEditor's own doc comment), so AddOrEditManualEntryAsync's own
    /// RunAsync call already serializes this against every other Attendance command --
    /// the same reason EditManualEntryAsync above needs no wrap of its own either.
    /// That shared instance is also what refreshes this grid afterward for free: this
    /// class's own _busy.PropertyChanged handler (in the constructor) already re-runs
    /// TryAutoRun() every time IsRunning drops back to false, and ShouldAutoReload
    /// picks up the ManualLogsVersion bump AddOrEditManualEntryAsync makes -- so unlike
    /// MainViewModel.AddManualEntryForDayAsync, there's no explicit
    /// RefreshCalendarAttendanceStatusesAsync-equivalent call to make here.</summary>
    private bool CanAddManualEntryForRow(AttendanceSummaryRow row) => !_busy.IsRunning;

    [RelayCommand(CanExecute = nameof(CanAddManualEntryForRow))]
    private Task AddManualEntryForRowAsync(AttendanceSummaryRow row)
    {
        var employee = _lastResult?.Employees.FirstOrDefault(e => e.Pin == row.EmployeeId);
        if (employee is null)
        {
            _statusBarService.ShowCaution(
                $"Couldn't find {row.EmployeeName} in the current summary. Refresh Summary and try again.",
                "Employee not found");
            return Task.CompletedTask;
        }

        return _manualEntryEditor.AddManualEntryForDayAsync(employee, DateOnly.FromDateTime(row.ShiftDate));
    }

    /// <summary>Backs the Summary grid's right-click "Edit Punch Pairing…" / "View
    /// Punches…" (see AttendanceSummaryView.xaml.cs) -- opens that day's punches in the
    /// Day Punch Pairing editor. Editable for a Flexible row (re-pair by hand, saved) or
    /// a Partial/Absent row of any type (add or correct the missing punch, which saves
    /// itself); read-only for any other non-Flexible row. row.Status is passed through so
    /// the launcher/editor can tell those apart.
    ///
    /// Resolves the Employee from _lastResult the same fail-soft way
    /// AddManualEntryForRowAsync above does, and for the same reason (AttendanceSummaryRow
    /// only carries the PIN). Wrapped in _busy.RunAsync unlike that method: the launcher
    /// does its own repository reads and writes against the shared, app-lifetime-scoped
    /// ScheduleDbContext, and -- unlike ManualEntryEditorViewModel -- has no RunAsync of
    /// its own to serialize them (it's deliberately UI-shaped rather than a ViewModel with
    /// a busy state, since MainViewModel drives it too, against a different one). visibly:
    /// false while the dialog is open, matching AddOrEditManualEntryAsync -- the person
    /// could sit on it for a while, and none of that is time they're waiting on the app.
    ///
    /// The Summary grid refreshes itself afterwards for free: the launcher bumps
    /// AttendanceDataVersion.PairingVersion, this class's own _busy.PropertyChanged
    /// handler re-runs TryAutoRun() when IsRunning drops back to false, and
    /// ShouldAutoReload now compares that counter (see _loadedSnapshot).</summary>
    private bool CanEditPunchPairingForRow(AttendanceSummaryRow row) => !_busy.IsRunning;

    [RelayCommand(CanExecute = nameof(CanEditPunchPairingForRow))]
    private async Task EditPunchPairingForRowAsync(AttendanceSummaryRow row)
    {
        var employee = _lastResult?.Employees.FirstOrDefault(e => e.Pin == row.EmployeeId);
        if (employee is null)
        {
            _statusBarService.ShowCaution(
                $"Couldn't find {row.EmployeeName} in the current summary. Refresh Summary and try again.",
                "Employee not found");
            return;
        }

        await _busy.RunAsync(visibly: false,
            ct => _pairingLauncher.OpenAsync(employee, DateOnly.FromDateTime(row.ShiftDate), row.Status, ct),
            onError: ex => _statusBarService.ShowError(ex.Message));
    }

    [ObservableProperty]
    private bool isSummaryTabSelected;

    partial void OnIsSummaryTabSelectedChanged(bool value)
    {
        if (!_tabActivationGate.IsReady) return;

        if (value && CanRun() && ShouldAutoReload())
            _ = RunCoreAsync(showFeedback: false);

        _saveViewState();
    }

    /// <summary>True when nothing any of this tab's auto-reload paths care about has
    /// changed since _lastResult was last successfully computed (see _loadedSnapshot) --
    /// the period, the report-scope tree selection, or the shared "something in
    /// AttendanceLogs/ManualAttendanceLogs changed" counters (see AttendanceDataVersion,
    /// which Generate Reports cares about both of, since it merges both tables -- see
    /// AttendanceWorkflowService). Checked by OnIsSummaryTabSelectedChanged's silent
    /// reload above and by TryAutoRun below -- ImportPunchLogAsync/FetchFromDeviceAsync/
    /// the manual-entry actions still always do their own thing unconditionally on an
    /// explicit click rather than asking "did anything change first", since those aren't
    /// reload paths for this tab's own data at all.
    ///
    /// True (i.e. "go ahead and reload") whenever nothing has successfully loaded yet
    /// (_loadedSnapshot is null) or PeriodStart/PeriodEnd aren't validly set -- Validate()/
    /// RunCoreAsync's own checks handle an invalid period correctly either way, so there's
    /// no need to duplicate that check here.
    ///
    /// This is what actually fixes the DataGrid's scroll position resetting on every
    /// revisit to this tab: previously, OnIsSummaryTabSelectedChanged called RunCoreAsync
    /// unconditionally on every single visit, and RunCoreAsync's SummaryRows.Clear()-then-
    /// rebuild (needed for a *real* refresh -- see that method's own comment) fires a
    /// CollectionChanged Reset notification that snaps the DataGrid back to the top
    /// regardless of whether the data underneath actually changed. Skipping the reload
    /// entirely when nothing relevant has changed leaves SummaryRows -- and the grid's
    /// scroll position -- untouched.
    ///
    /// _dataVersion.ScheduleVersion covers a schedule edit made on the Schedule page (see
    /// MainViewModel, which shares this same AttendanceDataVersion instance and bumps it
    /// after every Set/Edit/Clear Schedule, Set Leave, Import Schedule, or Delete
    /// Employee) -- a schedule edit changes what a report *should* show even when neither
    /// punch table moved at all, so Generate Reports needs to notice it exactly the same
    /// way it already notices a new import/manual entry.</summary>
    private bool ShouldAutoReload() =>
        _loadedSnapshot != (PeriodStart, PeriodEnd, _reportScope.SelectionVersion,
            _dataVersion.DeviceLogsVersion, _dataVersion.ManualLogsVersion, _dataVersion.ScheduleVersion,
            _dataVersion.PairingVersion);

    /// <summary>Manual fallback for the auto-refresh above -- backs the Summary tab's
    /// icon-only Refresh button, at the right edge of the Period row (moved there from a
    /// text "↻ Refresh" button in the status-counts strip below, restyled to match
    /// SchedulePage's and PayrollSummaryView's own header Refresh buttons -- see
    /// AttendanceView.xaml's IconHeaderActionButton). Auto-refresh (ShouldAutoReload,
    /// checked on Period/tree edits and on switching back to this tab) covers the
    /// ordinary case, but the schedule and Summary tab can be visited in either order and
    /// this tab's own IsSummaryTabSelected only flips when *this page's* tab control
    /// changes selection -- editing the schedule on the separate Schedule page while this
    /// tab was already the last one showing on the Attendance page doesn't reselect
    /// anything here, so nothing tells this tab to look again until some other trigger (a
    /// Period edit, a tree check, an actual tab switch) happens to come along. This button
    /// exists for exactly that gap: an explicit, unconditional re-run whenever the person
    /// wants to be sure they're looking at the latest schedule, without waiting for one of
    /// those other triggers.
    ///
    /// Unconditional (no ShouldAutoReload gate) and always shows feedback -- unlike
    /// TryAutoRun, a click here is always something the person is watching happen, even
    /// if it turns out nothing had actually changed.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RefreshSummaryAsync() => RunCoreAsync(showFeedback: true);

    /// <summary>The Period row's icon button's actual Content/Command/ToolTip binding
    /// target (see AttendanceSummaryView.xaml) -- this, not RefreshSummaryCommand
    /// directly, is what the button is wired to now, so the same button reads Refresh
    /// when idle and Cancel while anything on the Attendance page is visibly running
    /// (_busy.IsVisiblyRunning -- not just a run started by this button; see
    /// RefreshOrCancelToolTip). This replaces a separate Cancel button that used to
    /// appear in its own CardBorder above the Report Scope tree/Period row whenever
    /// IsVisiblyRunning flipped true, shifting both down for as long as it was visible
    /// and back up the moment it cleared -- an explicit Refresh click was the most common
    /// way to see that happen, since (unlike a Period edit or a tree check, which are
    /// already busy doing something else on screen) clicking Refresh has nothing else
    /// to look at while it runs. Reusing this same button in place avoids the shift
    /// entirely instead of just moving it somewhere smaller.
    ///
    /// CanExecute is IsVisiblyRunning (always fine to try to cancel) OR CanRun() (the
    /// same gate RefreshSummaryCommand already uses) -- needed because CanRun() alone
    /// would leave the button disabled during a run it didn't itself start (_busy.
    /// IsRunning is true, so CanRun() is false) at exactly the moment IsVisiblyRunning
    /// makes it look like a live Cancel button.</summary>
    private bool CanRefreshOrCancelSummary() => _busy.IsVisiblyRunning || CanRun();

    [RelayCommand(CanExecute = nameof(CanRefreshOrCancelSummary))]
    private void RefreshOrCancelSummary()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            _ = RunCoreAsync(showFeedback: true);
    }

    /// <summary>Segoe Fluent Icons glyphs for RefreshOrCancelSummaryCommand's button --
    /// the ordinary Refresh glyph this button always showed, or the same "Cancel" (X)
    /// glyph AttendanceBusyState.CancelCommand's own button used to show, swapped in
    /// while _busy.IsVisiblyRunning is true. See the _busy.PropertyChanged handler in
    /// this class's constructor for what raises this on every flip.</summary>
    public string RefreshOrCancelGlyph => _busy.IsVisiblyRunning ? "" : "";

    /// <summary>Generic on purpose, not "Stop this report" -- IsVisiblyRunning can be
    /// true because of literally anything on the Attendance page (an Import/Fetch
    /// started from Punch Records, a manual entry save/delete, or this tab's own
    /// report run), not only a click on this same button, and there's no way to tell
    /// which one from here. Same wording the old Cancel-bar button's own ToolTip used
    /// for exactly that reason.</summary>
    public string RefreshOrCancelToolTip => _busy.IsVisiblyRunning
        ? "Stop whatever's currently running."
        : "Re-run this report -- useful after editing the schedule elsewhere.";

    /// <summary>Called by AttendanceViewModel.RecheckActiveTabOnReturn every time
    /// AttendancePage.OnNavigatedToAsync fires -- not just the first time (see that
    /// page's own _loaded guard, which only wraps InitializeAsync/ActivateInitialTabAsync).
    /// Closes the one gap ShouldAutoReload's ScheduleVersion check can't close by itself:
    /// a schedule edit made on the separate Schedule page never toggles *this* page's own
    /// IsSummaryTabSelected, so if Summary was already the tab showing here before the
    /// person navigated away, nothing would otherwise notice the edit until some other
    /// trigger (a Period edit, a tree check, an actual sub-tab switch within this page)
    /// happened to come along. This is that missing trigger -- re-run the exact same
    /// ShouldAutoReload-gated check OnIsSummaryTabSelectedChanged's own tab-reselect
    /// reload already uses, just fired from page-level navigation instead of a tab
    /// selection flip.
    ///
    /// Silent (showFeedback: false), same reasoning as that reload -- an ordinary page
    /// switch shouldn't visibly flicker the progress bar any more than an ordinary tab
    /// switch does; the refreshed grid is feedback enough. A no-op whenever
    /// ShouldAutoReload is false, so revisiting the page after nothing relevant changed
    /// doesn't reset the DataGrid's scroll position either.</summary>
    internal void RecheckOnPageRevisit()
    {
        if (!_tabActivationGate.IsReady) return;

        if (CanRun() && ShouldAutoReload())
            _ = RunCoreAsync(showFeedback: false);
    }

    /// <summary>Guards against every checkbox flip in one synchronous bulk-selection
    /// burst -- checking a whole department (see DepartmentGroupViewModel.
    /// OnIsSelectedChanged), Select All, Clear Selection, or the tree's own initial
    /// load/restore -- each queuing its own deferred run below. Only the flip that
    /// finds this false actually schedules one; every later flip in the same burst
    /// sees it already true and returns immediately, having still done its job (bumped
    /// SelectionVersion) for the one deferred run to pick up once it actually
    /// runs.</summary>
    private bool _autoRunPending;

    /// <summary>Fires a visible re-run for a direct change to what a report should
    /// cover -- a Period date edit (OnPeriodStartChanged/OnPeriodEndChanged above) or a
    /// report-scope tree check/uncheck (the ReportScope.PropertyChanged handler in the
    /// constructor) -- and is also re-checked every time a run finishes (the
    /// AttendanceBusyState.IsRunning handler in the constructor), so a change that
    /// arrived while a previous run was still in flight isn't left stale once that one
    /// clears. There's no "Generate Reports" button anymore -- this is the only thing
    /// left that starts a run for a change the person actually made, as opposed to
    /// OnIsSummaryTabSelectedChanged's own silent reload on tab (re)selection, which
    /// exists for a change that happened somewhere *else* (a different tab, or another
    /// window entirely importing punches) while this tab wasn't even being looked at.
    ///
    /// Runs visibly (showFeedback: true) for exactly that reason: a Period edit or a
    /// tree check is something the person is watching happen, unlike a tab switch, so
    /// swapping in the progress bar and posting the usual status-bar messages is the
    /// right amount of feedback here, not a flicker to avoid.
    ///
    /// Gated on _tabActivationGate.IsReady the same way OnIsSummaryTabSelectedChanged is
    /// -- without it, this class's own constructor setting the initial PeriodStart/
    /// PeriodEnd, and ReportScopeViewModel's first tree load, would each try to run a
    /// report before the person has done anything at all (and, for an unset/invalid
    /// initial period, would surface a caution message on startup that nobody asked
    /// for). CanRun() and ShouldAutoReload() cover the rest: nothing fires while a run
    /// is already in progress, before at least one employee is selected, or for a call
    /// that wouldn't actually pick up anything new (e.g. this same method's own
    /// "did a run just finish" check, once that run has already brought _loadedSnapshot
    /// up to date).
    ///
    /// The actual run is deferred, not started inline -- see _autoRunPending's own
    /// doc comment for why a bulk selection change needs that.</summary>
    private void TryAutoRun()
    {
        if (!_tabActivationGate.IsReady) return;
        if (!CanRun() || !ShouldAutoReload()) return;
        if (_autoRunPending) return;

        _autoRunPending = true;

        // Deferred via BeginInvoke, not started inline, so a bulk selection change --
        // checking a whole department flips every one of its employees in one
        // synchronous loop, same for Select All/Clear Selection across every
        // department -- gets exactly one run, scoped to the *final* selection, instead
        // of one run per flip. Without this, the first flip's run would start
        // immediately (scoped to whichever single employee had been checked so far)
        // and visibly rebuild the grid once, before a second, catch-up run (see
        // ShouldAutoReload/_loadedSnapshot) rebuilds it again a moment later with the
        // rest of the selection -- a double flicker for what the person experienced as
        // one single click. BeginInvoke's queued callback can only run once the
        // *entire* current synchronous call stack -- including whatever's left of the
        // loop still flipping checkboxes -- has unwound back to the dispatcher, so by
        // the time it fires, every flip in the burst has already happened and
        // GetSelectedPins() (called fresh inside RunCoreAsync, not snapshotted here)
        // reads the fully-settled selection. DispatcherPriority.Background (lower than
        // Render/Input) additionally lets the checkboxes themselves actually repaint
        // checked before the run's own progress bar appears, rather than both changes
        // landing in the same repaint.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _autoRunPending = false;

            // Re-checked rather than assumed -- something else (a run already started
            // via RefreshSummaryAsync, or a further change that already made this one
            // redundant) may have altered the picture in the gap between this being
            // queued and actually running.
            if (!CanRun() || !ShouldAutoReload()) return;

            _ = RunCoreAsync(showFeedback: true);
        });
    }


    private async Task RunCoreAsync(bool showFeedback)
    {
        var validationErrors = Validate();
        if (validationErrors.Count > 0)
        {
            if (showFeedback)
                _statusBarService.ShowCaution(string.Join(" ", validationErrors));
            return;
        }

        // Only a direct change the person made (showFeedback: true, from TryAutoRun) should
        // bring up the progress bar -- the silent re-run that fires every time this tab is
        // (re)selected (see OnIsSummaryTabSelectedChanged) shouldn't visibly flicker it
        // just because the person switched tabs. See AttendanceBusyState.
        // IsVisiblyRunning's doc comment.
        //
        // A cancelled run deliberately doesn't touch _lastResult/HasResults/SummaryRows
        // either way (same reasoning as the periodChanged branch in onError below, just
        // simpler: a cancelled run never reached a new result, so whatever was already
        // on screen before this run started is still exactly as valid as it was) -- see
        // AttendanceBusyState.RunAsync for why OperationCanceledException never reaches
        // onError at all.
        await _busy.RunAsync(visibly: showFeedback, async cancellationToken =>
        {
            var progress = new Progress<string>(msg =>
            {
                if (showFeedback)
                    _statusBarService.ShowInfo(msg);
            });

            var request = new AttendanceRunRequest
            {
                Policy = _policy,
                PeriodStart = DateOnly.FromDateTime(PeriodStart!.Value),
                PeriodEnd = DateOnly.FromDateTime(PeriodEnd!.Value),
                TargetPins = _reportScope.GetSelectedPins(),
            };

            // Captured now -- alongside request above, before the await below can
            // yield control back to whatever's on the other end of a bulk selection
            // change (e.g. a department checkbox, which flips every one of its
            // employees in a tight synchronous loop -- see
            // DepartmentGroupViewModel.OnIsSelectedChanged). Re-reading these fresh
            // *after* the await, as this used to do, would stamp _loadedSnapshot
            // with whatever SelectionVersion/PeriodStart/PeriodEnd happen to be true
            // by the time this run finishes, not the values request was actually
            // built from a few lines up -- if a second, third, ... employee got
            // checked while this run was already in flight (CanRun() blocks a second
            // *run* from starting, but not the checkbox toggles themselves from
            // still bumping SelectionVersion), the snapshot would then falsely claim
            // "the grid already reflects the current selection" even though request.
            // TargetPins only ever covered whichever employee(s) were checked at the
            // moment this run actually started. ShouldAutoReload's post-run
            // catch-up check (see the _busy.PropertyChanged handler in the
            // constructor) would then find no mismatch and silently skip the
            // re-run that should have picked up the rest of the selection --
            // leaving the grid stuck showing only the first employee(s) checked
            // before the report ever ran.
            var requestSnapshot = (PeriodStart, PeriodEnd, _reportScope.SelectionVersion,
                _dataVersion.DeviceLogsVersion, _dataVersion.ManualLogsVersion, _dataVersion.ScheduleVersion,
                _dataVersion.PairingVersion);

            AttendanceRunResult result = await _attendanceRunner.RunAsync(request, progress, cancellationToken);
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

            // Cleared and rebuilt here -- once the new rows are actually ready --
            // rather than up front before the await. HasResults was already true
            // from whatever the grid was already showing (unless this is the very
            // first run), and it stays true straight through this Clear/re-Add, so
            // the DataGrid (Visibility bound to HasSummaryRows, which depends on
            // HasResults) and the status-counts card above it (bound to HasResults
            // directly) never collapse and reappear for the length of an ordinary
            // refresh. This Clear/rebuild itself still resets the DataGrid's scroll
            // position back to the top, though -- ShouldAutoReload above is what
            // stops OnIsSummaryTabSelectedChanged's silent auto-reload from getting
            // this far at all when nothing relevant has actually changed, which is
            // what actually prevents that reset on an ordinary tab revisit. See
            // ShouldAutoReload's own doc comment for the full story.
            SummaryRows.Clear();
            foreach (var row in BuildSummaryRows(result.Summaries))
                SummaryRows.Add(row);

            HasResults = true;
            _loadedSnapshot = requestSnapshot;
            _saveViewState();

            if (showFeedback)
                _statusBarService.ShowSuccess(
                    $"Report generated: {CompleteCount} complete, {PartialCount} partial, " +
                    $"{AbsentCount} absent, {LeaveCount} leave, {OfficialBusinessCount} official business, " +
                    $"{RestDayCount} rest day.",
                    "Report ready");
        },
        onError: ex =>
        {
            // _lastResult/HasResults/SummaryRows are only dropped here if *this* run's
            // period actually differs from the period _lastResult was last successfully
            // computed for (see _loadedSnapshot) -- a same-period refresh (e.g. the
            // silent auto-reload from OnIsSummaryTabSelectedChanged when the report
            // scope or underlying data changed but the dates didn't, or a TryAutoRun
            // re-run triggered by a tree check without touching the dates) can fail transiently
            // (e.g. a dropped DB connection) without blanking out results that are still
            // perfectly valid for the period still on screen. But if PeriodStart/
            // PeriodEnd had already changed and *this* run -- the one for that new
            // period -- is the one that failed, _lastResult must still go, or
            // ExportSummaryCommand would offer to export the previous period's data
            // mislabeled under the new one.
            bool periodChanged = _loadedSnapshot is null
                || _loadedSnapshot.Value.Start != PeriodStart
                || _loadedSnapshot.Value.End != PeriodEnd;

            if (periodChanged)
            {
                _lastResult = null;
                HasResults = false;
                SummaryRows.Clear();
            }

            if (showFeedback)
                _statusBarService.ShowError(ex.Message);
        });
    }

    /// <summary>Internal (not private) so both this class's own auto-run paths
    /// (TryAutoRun, OnIsSummaryTabSelectedChanged) and
    /// AttendanceViewModel.ActivateInitialTabAsync's initial-tab check can share the one
    /// implementation.</summary>
    internal bool CanRun() =>
        !_busy.IsRunning && (_reportScope.TotalEmployeeCount == 0 || _reportScope.SelectedEmployeeCount > 0);

    private static IEnumerable<AttendanceSummaryRow> BuildSummaryRows(IEnumerable<AttendanceSummary> summaries) =>
        AttendanceSummaryRow.BuildRows(summaries);

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
