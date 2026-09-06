using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 2 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel as
/// build-order step 3. Owns the Payroll Group table (<see cref="PayrollGroupRows"/>, one row
/// per BatchScopeEmployees entry) and the "Not in group" roster (<see
/// cref="AvailableEmployeeRows"/>), the search box that filters both, and every command that
/// changes group membership (Select/Remove/Add-one/Add-many) -- see PayrollSummaryViewModel's
/// own doc comment for the sibling concern (concern 1) this was split apart from.
///
/// PayrollPage.xaml's PayrollGroupGrid/AvailableEmployeeGrid and PayrollPage.xaml.cs are all
/// typed/bound directly to PayrollViewModel (the facade), never to this class -- so every
/// member here that XAML or code-behind touches is forwarded back out under the same name via
/// PayrollViewModel.Group (see that class's own "Forwarded members (PayrollGroupViewModel)"
/// region). Nothing in this class itself needs to know that.
///
/// Takes PayrollScopeState and AttendanceBusyState the same shared-not-owned way
/// PayrollSummaryViewModel does (see that class's own doc comment and the refactor plan's own
/// "Dependency graph") -- both constructed on PayrollViewModel and passed in here. Also takes
/// PayrollSummaryViewModel itself, one direction only: the refactor plan's own "Group &lt;-&gt;
/// Summary" wrinkle found these two concerns depending on each other in *both* directions
/// under the old single-class shape (LoadCoreAsync patching PayrollGroupRows directly; Remove
/// calling Summary's own reload), which isn't constructible once they're separate classes --
/// broken here by having Group take Summary as a constructor dependency and subscribe to its
/// <see cref="PayrollSummaryViewModel.EmployeeNetPayComputed"/> event for the first case (see
/// this constructor), and by calling <see cref="PayrollSummaryViewModel.RefreshAsync"/>
/// directly for the second (see RemoveEmployeeFromGroupAsync below).</summary>
public partial class PayrollGroupViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;

    /// <summary>Replaces the raw IScheduleRepository this class used to read
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync through
    /// directly in RefreshFullRosterAsync and AddEmployeesToGroupAsync -- see
    /// ActiveRosterProvider's own doc comment for why both now go through the shared,
    /// RosterVersion-gated cache instead, the same swap ReportScopeViewModel/
    /// PayslipScopeViewModel/PayrollWizardViewModel/PayrollRunViewModel.LoadPayrollGroupAsync
    /// make for their own, identically-shaped roster reads.</summary>
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IPayrollRunRepository _payrollRunRepository;
    private readonly IStatusBarService _statusBarService;

    /// <summary>Shared with PayrollViewModel and its other children -- see
    /// PayrollViewModel's own _busy doc comment for why this is one instance, not one per
    /// child.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _scope doc comment. This class reads/writes PeriodStart/PeriodEnd/
    /// BatchScopeEmployees/ActivePayrollRunId straight off this instance rather than through
    /// any forwarding property of its own, the same way PayrollSummaryViewModel already
    /// does.</summary>
    private readonly PayrollScopeState _scope;

    /// <summary>The single-employee-breakdown concern this class depends on -- see this
    /// class's own doc comment for why the dependency runs this direction only.</summary>
    private readonly PayrollSummaryViewModel _summary;

    /// <summary>Shared with AttendanceViewModel/MainViewModel/ScheduleAssignmentViewModel --
    /// see App.xaml.cs's own registration of this class. Read by RecheckOnPageRevisitAsync
    /// below to notice a schedule edit made on the Schedule page while this group was
    /// already loaded; written by nothing here -- this class only ever reads
    /// ScheduleVersion/AnyScheduleChangeSince, never bumps them itself (that's
    /// ScheduleAssignmentViewModel's own job, on the write side).</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>Snapshot of _dataVersion.ScheduleVersion taken the moment
    /// RefreshPayrollGroupRowsAsync last successfully computed the currently-loaded group --
    /// what RecheckOnPageRevisitAsync below compares the live value against (via
    /// _dataVersion.AnyScheduleChangeSince, not a raw comparison -- see that method's own
    /// doc comment for why) to decide whether a schedule edit made elsewhere actually
    /// touches anyone in this group and is worth paying for a recompute over. -1 until the
    /// first successful load -- not that it matters, since RequestPayrollGroupRefresh
    /// itself already no-ops when BatchScopeEmployees is empty, exactly the state before
    /// any load, so RecheckOnPageRevisitAsync never has a reason to fire before then anyway.
    /// Taken at the START of the load (see RefreshPayrollGroupRowsAsync's own comment on
    /// where this gets set), not the end, so a bump that races in mid-load is still
    /// noticed on the very next revisit rather than being silently absorbed as "already
    /// accounted for."</summary>
    private int _loadedScheduleVersion = -1;

    /// <summary>Sibling of _loadedScheduleVersion above, for _dataVersion.HolidayVersion --
    /// snapshotted at the same start-of-load moment and compared the same way in
    /// RecheckOnPageRevisitAsync, except the comparison is a raw `!=` rather than a per-pin
    /// AnyScheduleChangeSince: holidays are company-wide (see
    /// AttendanceDataVersion.HolidayVersion's own doc comment), so any change moves Holiday
    /// Pay for whoever in the loaded group is scheduled on that date, and there's no
    /// per-employee holiday tracking to filter against.</summary>
    private int _loadedHolidayVersion = -1;

    /// <summary>Third sibling of the two stamps above, for _dataVersion.AttendanceInputs --
    /// the device-punch/manual-punch/pairing counters as one comparable value (see that
    /// property's own doc comment). Snapshotted at the same start-of-load moment, committed on
    /// the same success check, and compared with a raw `!=` like _loadedHolidayVersion rather
    /// than per-pin: none of the three counters behind it records which employees it touched.
    /// Null (not -1) until the first successful load -- see AttendanceInputsVersion's own doc
    /// comment.
    ///
    /// Group-side counterpart of PayrollSummaryViewModel._loadedAttendanceInputs, closing the
    /// same gap for the NetPay column that one closes for SelectedEmployee's detailed
    /// breakdown -- see that field's own doc comment for what actually goes stale without it.
    /// Unlike the schedule half, this can't be narrowed to just this group's members, so a
    /// punch edit for someone outside the group still recomputes it; that costs one batch run
    /// that lands on the same numbers, which is the same "over-report rather than under-report"
    /// trade the rest of this mechanism already makes.</summary>
    private AttendanceInputsVersion? _loadedAttendanceInputs;

    /// <summary>Guards RequestPayrollGroupRefresh() the same "arrived while busy, so defer
    /// instead of racing the shared ScheduleDbContext" way PayrollSummaryViewModel's own
    /// _refreshPending field guards RequestRefresh() -- see that field's own doc comment. Kept
    /// as a fully separate flag rather than shared with that one, since the two can
    /// legitimately be pending at the same time for different reasons (e.g. a period edit
    /// needs both this employee's own reload AND the payroll group requeried) and the
    /// _busy.PropertyChanged handler below needs to know which of the two (or both) to re-run
    /// once IsRunning drops back to false.</summary>
    private bool _payrollGroupRefreshPending;

    /// <summary>Cached flat roster (every department's employees, plus the unassigned ones)
    /// backing AvailableEmployeeRows -- loaded by RefreshFullRosterAsync whenever
    /// BatchScopeEmployees is wholesale-replaced (a fresh "New Payroll Run…"/"Load Payroll
    /// Group…", see RequestFullRosterRefresh) and then just re-filtered in memory by
    /// RebuildAvailableEmployeeRows on every subsequent BatchScopeEmployees change --
    /// including AddEmployeeToGroupAsync/RemoveEmployeeFromGroupAsync/
    /// AddEmployeesToGroupAsync's own single- or few-row patches -- so those stay a
    /// no-database-round-trip in-memory filter instead of re-querying the whole roster every
    /// time one row moves between the two grids. Empty until the first RefreshFullRosterAsync
    /// completes, which just means AvailableEmployeeRows starts empty too, the same way
    /// PayrollGroupRows does before its own first RefreshPayrollGroupRowsAsync.</summary>
    private List<Employee> _fullRoster = [];

    /// <summary>Same defer-until-idle role as _payrollGroupRefreshPending above, for
    /// RequestFullRosterRefresh() instead of RequestPayrollGroupRefresh() -- kept as its own
    /// flag rather than folded into that one since the two are fired from the same place
    /// (OnBatchScopeEmployeesChanged) but are NOT always both pending at once: a suppressed
    /// change (see _suppressGroupRefreshOnScopeChange) skips RequestPayrollGroupRefresh
    /// entirely but still calls RebuildAvailableEmployeeRows directly, with no roster reload
    /// needed at all.</summary>
    private bool _fullRosterRefreshPending;

    /// <summary>Set by RemoveEmployeeFromGroupAsync/AddEmployeesToGroupAsync/
    /// AddEmployeeToGroupAsync around their own BatchScopeEmployees assignment, so
    /// OnBatchScopeEmployeesChanged's own RequestPayrollGroupRefresh() call is skipped for
    /// just that one assignment. Every caller already patches PayrollGroupRows itself --
    /// removing or adding only the row(s) that actually changed -- instead of paying for
    /// RefreshPayrollGroupRowsAsync's Clear()+recompute-everyone rebuild to reflect a change
    /// that doesn't touch anyone else's NetPay. Every other BatchScopeEmployees assignment
    /// (from PayrollRunViewModel's NewPayrollRun/LoadPayrollGroupAsync) is a genuinely new roster
    /// and leaves this false, so those still go through the normal full refresh.</summary>
    private bool _suppressGroupRefreshOnScopeChange;

    public PayrollGroupViewModel(
        MainViewModel mainViewModel,
        ActiveRosterProvider rosterProvider,
        IPayrollComputationService payrollComputationService,
        IPayrollRunRepository payrollRunRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope,
        PayrollSummaryViewModel summary,
        AttendanceDataVersion dataVersion)
    {
        _mainViewModel = mainViewModel;
        _rosterProvider = rosterProvider;
        _payrollComputationService = payrollComputationService;
        _payrollRunRepository = payrollRunRepository;
        _statusBarService = statusBarService;
        _busy = busy;
        _scope = scope;
        _summary = summary;
        _dataVersion = dataVersion;

        // Replaces the direct PayrollGroupRows reach-in LoadCoreAsync used to make back when
        // both concerns lived on one class -- see EmployeeNetPayComputed's own doc comment and
        // this class's own doc comment for the "Group <-> Summary" wrinkle this closes. Patches
        // just the one row whose NetPay actually changed, same "opportunistic sync, not a full
        // rebuild" spirit RefreshPayrollGroupRowsAsync's own doc comment describes elsewhere.
        _summary.EmployeeNetPayComputed += (employeeId, netPay) =>
        {
            var row = PayrollGroupRows.FirstOrDefault(r => r.Employee.Id == employeeId);
            if (row is not null) row.NetPay = netPay;
        };

        // Trimmed to just this class's own concern (the group-membership commands) as of
        // build-order step 3 -- the Run/Print-Export commands this didn't cover moved to
        // PayrollRunViewModel's own NotifyRunCommands() (step 4) and
        // PayrollPrintExportViewModel's own NotifyPrintExportCommands() (step 5), each
        // notified from its own, independent _busy.PropertyChanged subscription. Same
        // "PropertyChanged -> NotifyCanExecuteChanged, immediately, every time, no artificial
        // hold" behavior as those two -- see either one's own doc comment for why smoothing
        // this independently never actually worked.
        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AttendanceBusyState.IsRunning)) return;

            NotifyGroupCommands();

            // Picks up a payroll-group-scope change that arrived while a previous requery was
            // still in flight -- RequestPayrollGroupRefresh() below deliberately does NOT
            // start a second, concurrent requery against the shared, app-lifetime-scoped
            // ScheduleDbContext while _busy.IsRunning is already true (see its own doc
            // comment); this is what stops that deferred change from just going stale instead.
            // Guarded on _payrollGroupRefreshPending so a run that finishes with nothing new
            // queued (the overwhelmingly common case) doesn't trigger a pointless extra reload
            // of what it just loaded. PayrollSummaryViewModel's own _busy.PropertyChanged
            // subscription runs the exact same "pending? re-run" check for its own
            // _refreshPending/RequestRefresh independently -- see that class's own doc
            // comment.
            if (!_busy.IsRunning && _payrollGroupRefreshPending)
            {
                _payrollGroupRefreshPending = false;
                RequestPayrollGroupRefresh();
            }

            // Same idea, for _fullRoster/AvailableEmployeeRows -- see
            // _fullRosterRefreshPending's own doc comment for why this is a third,
            // independent flag rather than folded into either check above.
            if (!_busy.IsRunning && _fullRosterRefreshPending)
            {
                _fullRosterRefreshPending = false;
                RequestFullRosterRefresh();
            }
        };

        // Mirrors ReportViewModel's SummaryRowsView setup -- see PayrollGroupRowsView's own
        // doc comment. PayrollGroupGrid binds to this instead of PayrollGroupRows directly, so
        // typing in the search box narrows the grid without touching the underlying rows
        // (RefreshPayrollGroupRowsAsync/the EmployeeNetPayComputed handler above both still
        // read/write PayrollGroupRows itself, same as before). The ObservableCollection's own
        // Add/Clear calls raise CollectionChanged, which the default view picks up on its own
        // -- only a search-text change needs an explicit Refresh() (see
        // OnPayrollGroupSearchTextChanged), same split SummaryRowsView/FilterSummaryRow
        // already follows.
        PayrollGroupRowsView = CollectionViewSource.GetDefaultView(PayrollGroupRows);
        PayrollGroupRowsView.Filter = FilterPayrollGroupRow;

        // PayrollGroupGrid has CanUserSortColumns="False" (a flat table, not the tree it
        // replaced, so there's no natural grouping order to preserve by disabling sort the way
        // the tree's own department/employee order does) -- this SortDescription is what
        // actually keeps the grid alphabetical by employee name instead of
        // RefreshPayrollGroupRowsAsync's own BatchScopeEmployees enumeration order (itself
        // following the tree's department-then-employee order, not a name order).
        // ListCollectionView applies SortDescriptions on every Add, so this stays correct as
        // RefreshPayrollGroupRowsAsync clears and repopulates PayrollGroupRows on each
        // period/scope change, with no extra Refresh() call needed the way
        // OnPayrollGroupSearchTextChanged needs one for the Filter above.
        PayrollGroupRowsView.SortDescriptions.Add(
            new SortDescription(nameof(PayrollGroupRow.EmployeeName), ListSortDirection.Ascending));

        // Same "grid binds to a live-filtered view over the real collection" shape as
        // PayrollGroupRowsView just above, for AvailableEmployeeRows (the "Not in group" grid
        // under it -- see that property's own doc comment) instead of PayrollGroupRows. Shares
        // PayrollGroupSearchText's own search box rather than getting one of its own --
        // OnPayrollGroupSearchTextChanged below Refreshes both views together, so typing
        // narrows the whole panel, not just whichever grid happens to be on top.
        AvailableEmployeeRowsView = CollectionViewSource.GetDefaultView(AvailableEmployeeRows);
        AvailableEmployeeRowsView.Filter = FilterAvailableEmployeeRow;
        AvailableEmployeeRowsView.SortDescriptions.Add(
            new SortDescription(nameof(AvailableEmployeeRow.EmployeeName), ListSortDirection.Ascending));

        // Keeps TotalNetPay live as PayrollGroupRows itself changes shape -- same "subscribe
        // once per child as it's added, no unsubscribe needed since a discarded row taking its
        // subscription with it is harmless" convention DepartmentGroupViewModel.
        // AttachChildNotifications already follows, just re-attached on every Add rather than
        // once up front since RefreshPayrollGroupRowsAsync clears and rebuilds this collection
        // instead of it being fixed at construction time the way DepartmentGroupViewModel.
        // Employees is. Deliberately keyed off the collection itself, not
        // PayrollGroupRowsView -- TotalNetPay always reflects every employee in the group
        // regardless of whatever PayrollGroupSearchText currently has on screen (see
        // TotalNetPay's own doc comment for why), so it has no reason to listen to the
        // filtered view at all.
        PayrollGroupRows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (PayrollGroupRow row in e.NewItems)
                    row.PropertyChanged += (_, rowArgs) =>
                    {
                        if (rowArgs.PropertyName == nameof(PayrollGroupRow.NetPay))
                            OnPropertyChanged(nameof(TotalNetPay));
                    };

            // Covers both the Add case above (a freshly-added row already carries its final
            // NetPay -- or null for a no-Pin employee -- since RefreshPayrollGroupRowsAsync now
            // computes every row before any of them reach PayrollGroupRows at all; see that
            // method's own doc comment) and Clear/Reset (RefreshPayrollGroupRowsAsync's own
            // PayrollGroupRows.Clear() before repopulating) -- either way, the set of rows
            // TotalNetPay sums over just changed, so it always needs its own notification too,
            // on top of whatever the loop above just wired up for future NetPay changes on the
            // new rows themselves (e.g. the EmployeeNetPayComputed handler's opportunistic
            // per-row patch).
            OnPropertyChanged(nameof(TotalNetPay));
        };

        // Replaces the four CommunityToolkit.Mvvm-generated OnPeriodStartChanged/
        // OnPeriodEndChanged/OnBatchScopeEmployeesChanged/OnActivePayrollRunIdChanged
        // partial-method hooks that used to fire automatically whenever those properties lived
        // directly on PayrollViewModel -- now that their storage lives on the shared
        // PayrollScopeState (see _scope's own doc comment), this subscription is what invokes
        // this class's own reaction methods (still below) whenever _scope actually changes.
        // Splits out of what used to be one switch on PayrollViewModel covering all four
        // eventual concerns at once -- see PayrollSummaryViewModel's own, differently-shaped
        // _scope.PropertyChanged subscription for the sibling concern's reaction to the same
        // three of these four properties (it has no reaction to BatchScopeEmployees, unlike
        // this class).
        _scope.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PayrollScopeState.PeriodStart):
                    OnPeriodStartChanged();
                    break;
                case nameof(PayrollScopeState.PeriodEnd):
                    OnPeriodEndChanged();
                    break;
                case nameof(PayrollScopeState.BatchScopeEmployees):
                    OnBatchScopeEmployeesChanged();
                    break;
                case nameof(PayrollScopeState.ActivePayrollRunId):
                    OnActivePayrollRunIdChanged();
                    break;
            }
        };

        // Explicit rather than relying solely on the "= []" field initializer above to keep
        // this empty -- EmptyStateOverlay's HasBatchScope binding (see that Border's own doc
        // comment in PayrollPage.xaml) depends on a freshly-constructed payroll tab genuinely
        // starting with no batch scope, so this makes that guarantee explicit here instead of
        // leaving it as an easy-to-break side effect of whatever PayrollScopeState's own field
        // happens to default to. Placed last (everything else above -- _busy,
        // PayrollGroupRowsView/AvailableEmployeeRowsView, PayrollGroupRows.CollectionChanged,
        // the _scope subscription -- is already wired by this point) since
        // OnBatchScopeEmployeesChanged's own RebuildAvailableEmployeeRows/
        // RequestPayrollGroupRefresh/RequestFullRosterRefresh all reach into those. Guarded on
        // Count > 0 so this stays a genuine no-op in the ordinary case where PayrollScopeState's
        // own default already left it empty; only does real work if some future change to that
        // default ever stops being empty. Since PayrollViewModel isn't built until the Payroll
        // tab is first navigated to (see PayrollSummaryViewModel's own constructor comment on
        // its RequestRefresh() call), this in practice means "the payroll group is always empty
        // the first time Payroll is opened after the program starts" -- there's no separate
        // "program start" hook to clear this from, because construction itself IS that start.
        if (_scope.BatchScopeEmployees.Count > 0)
            _scope.BatchScopeEmployees = [];
    }

    /// <summary>One PayrollGroupRow per BatchScopeEmployees entry — what PayrollPage's
    /// left-column DataGrid (Employee Name / Department / Net Pay) binds to (via
    /// PayrollViewModel's own forwarding). Built all-at-once by RefreshPayrollGroupRowsAsync:
    /// every employee is computed into a local, off-collection list first, and only once every
    /// row's NetPay is known does that method clear and repopulate this collection, so the
    /// grid replaces its old contents with the new ones in a single update instead of clearing
    /// to empty and then filling in row by row (with NetPay itself flashing blank-then-value on
    /// each row) while the person watches.
    ///
    /// Empty until BatchScopeEmployees is populated (i.e. until "New Payroll Run…" or "Load
    /// Payroll Group…" has been run this session). Cleared and rebuilt by
    /// RefreshPayrollGroupRowsAsync on every period change or scope change; individual rows'
    /// NetPay values are also patched in place opportunistically as PayrollSummaryViewModel.
    /// LoadCoreAsync recomputes each employee's own breakdown, without waiting on a full
    /// rebuild -- via the EmployeeNetPayComputed subscription in this class's own constructor
    /// (see that event's own doc comment on PayrollSummaryViewModel for why it's an event, not
    /// a direct reach-in, as of build-order step 2; wired back up here as of build-order step
    /// 3).</summary>
    public ObservableCollection<PayrollGroupRow> PayrollGroupRows { get; } = [];

    /// <summary>What PayrollGroupGrid actually binds to (via PayrollViewModel's own
    /// forwarding) instead of PayrollGroupRows directly -- same "grid binds to a live-filtered
    /// view over the real collection" shape as ReportViewModel.SummaryRowsView, just filtered
    /// on PayrollGroupSearchText (see FilterPayrollGroupRow) rather than the report-scope
    /// tree's checked state. Built once in the constructor, since CollectionViewSource.
    /// GetDefaultView returns the same view instance for a given source collection every time
    /// it's asked -- there's nothing to rebuild when PayrollGroupRows itself is
    /// cleared/repopulated by RefreshPayrollGroupRowsAsync, only when the *filter* criterion
    /// (the search text) changes.</summary>
    public ICollectionView PayrollGroupRowsView { get; }

    /// <summary>One AvailableEmployeeRow per roster employee NOT in BatchScopeEmployees --
    /// what PayrollPage's "Not in group" DataGrid (Employee Name / Department, under
    /// PayrollGroupGrid -- see PayrollPage.xaml) binds to via AvailableEmployeeRowsView below.
    /// Rebuilt wholesale by RebuildAvailableEmployeeRows every time BatchScopeEmployees or
    /// _fullRoster changes -- there's no per-row NetPay to patch in place the way
    /// PayrollGroupRows' opportunistic sync does, so a Clear()+repopulate is always cheap
    /// enough here. Empty until _fullRoster's own first load (see that field's doc
    /// comment).</summary>
    public ObservableCollection<AvailableEmployeeRow> AvailableEmployeeRows { get; } = [];

    /// <summary>Same "grid binds to a live-filtered view over the real collection" role as
    /// PayrollGroupRowsView, for AvailableEmployeeRows instead of PayrollGroupRows -- see that
    /// property's own doc comment.</summary>
    public ICollectionView AvailableEmployeeRowsView { get; }

    /// <summary>Sum of every row's own NetPay across the whole payroll group -- what
    /// PayrollPage's totals strip under PayrollGroupGrid binds to. Deliberately sums
    /// PayrollGroupRows itself, not PayrollGroupRowsView's filtered subset -- this is meant to
    /// read as "the total that'll be disbursed for this run", a fixed figure that shouldn't
    /// silently shrink just because someone typed a department name into PayrollGroupSearchText
    /// to find one employee's row; a filtered subtotal that looks like the run's whole total
    /// would be an easy way to misread actual payroll cost. A row with NetPay still null (no
    /// Pin) contributes 0 rather than being skipped, the same way NumberConverter would
    /// blank-render it individually. Since RefreshPayrollGroupRowsAsync now computes every row
    /// before PayrollGroupRows is touched at all (see that method's and PayrollGroupRows' own
    /// doc comments), this jumps straight to the run's real total the moment the collection is
    /// repopulated, rather than climbing row by row. Plain computed property (not
    /// [ObservableProperty]) since nothing ever sets it directly -- see the constructor's
    /// PayrollGroupRows.CollectionChanged subscription for what actually triggers its change
    /// notification, same "OnPropertyChanged fired by whatever changed the underlying data"
    /// idea PayrollWizardViewModel.TotalNetPay's own doc comment describes for the wizard's
    /// review grid.</summary>
    public decimal TotalNetPay => PayrollGroupRows.Sum(r => r.NetPay ?? 0m);

    /// <summary>True while RefreshPayrollGroupRowsAsync or AddEmployeesToGroupAsync is
    /// actively computing rows for the Payroll Group table. Since both methods build their
    /// rows off-collection and only touch PayrollGroupRows once every row is done (see
    /// RefreshPayrollGroupRowsAsync's own doc comment for why -- the grid should replace its
    /// contents in one redraw, not fill in row by row), the grid itself sits unchanged for the
    /// whole several-second run; this and PayrollGroupLoadPercent drive the status bar's own
    /// left-aligned progress indicator (see IStatusBarService.ShowProgress/ClearProgress) so
    /// the person still sees something is happening in the meantime. Deliberately separate
    /// from PayrollSummaryViewModel.IsBusy/_busy.IsVisiblyRunning -- both group methods call
    /// _busy.RunAsync with visibly: false (see RefreshPayrollGroupRowsAsync's own doc comment
    /// on why), so IsBusy's progress bar over on PayrollSummaryView/EmployeeAttendancePanel
    /// deliberately does NOT appear for this background requery; this flag drives its own,
    /// narrower indicator instead. Always reset to false in a finally, so a cancelled or
    /// failed run doesn't leave the status bar stuck reading "Computing…". Deliberately does
    /// NOT drive anything in the Payroll Group panel itself (the grid's Net Pay header is a
    /// plain, static string) -- only the status bar shows this progress.</summary>
    [ObservableProperty]
    private bool isPayrollGroupLoading;

    partial void OnIsPayrollGroupLoadingChanged(bool value)
    {
        if (value)
            _statusBarService.ShowProgress("Computing payroll group…", PayrollGroupLoadPercent);
        else
            _statusBarService.ClearProgress();
    }

    /// <summary>0-100 -- how far RefreshPayrollGroupRowsAsync/AddEmployeesToGroupAsync's
    /// current compute loop has gotten, as "employees computed so far" / "employees in this
    /// run", rounded to the nearest whole percent. Only meaningful while IsPayrollGroupLoading
    /// is true; the status bar's left-aligned progress bar (see the handler right after this
    /// property) is what actually surfaces it, since PayrollGroupRows itself isn't touched (and
    /// so can't show per-row progress) until the whole batch is done.</summary>
    [ObservableProperty]
    private int payrollGroupLoadPercent;

    partial void OnPayrollGroupLoadPercentChanged(int value)
    {
        if (IsPayrollGroupLoading)
            _statusBarService.ShowProgress("Computing payroll group…", value);
    }

    /// <summary>Free-text filter for the Payroll Group table -- comma-separated terms, each
    /// matched against the employee's Employee ID (exact, numeric terms only), first name,
    /// last name, or department name, same matching rule EmployeeTreeSearchFilter applies to
    /// the Schedule/Employees/Report Scope trees (see FilterPayrollGroupRow). Duplicated here
    /// rather than reused directly since this table has no tree to walk -- it's a flat
    /// PayrollGroupRows list, so there's no per-department "surface all its employees" step the
    /// tree version needs. Purely a display filter, same role EmployeeTreeSearchText/
    /// ReportScopeViewModel.SearchText/PunchRecordsViewModel.LogViewSearchText already play
    /// elsewhere -- narrows what's shown in PayrollGroupRowsView, never touches
    /// PayrollGroupRows itself or which employee is selected. Not persisted across sessions,
    /// same as EmployeeTreeSearchText.</summary>
    [ObservableProperty]
    private string payrollGroupSearchText = string.Empty;

    partial void OnPayrollGroupSearchTextChanged(string value)
    {
        PayrollGroupRowsView.Refresh();
        AvailableEmployeeRowsView.Refresh();
    }

    /// <summary>See PayrollGroupSearchText's own doc comment for the matching rule this
    /// implements. Reads straight off row.Employee rather than the row's own EmployeeName/
    /// DepartmentName display strings, so a Pin match doesn't have to first round-trip through
    /// "LastName, FirstName" formatting the way EmployeeName does.</summary>
    private bool FilterPayrollGroupRow(object obj)
    {
        if (obj is not PayrollGroupRow row) return false;

        var terms = PayrollGroupSearchText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        return terms.Any(t =>
            (int.TryParse(t, out var id) && row.Employee.Pin == id) ||
            row.Employee.FirstName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            row.Employee.LastName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            row.DepartmentName.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Same matching rule as FilterPayrollGroupRow above, just against an
    /// AvailableEmployeeRow instead of a PayrollGroupRow -- kept as its own method rather than
    /// a shared helper since the two row types have no common base to read
    /// Employee/DepartmentName off of.</summary>
    private bool FilterAvailableEmployeeRow(object obj)
    {
        if (obj is not AvailableEmployeeRow row) return false;

        var terms = PayrollGroupSearchText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        return terms.Any(t =>
            (int.TryParse(t, out var id) && row.Employee.Pin == id) ||
            row.Employee.FirstName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            row.Employee.LastName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            row.DepartmentName.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drives EmptyStateOverlay's own Visibility in PayrollPage.xaml (via
    /// PayrollViewModel's own forwarding) -- a named projection of BatchScopeEmployees.Count >
    /// 0 so that binding reads as intent ("is there an active batch") rather than an inline
    /// count comparison. False (overlay shown) covers the whole page, not just the Payroll
    /// Group column, until "New Payroll Run…" or "Load Payroll Group…" is completed once this
    /// session -- see that Border's own doc comment for why that in practice means "the first
    /// time Payroll is selected after the program starts", even though nothing here is keyed
    /// off navigation events directly.</summary>
    public bool HasBatchScope => _scope.BatchScopeEmployees.Count > 0;

    /// <summary>Formerly a CommunityToolkit.Mvvm-generated `partial void
    /// OnPeriodStartChanged(DateTime value)` hook on PayrollViewModel, invoked automatically
    /// whenever PeriodStart's own [ObservableProperty] field changed; now invoked explicitly
    /// from this class's own _scope.PropertyChanged subscription in the constructor instead,
    /// since PayrollScopeState (not this class) owns that storage. The `value` parameter was
    /// never used by the body, so it's dropped rather than threaded through -- read
    /// _scope.PeriodStart directly if a future change needs it.</summary>
    private void OnPeriodStartChanged()
    {
        RequestPayrollGroupRefresh();
    }

    /// <summary>Same "no longer a generated partial-method hook, invoked explicitly from
    /// _scope.PropertyChanged instead" story as OnPeriodStartChanged just above.</summary>
    private void OnPeriodEndChanged()
    {
        RequestPayrollGroupRefresh();
    }

    /// <summary>The RequestPayrollGroupRefresh() call below is a full Clear()+recompute-
    /// everyone rebuild, appropriate for "Start Payroll Period…"/"Load Payroll Group…"
    /// replacing the roster outright but wasteful for RemoveEmployeeFromGroupAsync/
    /// AddEmployeesToGroupAsync/AddEmployeeToGroupAsync, which only ever change the group by
    /// one (or a few) rows and already patch PayrollGroupRows themselves -- see
    /// _suppressGroupRefreshOnScopeChange's own doc comment for how those skip it.
    ///
    /// Formerly a CommunityToolkit.Mvvm-generated `partial void
    /// OnBatchScopeEmployeesChanged(IReadOnlyList&lt;Employee&gt; value)` hook on
    /// PayrollViewModel -- now invoked explicitly from this class's own _scope.PropertyChanged
    /// subscription in the constructor instead, since PayrollScopeState (not this class) owns
    /// that storage. The `value` parameter was never used by the body, so it's dropped rather
    /// than threaded through -- read _scope.BatchScopeEmployees directly if a future change
    /// needs it.</summary>
    private void OnBatchScopeEmployeesChanged()
    {
        OnPropertyChanged(nameof(HasBatchScope));

        // Cheap in-memory re-filter of whatever _fullRoster already holds -- correct for every
        // case, suppressed or not: a suppressed single-row Add/Remove only moves one employee
        // between the two grids (no roster reload needed at all), and a wholesale replace
        // still wants AvailableEmployeeRows to reflect the new scope immediately even before
        // RequestFullRosterRefresh's own reload below returns.
        RebuildAvailableEmployeeRows();

        if (_suppressGroupRefreshOnScopeChange) return;
        RequestPayrollGroupRefresh();
        RequestFullRosterRefresh();
    }

    /// <summary>Fired whenever a new run is saved or loaded (PayrollRunViewModel's own
    /// NewPayrollRun/LoadPayrollGroupAsync, extracted as of build-order step 4) -- both set
    /// ActivePayrollRunId as part of their five-property
    /// landing sequence. Notifies the three group-membership commands whose CanExecute
    /// (CanEditGroupMembership) depends on this being non-null.
    ///
    /// Formerly a CommunityToolkit.Mvvm-generated `partial void
    /// OnActivePayrollRunIdChanged(int? value)` hook on PayrollViewModel -- now invoked
    /// explicitly from this class's own _scope.PropertyChanged subscription in the constructor
    /// instead, since PayrollScopeState (not this class) owns that storage. The `value`
    /// parameter was never used by the body, so it's dropped rather than threaded through --
    /// read _scope.ActivePayrollRunId directly if a future change needs it.</summary>
    private void OnActivePayrollRunIdChanged()
    {
        RemoveEmployeeFromGroupCommand.NotifyCanExecuteChanged();
        AddEmployeesToGroupCommand.NotifyCanExecuteChanged();
        AddEmployeeToGroupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Gate and defer counterpart to PayrollSummaryViewModel's own RequestRefresh(),
    /// for the Payroll Group table instead of the single-employee detail panel. Returns early
    /// and clears PayrollGroupRows when BatchScopeEmployees is empty or the period is backwards
    /// (same guard RequestRefresh itself uses). Defers via _payrollGroupRefreshPending when
    /// _busy.IsRunning (same _busy.PropertyChanged handler re-runs it once IsRunning drops back
    /// to false). Otherwise fires RefreshPayrollGroupRowsAsync directly.</summary>
    private void RequestPayrollGroupRefresh()
    {
        if (_scope.BatchScopeEmployees.Count == 0 || _scope.PeriodEnd < _scope.PeriodStart)
        {
            PayrollGroupRows.Clear();
            _payrollGroupRefreshPending = false;
            return;
        }

        if (_busy.IsRunning)
        {
            _payrollGroupRefreshPending = true;
            return;
        }

        _ = RefreshPayrollGroupRowsAsync();
    }

    /// <summary>The actual compute behind RequestPayrollGroupRefresh() — one
    /// IPayrollComputationService.PrepareBatchAsync call for every BatchScopeEmployees pin at
    /// once, then ComputeOneFromBatchAsync once per employee against that shared result
    /// (instead of looping ComputeOneAsync, which used to re-run PrepareBatchAsync's own
    /// company-wide attendance fetch once per employee -- see PayrollBatchContext's own doc
    /// comment). Builds each row into a local `rows` list that PayrollGroupRows itself is not
    /// touched by. Only once every employee has been computed does PayrollGroupRows get
    /// cleared and repopulated from `rows`, in one tight loop with no awaits in between -- so
    /// the DataGrid (and the TotalNetPay strip below it) replace their old contents with the
    /// new ones in a single redraw, instead of clearing to empty and then filling in one row
    /// at a time with NetPay itself flashing blank-then-value as each compute returns.
    ///
    /// IsPayrollGroupLoading/PayrollGroupLoadPercent track the per-employee loop only (not
    /// the PrepareBatchAsync call ahead of it) -- unlike PayrollGroupRows, there's no reason
    /// to defer those, since they're what drives the status bar's own left-aligned progress
    /// indicator (see IStatusBarService.ShowProgress/ClearProgress) while the grid body sits
    /// still. Wrapped in try/finally so IsPayrollGroupLoading always drops back to false --
    /// including if cancellationToken fires partway through -- rather than leaving the status
    /// bar stuck reading "Computing…".
    ///
    /// visibly: false — this is a background requery the person didn't explicitly trigger, so
    /// it should not flip IsVisiblyRunning and make PayrollSummaryViewModel's own progress bar
    /// appear on its own. If it happens to run nested inside an already-visible refresh, the
    /// outer refresh's visibly: true already governs the bar.</summary>
    private async Task RefreshPayrollGroupRowsAsync()
    {
        var employees = _scope.BatchScopeEmployees;
        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);
        var succeeded = true;

        // Read at the START of the load, not the end -- see _loadedScheduleVersion's own
        // doc comment for why a version bump racing in mid-load still needs to be noticed
        // on the next revisit rather than silently absorbed as "already accounted for."
        // Only committed to the field below once this whole run has actually succeeded.
        var scheduleVersionAtLoadStart = _dataVersion.ScheduleVersion;
        var holidayVersionAtLoadStart = _dataVersion.HolidayVersion;
        var attendanceInputsAtLoadStart = _dataVersion.AttendanceInputs;

        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            IsPayrollGroupLoading = true;
            PayrollGroupLoadPercent = 0;

            try
            {
                var pins = employees.Select(e => e.Pin).ToHashSet();
                var batch = await _payrollComputationService.PrepareBatchAsync(pins, start, end, cancellationToken);

                // Seeds/corrects every employee's SSS/PhilHealth/Pag-IBIG/Premium Pay/
                // Allowance/Cash Advance rows for the whole group in at most two round trips
                // total, rather than letting each ComputeOneFromBatchAsync call below do its
                // own per-employee seed pass -- see SeedContributionsForBatchAsync's own doc
                // comment. batch is reassigned to its returned context (ContributionsSeeded:
                // true) so the loop below skips straight to PayrollCalculator.Calculate with
                // nothing left to write.
                batch = await _payrollComputationService.SeedContributionsForBatchAsync(
                    employees, start, end, batch, cancellationToken);

                var rows = new List<PayrollGroupRow>(employees.Count);
                for (var i = 0; i < employees.Count; i++)
                {
                    var employee = employees[i];
                    var row = new PayrollGroupRow { Employee = employee };
                    rows.Add(row);

                    var (result, _) = await _payrollComputationService.ComputeOneFromBatchAsync(
                        employee, start, end, batch, cancellationToken);

                    row.NetPay = result.NetPay;

                    PayrollGroupLoadPercent = (i + 1) * 100 / employees.Count;

                    // WPF's async continuations post at DispatcherPriority.Normal, which
                    // outranks the DispatcherPriority.Render work that would actually repaint
                    // the status bar's progress fill just set above -- without this yield, a
                    // loop whose ComputeOneFromBatchAsync calls resolve quickly (purely
                    // in-memory now that SeedContributionsForBatchAsync above has already
                    // written every contribution row this group needs, so an iteration here
                    // never itself does a database round trip the way it occasionally used to)
                    // never lets a Render pass run at all until the whole loop is done, and the
                    // person sees
                    // nothing change until it's over (see
                    // https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.yield
                    // for the same "async work starves Render" story). Yielding at Background --
                    // lower priority than Render -- forces this method to step aside and let
                    // the pending repaint actually happen before the next employee's compute
                    // starts. Safe to do every iteration now that PercentProgressBar no longer
                    // re-binds Grid Star-width ColumnDefinitions on each Value change (see that
                    // control's own doc comment) -- this yield was never the problem, the Star
                    // rebinding was.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                PayrollGroupRows.Clear();
                foreach (var row in rows)
                    PayrollGroupRows.Add(row);
            }
            finally
            {
                IsPayrollGroupLoading = false;
            }
        },
        onError: ex =>
        {
            succeeded = false;
            _statusBarService.ShowError(ex.Message, "Could not refresh payroll group");
        });

        if (succeeded)
        {
            _loadedScheduleVersion = scheduleVersionAtLoadStart;
            _loadedHolidayVersion = holidayVersionAtLoadStart;
            _loadedAttendanceInputs = attendanceInputsAtLoadStart;
        }
    }

    /// <summary>Closes the one gap a page-level "was I on a different tab" check can't
    /// close on its own: unlike ReportViewModel (Attendance's own Summary tab, checked via
    /// IsSummaryTabSelected/RecheckOnPageRevisit), the Payroll page has no separate "which
    /// sub-tab is showing" concept for the schedule-edit case, since there's nowhere on
    /// this page to edit a schedule from in the first place -- the ONLY way to change a
    /// schedule while a payroll group is loaded here is to navigate away to the Schedule
    /// page, edit there, and navigate back. Called from PayrollPage.OnNavigatedToAsync
    /// (via PayrollViewModel's own forwarding), every visit, not just the first -- same
    /// "page revisit is the trigger, not a tab flip" reasoning as
    /// ReportViewModel.RecheckOnPageRevisit's own doc comment.
    ///
    /// Genuinely awaited all the way up to PayrollPage.OnNavigatedToAsync, unlike
    /// RequestPayrollGroupRefresh's own fire-and-forget `_ = RefreshPayrollGroupRowsAsync()`
    /// -- deliberately NOT that same pattern here. PayrollPage's own constructor also wires
    /// PayrollGroupRows.CollectionChanged to re-dispatch RestorePayrollGroupSelection (see
    /// that constructor's own comment), which cascades through SelectBatchEmployeeCommand
    /// into PayrollSummaryViewModel.RequestRefresh -- a second, independent user of the
    /// same shared, app-lifetime-scoped ScheduleDbContext (see App.xaml.cs's own
    /// AttendanceBusyState registration for why that context can't tolerate two operations
    /// in flight at once). RestorePayrollGroupSelection is dispatched via
    /// Dispatcher.BeginInvoke, so it can only ever run after OnNavigatedToAsync itself
    /// returns -- awaiting the refresh all the way through here is what guarantees that
    /// return doesn't happen until this method's own database work has genuinely finished,
    /// rather than leaving that guarantee to depend on exactly how _busy's own
    /// gate/reentrancy tracking happens to interleave with a separately-queued dispatcher
    /// callback. Calls RefreshPayrollGroupRowsAsync() directly, not through
    /// RequestPayrollGroupRefresh's own _payrollGroupRefreshPending defer-and-replay dance
    /// -- that dance exists for PropertyChanged handlers, which can't be async themselves;
    /// this method already is, so it can just let RefreshPayrollGroupRowsAsync's own
    /// _busy.RunAsync call wait its turn (if something else genuinely is running) the
    /// normal way, no separate pending flag needed.
    ///
    /// Recomputes the WHOLE group (not just the touched employee's own row) once
    /// _dataVersion.AnyScheduleChangeSince says at least one currently-loaded employee's
    /// schedule moved -- which now covers every schedule write in the app: Set/Clear Schedule
    /// and Set Leave (single and bulk), Import Schedule, and Delete Employee all call
    /// BumpScheduleForEmployees with the Pins they touched, so none of them needs a manual
    /// fallback to be noticed here. (Import and Delete used to bump only the raw ScheduleVersion
    /// counter, which this deliberately doesn't compare against, leaving them invisible to this
    /// check; see BumpScheduleForEmployees' own doc comment for why per-pin is the right shape
    /// and what "no caller left" means for the plain BumpSchedule().)
    ///
    /// Checked alongside two coarse `!=` comparisons this method runs first -- _loadedHolidayVersion
    /// and _loadedAttendanceInputs -- which between them cover the company-wide inputs that have
    /// no per-employee dimension to narrow by. See each field's own doc comment.
    ///
    /// A no-op, database-round-trip-wise, whenever nothing relevant changed -- the overwhelming
    /// majority of revisits -- since all three checks are pure in-memory comparisons.
    ///
    /// Deliberately does NOT check the payroll group's own date range against the edited
    /// dates -- RefreshPayrollGroupRowsAsync always recomputes against _scope's own
    /// current PeriodStart/PeriodEnd, and IAttendanceRunner.RunAsync only ever reads
    /// schedule/attendance data inside that window, so an edit to a date genuinely outside
    /// the loaded group's period can't change what gets recomputed either way -- worst
    /// case here is one harmless recompute that reproduces the exact same numbers, not a
    /// wrong one.</summary>
    internal async Task RecheckOnPageRevisitAsync()
    {
        var pins = _scope.BatchScopeEmployees.Select(e => e.Pin).ToList();

        if (pins.Count == 0) return;

        // A holiday added/removed/edited anywhere (calendar right-click or ManageHolidaysDialog)
        // since this group was loaded -- recompute the whole group unconditionally, since
        // Holiday Pay is company-wide and there's no per-pin holiday tracking to narrow it
        // the way AnyScheduleChangeSince narrows a schedule edit below.
        if (_dataVersion.HolidayVersion != _loadedHolidayVersion)
        {
            await RefreshPayrollGroupRowsAsync();
            return;
        }

        // A device-log import, manual-entry edit, or punch-pairing edit since this group was
        // loaded moves the hours behind every affected employee's NetPay -- recompute the whole
        // group unconditionally, same as the holiday check above and for the same reason: none
        // of the three counters behind AttendanceInputs is per-employee, so there's nothing to
        // narrow it to this group's pins with. See _loadedAttendanceInputs' own doc comment.
        if (_dataVersion.AttendanceInputs != _loadedAttendanceInputs)
        {
            await RefreshPayrollGroupRowsAsync();
            return;
        }

        if (!_dataVersion.AnyScheduleChangeSince(pins, _loadedScheduleVersion)) return;

        await RefreshPayrollGroupRowsAsync();
    }

    /// <summary>Gate and defer counterpart to RequestPayrollGroupRefresh(), for
    /// _fullRoster/AvailableEmployeeRows instead of PayrollGroupRows -- fired alongside it from
    /// OnBatchScopeEmployeesChanged whenever BatchScopeEmployees is wholesale-replaced (a fresh
    /// "New Payroll Run…"/"Load Payroll Group…"), so the "Not in group" grid ends up reflecting
    /// the current roster rather than whatever _fullRoster last happened to hold from an
    /// earlier group or session. Deliberately NOT fired from OnPeriodStartChanged/
    /// OnPeriodEndChanged the way RequestPayrollGroupRefresh is -- the roster has nothing to do
    /// with the period, so re-querying it on every date-picker edit would just be wasted round
    /// trips against the shared ScheduleDbContext. Same _busy.IsRunning gate/defer shape as
    /// RequestPayrollGroupRefresh, just tracking its own _fullRosterRefreshPending flag -- see
    /// that field's own doc comment for why it's separate.</summary>
    private void RequestFullRosterRefresh()
    {
        if (_busy.IsRunning)
        {
            _fullRosterRefreshPending = true;
            return;
        }

        _ = RefreshFullRosterAsync();
    }

    /// <summary>The actual roster load behind RequestFullRosterRefresh() -- the same
    /// _rosterProvider.GetAsync() pair AddEmployeesToGroupAsync's own picker-dialog load
    /// already uses, just cached into _fullRoster instead of feeding a tree, and run through
    /// _busy.RunAsync the same "never touch the shared, app-lifetime-scoped ScheduleDbContext
    /// outside the busy gate" rule every other read/write on this tab already follows.
    /// visibly: false for the same reason RefreshPayrollGroupRowsAsync's own compute loop is --
    /// this is a background requery triggered by BatchScopeEmployees changing, not something
    /// the person directly clicked a button for.</summary>
    private async Task RefreshFullRosterAsync()
    {
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            var (departments, unassigned) = await _rosterProvider.GetAsync(cancellationToken);

            _fullRoster = [.. departments.SelectMany(d => d.Employees), .. unassigned];
            RebuildAvailableEmployeeRows();
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not load employee roster"));
    }

    /// <summary>Rebuilds AvailableEmployeeRows from _fullRoster minus whoever's currently in
    /// BatchScopeEmployees, matched by Employee.Id. Purely in-memory -- no database round trip
    /// -- so this is cheap enough to call on every BatchScopeEmployees change (see
    /// OnBatchScopeEmployeesChanged), including AddEmployeeToGroupAsync/
    /// RemoveEmployeeFromGroupAsync/AddEmployeesToGroupAsync's own single- or few-row patches
    /// that deliberately skip the far more expensive PayrollGroupRows NetPay recompute (see
    /// _suppressGroupRefreshOnScopeChange's own doc comment) -- this list has no NetPay to
    /// recompute, so there's no equivalent cost to avoid here. A no-op (AvailableEmployeeRows
    /// just ends up empty) until _fullRoster itself has been loaded at least once by
    /// RefreshFullRosterAsync.</summary>
    private void RebuildAvailableEmployeeRows()
    {
        var currentIds = _scope.BatchScopeEmployees.Select(e => e.Id).ToHashSet();

        AvailableEmployeeRows.Clear();
        foreach (var employee in _fullRoster.Where(e => !currentIds.Contains(e.Id)))
            AvailableEmployeeRows.Add(new AvailableEmployeeRow { Employee = employee });
    }

    /// <summary>The command notifications gated on _busy.IsRunning that this class owns --
    /// factored out of the _busy.PropertyChanged handler's IsRunning case so the disable
    /// (IsRunning going true) and the re-enable (IsRunning going false) call the exact same
    /// list, both immediately, rather than two copies that could drift apart. Covers the
    /// three group-membership commands (Remove/Add-one/Add-many) -- Select carries no
    /// CanExecute (see SelectBatchEmployee's own doc comment) so it isn't included. Named
    /// distinctly from PayrollRunViewModel's own NotifyRunCommands() and
    /// PayrollPrintExportViewModel's own NotifyPrintExportCommands() -- each covers only the
    /// commands that moved onto its own class (build-order steps 4 and 5 respectively); this
    /// one covers just these three. PayrollViewModel's own NotifyToolbarCommands, which used
    /// to cover whatever hadn't moved off the facade yet, was retired entirely once step 5
    /// gave away its last two entries -- see the facade's own constructor doc comment.</summary>
    private void NotifyGroupCommands()
    {
        RemoveEmployeeFromGroupCommand.NotifyCanExecuteChanged();
        AddEmployeesToGroupCommand.NotifyCanExecuteChanged();
        AddEmployeeToGroupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Bound to each PayrollGroup table row's click (see PayrollPage.xaml.cs's
    /// PayrollGroupGrid_OnSelectionChanged) — same _mainViewModel.SelectedEmployee assignment
    /// the old EmployeeTree_OnSelectedItemChanged made from a tree click, which re-triggers the
    /// exact same RequestRefresh() cascade on PayrollSummaryViewModel. No CanExecute — clicking
    /// a row doesn't touch _busy or the database directly, it only changes which employee is
    /// selected (same as the tree click was never disabled while _busy.IsRunning).</summary>
    [RelayCommand]
    private void SelectBatchEmployee(Employee employee) => _mainViewModel.SelectedEmployee = employee;

    /// <summary>Right-click "Remove from group" on a PayrollGroupGrid row. Requires an active
    /// saved run (ActivePayrollRunId non-null) -- the grid only shows employees once a run is
    /// loaded, so this is always true when the context menu is reachable, but guarded
    /// explicitly so a stray invocation can't corrupt BatchScopeEmployees without a run to
    /// write the change back to. Also blocked while _busy.IsRunning for the same shared-
    /// DbContext race reason every other write command follows.
    ///
    /// Writes the removal to the database first (RemoveEmployeeAsync on the active run), then
    /// removes the employee from BatchScopeEmployees in memory and drops their row from
    /// PayrollGroupRows directly -- no RefreshPayrollGroupRowsAsync rebuild, since dropping one
    /// row doesn't change anyone else's NetPay, so there's nothing to recompute for the
    /// employees staying in the group (see _suppressGroupRefreshOnScopeChange's own doc
    /// comment). If the removed employee is currently selected in the right-side panels,
    /// selection is moved to the first remaining member (same fallback
    /// PayrollRunViewModel.LoadPayrollGroupAsync uses when it resolves a run with only one
    /// employee left) and that employee's own detail panels are reloaded.
    ///
    /// All of that -- the DB write, the in-memory removal, and the moved-selection reload --
    /// runs inside this ONE _busy.RunAsync call rather than each kicking off their own separate
    /// call once this one returns. RunAsync is reentrant (see its own doc comment): a nested
    /// call made from inside an outer one's action just rides along on the outer call's
    /// IsRunning instead of toggling it again. Keeping everything nested means one Remove click
    /// flips IsRunning (and re-notifies every gated button's CanExecute) once, not
    /// twice.</summary>
    [RelayCommand(CanExecute = nameof(CanEditGroupMembership))]
    private async Task RemoveEmployeeFromGroupAsync(Employee? employee)
    {
        // Null guard: CommandParameter binding can resolve to null if something goes wrong
        // with the ContextMenu PlacementTarget chain -- bail silently rather than crash.
        if (employee is null) return;
        if (_scope.ActivePayrollRunId is not { } runId) return;
        var pin = employee.Pin;

        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            await _payrollRunRepository.RemoveEmployeeAsync(runId, pin, cancellationToken);

            // Apply in memory -- replace BatchScopeEmployees with a new list that excludes
            // this employee. Suppressed around the assignment: PayrollGroupRows.Remove(...)
            // below already reflects the removal directly, so OnBatchScopeEmployeesChanged's
            // own RequestPayrollGroupRefresh() call would just redo that same work via a full
            // Clear()+recompute-everyone rebuild.
            var remaining = _scope.BatchScopeEmployees.Where(e => e.Id != employee.Id).ToList();
            _suppressGroupRefreshOnScopeChange = true;
            _scope.BatchScopeEmployees = remaining;
            _suppressGroupRefreshOnScopeChange = false;

            var rowToRemove = PayrollGroupRows.FirstOrDefault(r => r.Employee.Id == employee.Id);
            if (rowToRemove is not null)
                PayrollGroupRows.Remove(rowToRemove);

            if (remaining.Count == 0)
            {
                _mainViewModel.SelectedEmployee = null;
                return;
            }

            // Move selection away from the removed employee if they were selected, then reload
            // the detail panels for whoever it lands on. Calls PayrollSummaryViewModel.
            // RefreshAsync() directly (reentrant, nested inside this same _busy.RunAsync
            // window) rather than going through its own RequestRefresh() -- see this class's
            // own doc comment on the "Group <-> Summary" wrinkle, and RefreshAsync's own doc
            // comment on PayrollSummaryViewModel for why this is safe: RefreshAsync is internal
            // specifically for this call, and self-clears Summary's own _refreshPending.
            if (_mainViewModel.SelectedEmployee?.Id == employee.Id)
            {
                _mainViewModel.SelectedEmployee = remaining[0];
                await _summary.RefreshAsync();
            }
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not remove employee"));
    }

    /// <summary>Right-click "Add to group" on an AvailableEmployeeGrid row -- the
    /// single-employee counterpart to AddEmployeesToGroupAsync's own dialog-driven bulk add,
    /// for the "Not in group" grid's own row-level workflow (PayrollPage.xaml's
    /// AvailableEmployeeGrid.RowStyle mirrors PayrollGroupGrid's own "Remove from group"
    /// ContextMenu -- see that RowStyle's doc comment for the Tag-tunnelling it relies on).
    /// Same CanEditGroupMembership guard (an active saved run, nothing else already writing)
    /// and the same "DB write, then patch BatchScopeEmployees/PayrollGroupRows in place under
    /// _suppressGroupRefreshOnScopeChange, all inside one _busy.RunAsync call" shape
    /// RemoveEmployeeFromGroupAsync above follows, just adding instead of removing -- see that
    /// method's own doc comment for why everything happens nested in one call rather than
    /// several. RebuildAvailableEmployeeRows() is NOT called explicitly here -- the
    /// BatchScopeEmployees assignment below still fires OnBatchScopeEmployeesChanged
    /// (suppression only skips its RequestPayrollGroupRefresh()/RequestFullRosterRefresh()
    /// calls, not the method itself), which already calls it unconditionally.
    ///
    /// Computes this one employee's NetPay before appending their PayrollGroupRow, same as
    /// AddEmployeesToGroupAsync's own per-employee loop -- otherwise a blank/pending NetPay
    /// cell would sit there until the next full RefreshPayrollGroupRowsAsync. Left null only
    /// when the period is currently invalid (PeriodEnd < PeriodStart), same guard
    /// RefreshPayrollGroupRowsAsync itself applies.
    ///
    /// added tracks whether the DB write and in-memory patch actually completed, the same
    /// "local flag set only after the risky work succeeds" convention AddEmployeesToGroupAsync's
    /// own addedEmployees follows -- so a failed round trip (caught by onError below) skips the
    /// success toast instead of falsely confirming an add that never happened.</summary>
    [RelayCommand(CanExecute = nameof(CanEditGroupMembership))]
    private async Task AddEmployeeToGroupAsync(Employee? employee)
    {
        // Null guard: CommandParameter binding can resolve to null if something goes wrong
        // with the ContextMenu PlacementTarget chain -- bail silently rather than crash.
        if (employee is null) return;
        if (_scope.ActivePayrollRunId is not { } runId) return;
        var pin = employee.Pin;

        var added = false;

        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            await _payrollRunRepository.AddEmployeeAsync(runId, pin, cancellationToken);
            added = true;

            var row = new PayrollGroupRow { Employee = employee };
            if (_scope.PeriodEnd >= _scope.PeriodStart)
            {
                var start = DateOnly.FromDateTime(_scope.PeriodStart);
                var end = DateOnly.FromDateTime(_scope.PeriodEnd);
                var (result, _) = await _payrollComputationService.ComputeOneAsync(
                    employee, start, end, cancellationToken);
                row.NetPay = result.NetPay;
            }
            PayrollGroupRows.Add(row);

            _suppressGroupRefreshOnScopeChange = true;
            _scope.BatchScopeEmployees = [.. _scope.BatchScopeEmployees, employee];
            _suppressGroupRefreshOnScopeChange = false;
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not add employee"));

        if (added)
            _statusBarService.ShowSuccess($"Added {employee.DisplayName} to the group.");
    }

    /// <summary>Opens a department/employee checkbox tree (same PayrollWizardDialog Step 2
    /// shape) pre-seeded with the current group's members already checked, so the person can
    /// pick additional employees to add. Only the newly-checked employees (those not already in
    /// BatchScopeEmployees) are written to the database, computed, and appended as new
    /// PayrollGroupRows -- existing members' rows are left untouched and NOT recomputed (see
    /// _suppressGroupRefreshOnScopeChange's own doc comment), so their NetPay values stay
    /// on-screen exactly as they were.
    ///
    /// Blocked while _busy.IsRunning and when there is no active saved run (same guards as
    /// RemoveEmployeeFromGroupAsync). Opens the tree dialog outside the busy window, same
    /// "dialog shown before _busy.RunAsync starts" pattern PayrollRunViewModel's own NewPayrollRun
    /// follows for PayrollWizardDialog itself.</summary>
    [RelayCommand(CanExecute = nameof(CanEditGroupMembership))]
    private async Task AddEmployeesToGroupAsync()
    {
        if (_scope.ActivePayrollRunId is not { } runId) return;

        // Load the full department/employee roster to populate the tree.  This is the same
        // two-call shape PayrollRunViewModel's own LoadPayrollGroupAsync and
        // PayrollWizardViewModel both use. Done before opening the dialog (outside any _busy
        // window) so the dialog opens with data already present, same "caller owns the async
        // load" pattern PayrollWizardViewModel follows for its Departments list.
        List<DepartmentGroupViewModel> treeGroups = [];

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var (departments, unassigned) = await _rosterProvider.GetAsync(cancellationToken);

            treeGroups = EmployeeTreeBuilder.Build(departments, unassigned, (_, _) => { });

            // Piggybacks this same fetch onto _fullRoster (see that field's own doc comment)
            // so the "Not in group" grid picks up whatever's genuinely new since the last load
            // too -- BatchScopeEmployees changing below already calls
            // RebuildAvailableEmployeeRows unconditionally (suppressed or not), it would
            // otherwise just be re-filtering a possibly-stale copy instead of this fresh one.
            // Cheaper than also firing RequestFullRosterRefresh() afterward, which would pay
            // for this exact same round trip a second time.
            _fullRoster = [.. departments.SelectMany(d => d.Employees), .. unassigned];
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not load employee list"));

        if (treeGroups.Count == 0) return;

        var currentPins = _scope.BatchScopeEmployees
            .Select(e => e.Pin)
            .ToHashSet();

        var pickerDialog = new AddToPayrollGroupDialog(treeGroups, _scope.BatchScopeEmployees)
        {
            Owner = Application.Current.MainWindow,
        };

        if (pickerDialog.ShowDialog() != true || pickerDialog.SelectedEmployees is not { } picked) return;

        // Filter to employees not already in the group -- the dialog pre-checks existing
        // members for display, but we only persist and append the new ones.
        var toAdd = picked
            .Where(e => !currentPins.Contains(e.Pin))
            .ToList();

        if (toAdd.Count == 0)
        {
            _statusBarService.ShowSuccess("No new employees selected.");
            return;
        }

        List<Employee>? addedEmployees = null;
        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);
        var periodIsValid = _scope.PeriodEnd >= _scope.PeriodStart;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            foreach (var emp in toAdd)
                await _payrollRunRepository.AddEmployeeAsync(runId, emp.Pin, cancellationToken);

            addedEmployees = toAdd;

            // Compute a row for just the newly added employees instead of paying for
            // RefreshPayrollGroupRowsAsync's Clear()+recompute-everyone rebuild -- existing
            // rows' NetPay hasn't changed, so there's nothing to gain from recomputing them
            // again. Same "build off-collection, then append in one go, with
            // IsPayrollGroupLoading/PayrollGroupLoadPercent tracking progress via the status
            // bar in the meantime" shape RefreshPayrollGroupRowsAsync itself uses (see that
            // method's own doc comment), just scoped to toAdd: each new row is computed into a
            // local list first, and only once every one of them is done are they appended to
            // PayrollGroupRows together, so the grid gains the whole new batch in a single
            // update rather than the added rows trickling in with NetPay flashing
            // blank-then-value one at a time. Skipped when the period itself is invalid
            // (PeriodEnd < PeriodStart) -- same guard RequestPayrollGroupRefresh applies for
            // every other trigger; BatchScopeEmployees changing below still notifies normally
            // in that case, so PayrollGroupRows stays whatever it already was (empty, per that
            // same guard) rather than showing rows for an unrecomputable period.
            if (!periodIsValid) return;

            IsPayrollGroupLoading = true;
            PayrollGroupLoadPercent = 0;

            try
            {
                // Same PrepareBatchAsync-once-then-ComputeOneFromBatchAsync-per-employee shape
                // RefreshPayrollGroupRowsAsync uses (see that method's own doc comment) --
                // scoped to just toAdd's pins here rather than the whole group, since only
                // these rows are being computed.
                var pins = toAdd.Select(e => e.Pin).ToHashSet();
                var batch = await _payrollComputationService.PrepareBatchAsync(pins, start, end, cancellationToken);

                // Same batch-wide seed-before-compute shape RefreshPayrollGroupRowsAsync uses
                // (see that method's own comment) -- scoped to just toAdd here, same as the
                // PrepareBatchAsync call right above it.
                batch = await _payrollComputationService.SeedContributionsForBatchAsync(
                    toAdd, start, end, batch, cancellationToken);

                var newRows = new List<PayrollGroupRow>(toAdd.Count);
                for (var i = 0; i < toAdd.Count; i++)
                {
                    var emp = toAdd[i];
                    var row = new PayrollGroupRow { Employee = emp };
                    newRows.Add(row);

                    var (result, _) = await _payrollComputationService.ComputeOneFromBatchAsync(
                        emp, start, end, batch, cancellationToken);
                    row.NetPay = result.NetPay;

                    PayrollGroupLoadPercent = (i + 1) * 100 / toAdd.Count;

                    // See RefreshPayrollGroupRowsAsync's own doc comment on the matching yield
                    // there for why this is needed, not optional -- same
                    // Normal-priority-continuations-starve-Render story applies here too.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                foreach (var row in newRows)
                    PayrollGroupRows.Add(row);
            }
            finally
            {
                IsPayrollGroupLoading = false;
            }
        },
        onError: ex => _statusBarService.ShowError(ex.Message, "Could not add employees"));

        if (addedEmployees is null || addedEmployees.Count == 0) return;

        // Append newly added employees to the in-memory scope. Suppressed around the
        // assignment when the period was valid, since PayrollGroupRows was already updated
        // above and OnBatchScopeEmployeesChanged's own RequestPayrollGroupRefresh() call would
        // just redo that same work via a full rebuild. Left un-suppressed for the
        // invalid-period case above, so that guard still runs its usual PayrollGroupRows.Clear().
        _suppressGroupRefreshOnScopeChange = periodIsValid;
        _scope.BatchScopeEmployees = [.. _scope.BatchScopeEmployees, .. addedEmployees];
        _suppressGroupRefreshOnScopeChange = false;

        _statusBarService.ShowSuccess(
            addedEmployees.Count == 1
                ? $"Added {addedEmployees[0].DisplayName} to the group."
                : $"Added {addedEmployees.Count} employees to the group.");
    }

    /// <summary>Shared CanExecute for RemoveEmployeeFromGroupAsync/AddEmployeesToGroupAsync/
    /// AddEmployeeToGroupAsync -- all three require an active saved run to write membership
    /// changes back to, and none should run while another database operation is already in
    /// progress (same _busy.IsRunning guard every write command here uses).</summary>
    private bool CanEditGroupMembership() => _scope.ActivePayrollRunId.HasValue && !_busy.IsRunning;
}
