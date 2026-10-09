using System.Reactive.Linq;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ReactiveUI;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// The Attendance section -- Summary, Punch Records, and Manual Entries, the three pages the
/// "Attendance" drawer item's submenu navigates between (AttendanceSummaryPage/PunchRecordsPage/
/// ManualEntriesPage). Builds seven single-purpose children (see ViewModels/Attendance/), which
/// the pages bind to directly:
///
///  - Import      -- AttendanceImportViewModel: reads a ZKTeco .dat file into AttendanceLogs.
///  - DeviceFetch -- DeviceFetchViewModel: pulls the same data over the network instead.
///  - ReportScope -- ReportScopeViewModel: the report-scope Department/Employee tree.
///  - Report      -- ReportViewModel: the attendance summary and its export, scoped by ReportScope.
///  - PunchRecords     -- PunchRecordsViewModel: the date-ranged, device-only punch-log viewer and
///    exporter -- deliberately never shows ManualAttendanceLogs.
///  - ManualEntriesTab -- ManualEntriesViewModel: the date-ranged, manual-only viewer, with its
///    own Export…/Import….
///  - ManualEntryEditor -- ManualEntryEditorViewModel: Add/Edit/Delete on a manual entry.
///
/// This class's own job is the two concerns no one child owns:
///
///  - View-state persistence (SaveViewState) -- a period, a tree selection, a date range, a
///    search box, and the page showing captured as one consistent snapshot.
///  - Section activation (EnsureInitializedAsync and the Activate…Tab methods) -- the one-time
///    employee-tree load, and marking which of the three pages was just navigated to, so its
///    own reload-if-stale logic fires.
///
/// Shares the one app-wide AttendanceBusyState with the Schedule and Payroll tabs: every one of
/// them reads and writes the one app-lifetime ScheduleDbContext, and a separate gate per tab
/// serialized nothing against the others -- leaving this section mid-import doesn't cancel the
/// import, so another tab could otherwise start a concurrent operation on that context.
/// </summary>
public sealed class AttendanceViewModel : ReactiveObject, IDisposable
{
    private readonly ViewStateStore _viewStateStore;
    private readonly AttendanceTabActivationGate _tabActivationGate = new();
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;
    private bool _initialized;

    public AttendanceViewModel(
        IAttendanceRunner attendanceRunner,
        IAttendanceLogRepository attendanceLogRepository,
        IManualAttendanceLogRepository manualAttendanceLogRepository,
        Data.Repositories.IScheduleRepository scheduleRepository,
        IStatusBarService statusBarService,
        AttendanceSettings settings,
        ViewStateStore viewStateStore,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        ActiveRosterProvider activeRosterProvider,
        IDayPunchPairingEditorLauncher pairingLauncher)
    {
        _viewStateStore = viewStateStore;
        _busy = busy;
        _employeeDirectory = new AttendanceEmployeeDirectory(scheduleRepository);

        // In-memory, session-only state (see ViewStateStore): blank on every launch, so each
        // falls through to its default -- the current half-month pay period, the most likely
        // thing a person is looking at, for all three pages alike.
        var savedState = viewStateStore.Attendance;
        var today = DateTime.Today;
        var (startDay, endDay) = today.Day < 16 ? (1, 15) : (16, DateTime.DaysInMonth(today.Year, today.Month));
        var initialPeriodStart = savedState.PeriodStart ?? new DateTime(today.Year, today.Month, startDay);
        var initialPeriodEnd = savedState.PeriodEnd ?? new DateTime(today.Year, today.Month, endDay);

        Import = new AttendanceImportViewModel(attendanceLogRepository, statusBarService, _busy, dataVersion, settings.LogDatFile);
        DeviceFetch = new DeviceFetchViewModel(
            attendanceLogRepository, statusBarService, _busy, dataVersion,
            settings.DeviceIp, settings.DevicePort, settings.DeviceCommKey, settings.DeviceTransport);
        ReportScope = new ReportScopeViewModel(activeRosterProvider, viewStateStore);
        PunchRecords = new PunchRecordsViewModel(
            attendanceLogRepository, statusBarService, _busy, dataVersion, _employeeDirectory,
            savedState.LogViewStart ?? initialPeriodStart,
            savedState.LogViewEnd ?? initialPeriodEnd,
            savedState.LogViewSearchText ?? string.Empty,
            savedState.IsPunchRecordsTabSelected,
            SaveViewState, _tabActivationGate);
        ManualEntriesTab = new ManualEntriesViewModel(
            manualAttendanceLogRepository, statusBarService, _busy, dataVersion, _employeeDirectory,
            savedState.ManualEntriesStart ?? initialPeriodStart,
            savedState.ManualEntriesEnd ?? initialPeriodEnd,
            savedState.IsManualEntriesTabSelected, SaveViewState, _tabActivationGate);

        // Before Report, which takes it for the Summary grid's own "Add Manual Entry…".
        ManualEntryEditor = new ManualEntryEditorViewModel(
            manualAttendanceLogRepository, attendanceLogRepository, statusBarService, _busy, dataVersion, _employeeDirectory,
            ManualEntriesTab);
        Report = new ReportViewModel(
            attendanceRunner, statusBarService, settings.Policy, _busy, dataVersion, ReportScope,
            initialPeriodStart, initialPeriodEnd, SaveViewState, _tabActivationGate, ManualEntryEditor,
            pairingLauncher);
    }

    public AttendanceImportViewModel Import { get; }

    public DeviceFetchViewModel DeviceFetch { get; }

    public ReportScopeViewModel ReportScope { get; }

    public ReportViewModel Report { get; }

    public PunchRecordsViewModel PunchRecords { get; }

    public ManualEntriesViewModel ManualEntriesTab { get; }

    /// <summary>Also handed to MainViewModel (see App.xaml.cs), so the calendar's "Add Manual
    /// Entry…" goes through the same editor and the same busy bookkeeping.</summary>
    public ManualEntryEditorViewModel ManualEntryEditor { get; }

    private void SaveViewState()
    {
        if (!ReportScope.HasRestoredScope) return;

        var state = _viewStateStore.Attendance;
        state.PeriodStart = Report.PeriodStart;
        state.PeriodEnd = Report.PeriodEnd;
        state.SelectedPins = ReportScope.GetSelectedPins()?.ToList();
        state.LogViewStart = PunchRecords.LogViewStart;
        state.LogViewEnd = PunchRecords.LogViewEnd;
        state.LogViewSearchText = PunchRecords.LogViewSearchText;
        state.ManualEntriesStart = ManualEntriesTab.ManualEntriesStart;
        state.ManualEntriesEnd = ManualEntriesTab.ManualEntriesEnd;
        state.IsPunchRecordsTabSelected = PunchRecords.IsPunchRecordsTabSelected;
        state.IsManualEntriesTabSelected = ManualEntriesTab.IsManualEntriesTabSelected;

        _viewStateStore.Save();
    }

    /// <summary>
    /// Loads the report-scope tree and warms the employee directory's cache, then opens the
    /// tab-activation gate so each page's "load if stale" logic can start. Idempotent, and
    /// called from all three pages' OnNavigatedToAsync -- whichever is visited first pays for
    /// it.
    ///
    /// Held under IsRunning (not IsVisiblyRunning -- this is the near-instant background work
    /// that stays silent): neither the tree load nor the cache warm otherwise counts as using
    /// the shared ScheduleDbContext, so a keystroke in the Punch Records search box during it
    /// could start a concurrent operation. The cache is seeded from the tree's own employees
    /// rather than queried again -- it's the same Active-only roster -- so whichever of Punch
    /// Records/Manual Entries is visited next starts warm.
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        _busy.IsRunning = true;
        try
        {
            await ReportScope.LoadEmployeeTreeCommand.Execute();
            _employeeDirectory.SeedCache(ReportScope.LoadedEmployees);
        }
        finally
        {
            _busy.IsRunning = false;
        }

        _tabActivationGate.IsReady = true;
    }

    // Exactly one of the three is "the page showing" at a time -- what view-state persistence
    // records, and what each page's own reload-if-stale logic fires on.

    /// <summary>Selecting the Summary already checks whether it needs reloading; revisiting it
    /// while it was already the page showing (a trip to another section and back) checks
    /// explicitly -- once either way.</summary>
    public void ActivateSummaryTab()
    {
        PunchRecords.IsPunchRecordsTabSelected = false;
        ManualEntriesTab.IsManualEntriesTabSelected = false;
        if (Report.IsSummaryTabSelected)
            Report.RecheckOnPageRevisit();
        else
            Report.IsSummaryTabSelected = true;
    }

    public void ActivatePunchRecordsTab()
    {
        Report.IsSummaryTabSelected = false;
        ManualEntriesTab.IsManualEntriesTabSelected = false;
        PunchRecords.IsPunchRecordsTabSelected = true;
    }

    public void ActivateManualEntriesTab()
    {
        Report.IsSummaryTabSelected = false;
        PunchRecords.IsPunchRecordsTabSelected = false;
        ManualEntriesTab.IsManualEntriesTabSelected = true;
    }

    public void Dispose() => _employeeDirectory.Dispose();
}
