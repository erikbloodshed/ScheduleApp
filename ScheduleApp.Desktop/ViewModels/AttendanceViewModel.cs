using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs the Attendance section -- Summary, Punch Records, and Manual Entries, the three
/// pages the "Attendance" drawer item's submenu navigates between (AttendanceSummaryPage/
/// PunchRecordsPage/ManualEntriesPage; see MainWindow.xaml), formerly three TabItems
/// inside one AttendancePage. Composes seven single-purpose child ViewModels (see
/// ViewModels/Attendance/) rather than implementing the tab's eight concerns itself:
///
///  - Import      -- AttendanceImportViewModel: reads a ZKTeco .dat file into AttendanceLogs.
///  - DeviceFetch -- DeviceFetchViewModel: pulls the same data over the network instead.
///  - ReportScope -- ReportScopeViewModel: the report-scope Department/Employee tree.
///  - Report      -- ReportViewModel: Generate Reports / Export Summary…, scoped by ReportScope.
///  - PunchRecords    -- PunchRecordsViewModel: the date-ranged, device-only punch-log
///    viewer/exporter -- deliberately never shows ManualAttendanceLogs (see that class's
///    doc comment).
///  - ManualEntriesTab -- ManualEntriesViewModel: the date-ranged, manual-only viewer,
///    plus its own Export…/Import… (bulk-add many rows from an Excel workbook at once).
///  - ManualEntryEditor -- ManualEntryEditorViewModel: Add/Edit/Delete on a manual entry,
///    which refreshes ManualEntriesTab afterward if it's loaded.
///
/// This class's own job is just the two concerns that are inherently cross-cutting rather
/// than any one tab's:
///
///  - View-state persistence (SaveViewState) -- a period, a tree selection, a date range,
///    a search box, and a section choice all need to be captured as one consistent
///    snapshot, which only something above all of them can do.
///  - Section-activation orchestration (EnsureInitializedAsync/ActivateSummaryTab/
///    ActivatePunchRecordsTab/ActivateManualEntriesTab, in the "Section activation" region
///    below) -- the one-time employee-tree load, and marking whichever of the three pages
///    was just navigated to as the active one so its own auto-reload-if-stale logic fires.
///
/// Everything else -- the actual bindable properties and commands XAML uses -- is forwarded
/// from whichever child owns it (see the "Forwarded members" region below) so
/// AttendanceSummaryView.xaml/PunchRecordsView.xaml/ManualEntriesView.xaml's bindings
/// don't need to differ from what one shared AttendanceView.xaml used to bind to,
/// just backed by seven smaller, independently-testable objects instead of one 1,400+
/// line one.
/// </summary>
public partial class AttendanceViewModel : ObservableObject
{
    private readonly ViewStateStore _viewStateStore;
    private readonly AttendanceTabActivationGate _tabActivationGate = new();

    /// <summary>The *same* instance MainViewModel/PayrollViewModel/ScheduleCalendarViewModel/
    /// ScheduleAssignmentViewModel already share (see App.xaml.cs's registration) -- NOT a
    /// separate `new AttendanceBusyState(...)` of this class's own, which is what this field
    /// used to be constructed from.
    ///
    /// That separate instance was the actual remaining half of the "second operation started
    /// on this context" crash: every one of those classes agrees the single, app-lifetime-
    /// scoped ScheduleDbContext (see App.xaml.cs's AddDbContext/CreateScope comments) needs a
    /// gate around it, but a *different* gate serializes nothing against the other one.
    /// AttendancePage.OnNavigatedFromAsync is a no-op -- leaving the Attendance tab mid-Import/
    /// mid-Fetch/mid-Generate-Reports doesn't cancel it, it just keeps running in the
    /// background under this class's own busy state -- so switching to the Schedule tab and
    /// selecting an employee (or setting/clearing a schedule) could, and did, start a second,
    /// genuinely concurrent operation against that same shared DbContext instance while the
    /// Attendance-side one was still in flight: two unrelated gates, each individually correct,
    /// guarding one context they don't actually share knowledge of. Constructor-injecting the
    /// one DI-registered instance instead closes that gap the same way the Schedule/Payroll
    /// sharing already did for each other -- see this class's own constructor for where that
    /// used to be wired up differently, and AttendanceBusyState's own RunAsync doc comment for
    /// the rest of this class's race-closing history.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Injected (not `new()`'d here) so MainViewModel gets the exact same
    /// instance -- see this type's own doc comment and App.xaml.cs's registration of
    /// it -- rather than each ViewModel silently tracking its own, disconnected set of
    /// counters.</summary>
    private readonly AttendanceDataVersion _dataVersion;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;

    public AttendanceImportViewModel Import { get; }
    public DeviceFetchViewModel DeviceFetch { get; }
    public ReportScopeViewModel ReportScope { get; }
    public ReportViewModel Report { get; }
    public PunchRecordsViewModel PunchRecords { get; }

    /// <summary>Named "…Tab", not "ManualEntries", so it doesn't collide with the
    /// forwarded ManualEntries collection property below (the grid rows) -- the original
    /// class had exactly one thing called ManualEntries; splitting it into "the
    /// sub-ViewModel that owns it" and "the collection itself" needs two different
    /// names.</summary>
    public ManualEntriesViewModel ManualEntriesTab { get; }

    public ManualEntryEditorViewModel ManualEntryEditor { get; }

    public AttendanceViewModel(
        IAttendanceRunner attendanceRunner,
        IAttendanceLogRepository attendanceLogRepository,
        IManualAttendanceLogRepository manualAttendanceLogRepository,
        IScheduleRepository scheduleRepository,
        IStatusBarService statusBarService,
        AttendanceSettings settings,
        ViewStateStore viewStateStore,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        ActiveRosterProvider activeRosterProvider,
        IDayPunchPairingEditorLauncher pairingLauncher)
    {
        _viewStateStore = viewStateStore;
        _dataVersion = dataVersion;

        // Injected, not `new AttendanceBusyState(shutdownSignal.Token)` -- see this field's
        // own doc comment above for the cross-page race that separate instance left open.
        // AppShutdownSignal still reaches this gate the same as before (Phase 5 of the
        // cancellation rollout); it's just wired up once, in App.xaml.cs's registration of
        // the shared instance, instead of a second time here.
        _busy = busy;

        _employeeDirectory = new AttendanceEmployeeDirectory(scheduleRepository);

        // ViewStateStore is in-memory/session-only (see its own doc comment), so savedState
        // here is always blank on a fresh launch -- every one of PeriodStart/PeriodEnd/
        // LogView*/ManualEntries*/the tab-selected flags below falls through to its ??
        // default every time the app starts, not just the first time it's ever run.
        var savedState = viewStateStore.Attendance;

        DateTime today = DateTime.Today;
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        (int startDay, int endDay) = today.Day < 16 ? (1, 15) : (16, daysInMonth);
        DateTime initialPeriodStart = savedState.PeriodStart ?? new DateTime(today.Year, today.Month, startDay);
        DateTime initialPeriodEnd = savedState.PeriodEnd ?? new DateTime(today.Year, today.Month, endDay);

        // Punch Records and Manual Entries default to this exact same cutoff period, not
        // their own separate range -- previously "last 7 days" (DateTime.Today.AddDays(-6)
        // through DateTime.Today), now the current half-month, same reasoning as Report's
        // own PeriodStart/PeriodEnd just above and Payroll's own period default (see
        // PayrollScopeState's doc comment): the person's most likely task on any of these
        // three sub-tabs is "the current pay period," so all three should open already
        // scoped to it rather than each guessing a different range.

        Import = new AttendanceImportViewModel(attendanceLogRepository, statusBarService, _busy, _dataVersion, settings.LogDatFile);

        DeviceFetch = new DeviceFetchViewModel(
            attendanceLogRepository, statusBarService, _busy, _dataVersion,
            settings.DeviceIp, settings.DevicePort, settings.DeviceCommKey, settings.DeviceTransport);

        // activeRosterProvider replaces the raw scheduleRepository ReportScopeViewModel's own
        // tree load used to read through directly -- see ActiveRosterProvider's own doc
        // comment. scheduleRepository itself stays a constructor parameter here regardless,
        // for _employeeDirectory just above.
        ReportScope = new ReportScopeViewModel(activeRosterProvider, viewStateStore);

        PunchRecords = new PunchRecordsViewModel(
            attendanceLogRepository, statusBarService, _busy, _dataVersion, _employeeDirectory,
            savedState.LogViewStart ?? initialPeriodStart,
            savedState.LogViewEnd ?? initialPeriodEnd,
            savedState.LogViewSearchText ?? string.Empty,
            savedState.IsPunchRecordsTabSelected,
            SaveViewState, _tabActivationGate);

        ManualEntriesTab = new ManualEntriesViewModel(
            manualAttendanceLogRepository, statusBarService, _busy, _dataVersion, _employeeDirectory,
            savedState.ManualEntriesStart ?? initialPeriodStart,
            savedState.ManualEntriesEnd ?? initialPeriodEnd,
            savedState.IsManualEntriesTabSelected, SaveViewState, _tabActivationGate);

        // Constructed before Report (moved up from its old spot just below Report) --
        // Report now takes ManualEntryEditor itself as a constructor dependency (see
        // ReportViewModel._manualEntryEditor's own doc comment for why: the Summary
        // grid's own right-click "Add Manual Entry…" needs it), so it has to exist
        // first.
        ManualEntryEditor = new ManualEntryEditorViewModel(
            manualAttendanceLogRepository, attendanceLogRepository, statusBarService, _busy, _dataVersion, _employeeDirectory,
            ManualEntriesTab);

        Report = new ReportViewModel(
            attendanceRunner, statusBarService, settings.Policy, _busy, _dataVersion, ReportScope,
            initialPeriodStart, initialPeriodEnd, SaveViewState, _tabActivationGate, ManualEntryEditor,
            pairingLauncher);

        // Relays each child's own PropertyChanged onto this class under the same
        // property name, so a two-way XAML binding on (say) AttendanceViewModel.PeriodStart
        // -- which reads/writes through the forwarding property below -- still refreshes
        // when Report.PeriodStart changes for a reason other than that same binding
        // setting it (e.g. after LoadEmployeeTreeAsync). No name collides across more
        // than one child (each forwarded property belongs to exactly one of them; see
        // the region below), so a blanket relay is safe.
        Report.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        PunchRecords.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        ManualEntriesTab.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        ReportScope.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsVisiblyRunning))
                OnPropertyChanged(nameof(IsVisiblyRunning));
        };
    }

    // ---- Forwarded members ----
    //
    // Every property/command AttendanceView.xaml binds to, forwarded from whichever
    // child ViewModel actually owns it. Nothing here has its own logic -- it's here so
    // the XAML (and AttendancePage.xaml.cs/AttendanceView.xaml.cs's code-behind) didn't
    // need to change as part of the split.

    /// <summary>Drives the Summary tab's inline progress-bar in AttendanceView.xaml
    /// (there's no button to swap out anymore -- see ReportViewModel.TryAutoRun) --
    /// deliberately sourced from AttendanceBusyState.IsVisiblyRunning, not IsRunning, so
    /// a tab's own silent auto-load-on-select doesn't flicker this just because the
    /// person switched tabs. See that property's doc comment for the full story.</summary>
    public bool IsVisiblyRunning => _busy.IsVisiblyRunning;

    /// <summary>Backs the global Cancel button in AttendanceView.xaml's toolbar (above
    /// the TabControl, so it's visible regardless of which tab is active) -- same
    /// forwarded-command pattern as every other one here, just sourced from
    /// AttendanceBusyState instead of a tab-specific child ViewModel, since whatever's
    /// running could be Load/Export/Generate Reports on any tab, an Import/Fetch, or a
    /// Manual Entry Add/Edit/Delete dialog. See AttendanceBusyState.Cancel's own doc
    /// comment for why CanExecute is tied to IsVisiblyRunning rather than IsRunning.</summary>
    public IRelayCommand CancelCommand => _busy.CancelCommand;

    public ObservableCollection<DepartmentGroupViewModel> Departments => ReportScope.Departments;
    public string SelectionScopeText => ReportScope.SelectionScopeText;
    public IAsyncRelayCommand LoadEmployeeTreeCommand => ReportScope.LoadEmployeeTreeCommand;
    public IRelayCommand SelectAllTreeCommand => ReportScope.SelectAllTreeCommand;
    public IRelayCommand ClearTreeSelectionCommand => ReportScope.ClearTreeSelectionCommand;

    /// <summary>Named with the "ReportScope" prefix (unlike this tab's other forwarded
    /// members) specifically to stay distinct from PunchRecords.LogViewSearchText below --
    /// both are free-text search boxes on this same tab, just scoping different
    /// things.</summary>
    public string ReportScopeSearchText
    {
        get => ReportScope.SearchText;
        set => ReportScope.SearchText = value;
    }

    public IAsyncRelayCommand ImportPunchLogCommand => Import.ImportPunchLogCommand;

    public IAsyncRelayCommand FetchFromDeviceCommand => DeviceFetch.FetchFromDeviceCommand;

    public DateTime? PeriodStart
    {
        get => Report.PeriodStart;
        set => Report.PeriodStart = value;
    }

    public DateTime? PeriodEnd
    {
        get => Report.PeriodEnd;
        set => Report.PeriodEnd = value;
    }

    public IRelayCommand PreviousPeriodCommand => Report.PreviousPeriodCommand;
    public IRelayCommand NextPeriodCommand => Report.NextPeriodCommand;

    public bool HasResults => Report.HasResults;
    public bool HasSummaryRows => Report.HasSummaryRows;
    public int TotalLogs => Report.TotalLogs;
    public int CompleteCount => Report.CompleteCount;
    public int PartialCount => Report.PartialCount;
    public int AbsentCount => Report.AbsentCount;
    public int LeaveCount => Report.LeaveCount;
    public int OfficialBusinessCount => Report.OfficialBusinessCount;

    // Pre-existing gap: every other status count above was already forwarded, but this
    // one wasn't, even though AttendanceView.xaml's Rest Day tile has always bound to
    // {Binding RestDayCount} -- silently resolving to nothing (a WPF binding error, not
    // a compile error) rather than Report.RestDayCount. Fixed here while touching this
    // same list for SelectedStatusFilter/ClearStatusFilterCommand below.
    public int RestDayCount => Report.RestDayCount;

    public int OrphanedCount => Report.OrphanedCount;
    public int UnscheduledCount => Report.UnscheduledCount;
    public ObservableCollection<AttendanceSummaryRow> SummaryRows => Report.SummaryRows;
    public ICollectionView SummaryRowsView => Report.SummaryRowsView;

    /// <summary>Which status tile currently narrows SummaryRowsView, or null when every
    /// status is showing -- see ReportViewModel.SelectedStatusFilter. Forwarded (not just
    /// left to the wildcard PropertyChanged relay above) because AttendanceView.xaml's
    /// per-tile Border.Background bindings and the Clear Filter button's Visibility both
    /// read it directly off this class's DataContext, same as every other Report-owned
    /// value on this page.</summary>
    public PunchStatus? SelectedStatusFilter => Report.SelectedStatusFilter;

    public bool IsSummaryTabSelected
    {
        get => Report.IsSummaryTabSelected;
        set => Report.IsSummaryTabSelected = value;
    }

    public IRelayCommand OpenSummaryCommand => Report.OpenSummaryCommand;
    public IRelayCommand ExportSummaryCommand => Report.ExportSummaryCommand;
    public IRelayCommand<PunchStatus> ShowStatusDetailCommand => Report.ShowStatusDetailCommand;
    public IRelayCommand ClearStatusFilterCommand => Report.ClearStatusFilterCommand;
    public IRelayCommand ShowOrphanedDetailCommand => Report.ShowOrphanedDetailCommand;
    public IRelayCommand ShowUnscheduledDetailCommand => Report.ShowUnscheduledDetailCommand;
    public IAsyncRelayCommand RefreshSummaryCommand => Report.RefreshSummaryCommand;

    /// <summary>Backs the Summary grid's own right-click "Add Manual Entry…" (see
    /// AttendanceView.xaml.cs's SummaryRow_MouseRightButtonDown) -- same forwarded-
    /// command pattern as every other Report-owned command above, just parameterized
    /// on the row that was right-clicked. See ReportViewModel.AddManualEntryForRowAsync's
    /// own doc comment for the rest of the story.</summary>
    public IAsyncRelayCommand<AttendanceSummaryRow> AddManualEntryForRowCommand => Report.AddManualEntryForRowCommand;

    /// <summary>Backs the Summary grid's own right-click "Edit Punch Pairing…" (see
    /// AttendanceSummaryView.xaml.cs's SummaryRow_MouseRightButtonDown, which only builds
    /// that item for a Flexible row) -- same forwarded-command pattern as
    /// AddManualEntryForRowCommand just above. See
    /// ReportViewModel.EditPunchPairingForRowAsync's own doc comment.</summary>
    public IAsyncRelayCommand<AttendanceSummaryRow> EditPunchPairingForRowCommand => Report.EditPunchPairingForRowCommand;

    public bool IsPunchRecordsTabSelected
    {
        get => PunchRecords.IsPunchRecordsTabSelected;
        set => PunchRecords.IsPunchRecordsTabSelected = value;
    }

    public DateTime? LogViewStart
    {
        get => PunchRecords.LogViewStart;
        set => PunchRecords.LogViewStart = value;
    }

    public DateTime? LogViewEnd
    {
        get => PunchRecords.LogViewEnd;
        set => PunchRecords.LogViewEnd = value;
    }

    public string LogViewSearchText
    {
        get => PunchRecords.LogViewSearchText;
        set => PunchRecords.LogViewSearchText = value;
    }

    public ObservableCollection<PunchSearchSuggestion> LogViewSuggestions => PunchRecords.LogViewSuggestions;

    public bool IsLogViewSuggestionsOpen
    {
        get => PunchRecords.IsLogViewSuggestionsOpen;
        set => PunchRecords.IsLogViewSuggestionsOpen = value;
    }

    public IRelayCommand<PunchSearchSuggestion> SelectLogViewSuggestionCommand => PunchRecords.SelectLogViewSuggestionCommand;

    public int StoredLogsCount => PunchRecords.StoredLogsCount;
    public bool HasLoadedStoredLogs => PunchRecords.HasLoadedStoredLogs;
    public ObservableCollection<StoredPunchLogRow> StoredLogs => PunchRecords.StoredLogs;
    public ICollectionView StoredLogsView => PunchRecords.StoredLogsView;
    public IAsyncRelayCommand LoadStoredLogsCommand => PunchRecords.LoadStoredLogsCommand;
    public IAsyncRelayCommand ExportStoredLogsCommand => PunchRecords.ExportStoredLogsCommand;
    public IRelayCommand OpenStoredLogsExportCommand => PunchRecords.OpenStoredLogsExportCommand;

    /// <summary>Named with the "LogView" prefix (unlike PreviousPeriodCommand/
    /// NextPeriodCommand above) for the same reason ReportScopeSearchText is -- both
    /// PunchRecords and Report have their own period-nav pair on this same
    /// tab.</summary>
    public IAsyncRelayCommand PreviousLogViewPeriodCommand => PunchRecords.PreviousPeriodCommand;
    public IAsyncRelayCommand NextLogViewPeriodCommand => PunchRecords.NextPeriodCommand;

    public bool IsManualEntriesTabSelected
    {
        get => ManualEntriesTab.IsManualEntriesTabSelected;
        set => ManualEntriesTab.IsManualEntriesTabSelected = value;
    }

    public DateTime? ManualEntriesStart
    {
        get => ManualEntriesTab.ManualEntriesStart;
        set => ManualEntriesTab.ManualEntriesStart = value;
    }

    public DateTime? ManualEntriesEnd
    {
        get => ManualEntriesTab.ManualEntriesEnd;
        set => ManualEntriesTab.ManualEntriesEnd = value;
    }

    public int ManualEntriesCount => ManualEntriesTab.ManualEntriesCount;
    public bool HasLoadedManualEntries => ManualEntriesTab.HasLoadedManualEntries;
    public ObservableCollection<StoredPunchLogRow> ManualEntries => ManualEntriesTab.ManualEntries;
    public IAsyncRelayCommand LoadManualEntriesCommand => ManualEntriesTab.LoadManualEntriesCommand;
    public IAsyncRelayCommand ExportManualEntriesCommand => ManualEntriesTab.ExportManualEntriesCommand;
    public IRelayCommand OpenManualEntriesExportCommand => ManualEntriesTab.OpenManualEntriesExportCommand;
    public IAsyncRelayCommand ImportManualEntriesCommand => ManualEntriesTab.ImportManualEntriesCommand;

    /// <summary>Named with the "ManualEntries" prefix for the same reason
    /// PreviousLogViewPeriodCommand/NextLogViewPeriodCommand are above -- keeps this
    /// tab's own pair distinct from Report's and PunchRecords'.</summary>
    public IAsyncRelayCommand PreviousManualEntriesPeriodCommand => ManualEntriesTab.PreviousPeriodCommand;
    public IAsyncRelayCommand NextManualEntriesPeriodCommand => ManualEntriesTab.NextPeriodCommand;

    public IAsyncRelayCommand AddManualEntryCommand => ManualEntryEditor.AddManualEntryCommand;
    public IAsyncRelayCommand<StoredPunchLogRow> EditManualEntryCommand => ManualEntryEditor.EditManualEntryCommand;
    public IAsyncRelayCommand<StoredPunchLogRow> DeleteManualEntryCommand => ManualEntryEditor.DeleteManualEntryCommand;

    // ---- View-state persistence ----

    /// <summary>Writes the whole Attendance view-state snapshot in one go and saves it --
    /// deliberately one method reading current values off every child, rather than each
    /// child persisting its own slice independently, so a restart always sees a
    /// consistent combination (e.g. a punch-log date range that matches the search text
    /// that was actually in the box next to it) instead of whichever fields happened to
    /// save last. Passed into ReportViewModel/PunchRecordsViewModel/ManualEntriesViewModel
    /// as a plain Action so those children can trigger a save at their own
    /// property-changed/tab-changed/successful-load moments without needing to know this
    /// class exists.
    ///
    /// Skips saving before the report-scope tree has been restored at least once this
    /// run (see ReportScopeViewModel.HasRestoredScope) -- without this, PeriodStart/
    /// PeriodEnd's constructor-time assignment (which fires before the tree even exists
    /// yet) would write "everyone" over a previously saved narrowed scope, since
    /// GetSelectedPins() can't tell "no employees loaded yet" apart from "nothing
    /// narrowed".</summary>
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

    // ---- Section activation (drawer submenu navigation) ----
    //
    // Summary/Punch Records/Manual Entries used to be three TabItems inside one
    // AttendancePage; they're now three separate pages the "Attendance" drawer item's
    // submenu navigates between (AttendanceSummaryPage/PunchRecordsPage/ManualEntriesPage
    // -- see MainWindow.xaml), each calling EnsureInitializedAsync then its own
    // ActivateXTab below from OnNavigatedToAsync. IsSummaryTabSelected/
    // IsPunchRecordsTabSelected/IsManualEntriesTabSelected (see the "Forwarded members"
    // region above) keep their old names and their old job -- each child's own
    // OnIsXTabSelectedChanged still auto-reloads when its flag flips false-to-true, and
    // _saveViewState still needs exactly one of the three meaning "the section currently
    // showing" -- only *what flips them* changed, from a TabControl's own selection
    // binding to these methods.

    private bool _initialized;

    /// <summary>Loads the report-scope employee tree and warms the shared employee-
    /// directory cache, then opens the tab-activation gate so each section's own
    /// OnIsXTabSelectedChanged can start driving its "load if stale" logic. Idempotent
    /// (_initialized) and called from all three pages' OnNavigatedToAsync -- whichever of
    /// Summary/Punch Records/Manual Entries the person navigates to first is the one that
    /// actually pays for this; the other two's own calls are then no-ops, same as
    /// revisiting an already-open page.
    ///
    /// Wraps the tree load + cache warm in _busy.IsRunning (not IsVisiblyRunning -- this
    /// is exactly the near-instant background work IsVisiblyRunning exists to stay silent
    /// for, see AttendanceBusyState) for the same reason the old InitializeAsync did:
    /// neither the tree load nor SeedCache otherwise participates in the "something is
    /// using the shared, app-lifetime-scoped ScheduleDbContext right now" guard every
    /// other command already respects, so a keystroke in the Punch Records search box
    /// landing during this window could fire a second, concurrent operation against that
    /// same DbContext (see PunchRecordsViewModel.OnLogViewSearchTextChanged, which checks
    /// _busy.IsRunning for exactly this reason).
    ///
    /// SeedCache is seeded from ReportScope.LoadedEmployees rather than a second
    /// _employeeDirectory.GetAllAsync() call -- LoadEmployeeTreeCommand just above already
    /// ran the exact same Active-only departments+unassigned query GetAllAsync would run,
    /// and SeedCache's own doc comment covers why re-reading isn't needed. This also warms
    /// the cache for whichever of Punch Records/Manual Entries the person visits next,
    /// covering the one case their own tab-activation load doesn't already handle as a
    /// side effect: if the tree load is the only thing that's run so far (person opened
    /// Summary first, which never touches AttendanceEmployeeDirectory directly), the cache
    /// would otherwise stay cold until whatever they first type into the Punch Records
    /// search box, once they get there.</summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        _busy.IsRunning = true;
        try
        {
            await ReportScope.LoadEmployeeTreeCommand.ExecuteAsync(null);
            _employeeDirectory.SeedCache(ReportScope.LoadedEmployees);
        }
        finally
        {
            _busy.IsRunning = false;
        }

        _tabActivationGate.IsReady = true;
    }

    /// <summary>Marks Summary as the active section and lets Report's own
    /// OnIsSummaryTabSelectedChanged auto-reload if anything's stale -- covers navigating
    /// here from a sibling section (Punch Records/Manual Entries), the same false-to-true
    /// flip a TabControl selection used to cause. RecheckOnPageRevisit afterward covers
    /// the one case that flip can't: navigating back to Summary when it was *already* the
    /// active section (IsSummaryTabSelected never changed, so no change notification
    /// fired) after visiting a completely different top-level page (Schedule) that may
    /// have edited something a report depends on -- see that method's own doc comment.
    /// Safe to call even when the line above just started its own run: RunCoreAsync's
    /// CanRun() check (!_busy.IsRunning) is already true by this point if so, since
    /// nothing awaits between here and there, so RecheckOnPageRevisit's identical guard
    /// simply no-ops. Only Summary needs this -- see PunchRecords/ManualEntriesTab's own
    /// ShouldAutoReload doc comments for why their data can only ever change from this
    /// same page-family's own actions, not a totally unrelated page.</summary>
    public void ActivateSummaryTab()
    {
        IsPunchRecordsTabSelected = false;
        IsManualEntriesTabSelected = false;
        IsSummaryTabSelected = true;
        Report.RecheckOnPageRevisit();
    }

    /// <summary>Marks Punch Records as the active section -- see ActivateSummaryTab's own
    /// doc comment for the shared false-to-true-flip mechanics. No RecheckOnPageRevisit
    /// equivalent needed here: DeviceLogsVersion only ever changes via Import…/Fetch from
    /// Device, both of which now live on this same page (see PunchRecordsView.xaml), so
    /// there's no route for it to change while this page wasn't the one showing.</summary>
    public void ActivatePunchRecordsTab()
    {
        IsSummaryTabSelected = false;
        IsManualEntriesTabSelected = false;
        IsPunchRecordsTabSelected = true;
    }

    /// <summary>Marks Manual Entries as the active section -- see ActivateSummaryTab's own
    /// doc comment for the shared false-to-true-flip mechanics. No RecheckOnPageRevisit
    /// equivalent needed here, same reasoning as ActivatePunchRecordsTab: ManualLogsVersion
    /// only ever changes via this page's own Add/Edit/Delete/Import….</summary>
    public void ActivateManualEntriesTab()
    {
        IsSummaryTabSelected = false;
        IsPunchRecordsTabSelected = false;
        IsManualEntriesTabSelected = true;
    }
}
