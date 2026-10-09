using System.Collections.Specialized;
using System.Reactive.Linq;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

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
/// PayrollViewModel.Group. Nothing in this class itself needs to know that.
///
/// Takes PayrollScopeState and AttendanceBusyState the same shared-not-owned way
/// PayrollSummaryViewModel does -- both constructed on PayrollViewModel and passed in here.
/// Also takes PayrollSummaryViewModel itself, one direction only: under the old single-class
/// shape these two concerns depended on each other in *both* directions (LoadCoreAsync patching
/// PayrollGroupRows directly; Remove calling Summary's own reload), which isn't constructible
/// once they're separate classes -- broken here by Group taking Summary as a constructor
/// dependency and subscribing to its <see cref="PayrollSummaryViewModel.EmployeeNetPayComputed"/>
/// event for the first case, and calling <see cref="PayrollSummaryViewModel.RefreshAsync"/>
/// directly for the second (see RemoveEmployeeFromGroupAsync).</summary>
public partial class PayrollGroupViewModel : ViewModelBase
{
    /// <summary>The employee selected app-wide -- moved by a row click, and off a removed
    /// employee.</summary>
    private readonly IEmployeeSelection _selection;

    /// <summary>The shared, RosterVersion-gated roster cache -- see ActiveRosterProvider's own
    /// doc comment.</summary>
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IPayrollRunRepository _payrollRunRepository;

    /// <summary>Shared with PayrollViewModel and its other children -- see
    /// PayrollViewModel's own _busy doc comment for why this is one instance, not one per
    /// child.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Shared with PayrollViewModel and its other children -- see PayrollViewModel's
    /// own _scope doc comment. This class reads/writes PeriodStart/PeriodEnd/
    /// BatchScopeEmployees/ActivePayrollRunId straight off this instance.</summary>
    private readonly PayrollScopeState _scope;

    /// <summary>The single-employee-breakdown concern this class depends on -- see this
    /// class's own doc comment for why the dependency runs this direction only.</summary>
    private readonly PayrollSummaryViewModel _summary;

    /// <summary>Shared with AttendanceViewModel/MainViewModel/ScheduleAssignmentViewModel --
    /// see App.xaml.cs's own registration of this class. Read by RecheckOnPageRevisitAsync
    /// to notice a schedule edit made on the Schedule page while this group was already
    /// loaded; never bumped here (that's the write side's job).</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>Snapshot of _dataVersion.ScheduleVersion taken the moment
    /// RefreshPayrollGroupRowsAsync last successfully computed the currently-loaded group --
    /// what RecheckOnPageRevisitAsync compares the live value against (via
    /// _dataVersion.AnyScheduleChangeSince) to decide whether a schedule edit made elsewhere
    /// touches anyone in this group. Taken at the START of the load, not the end, so a bump that
    /// races in mid-load is still noticed on the very next revisit rather than being silently
    /// absorbed as "already accounted for."</summary>
    private int _loadedScheduleVersion = -1;

    /// <summary>Sibling of _loadedScheduleVersion for _dataVersion.HolidayVersion, compared
    /// with a raw `!=`: holidays are company-wide (see AttendanceDataVersion.HolidayVersion's
    /// own doc comment), so there's no per-employee holiday tracking to filter against.</summary>
    private int _loadedHolidayVersion = -1;

    /// <summary>Third sibling, for _dataVersion.AttendanceInputs -- the device-punch/
    /// manual-punch/pairing counters as one comparable value. Compared with a raw `!=` like
    /// _loadedHolidayVersion: none of the three counters behind it records which employees it
    /// touched, so a punch edit for someone outside the group still recomputes it -- one batch
    /// run landing on the same numbers, the same "over-report rather than under-report" trade
    /// the rest of this mechanism makes. Group-side counterpart of
    /// PayrollSummaryViewModel._loadedAttendanceInputs. Null until the first successful
    /// load.</summary>
    private AttendanceInputsVersion? _loadedAttendanceInputs;

    /// <summary>A RequestPayrollGroupRefresh() that arrived while _busy.IsRunning, so it was
    /// deferred rather than racing the shared ScheduleDbContext -- re-run once IsRunning drops
    /// (see the constructor). Separate from PayrollSummaryViewModel's own _refreshPending: both
    /// can legitimately be pending at once for different reasons.</summary>
    private bool _payrollGroupRefreshPending;

    /// <summary>Cached flat roster (every department's employees, plus the unassigned ones)
    /// backing AvailableEmployeeRows -- loaded by RefreshFullRosterAsync whenever
    /// BatchScopeEmployees is wholesale-replaced and then just re-filtered in memory by
    /// RebuildAvailableEmployeeRows on every later BatchScopeEmployees change, so moving one
    /// row between the two grids never re-queries the whole roster.</summary>
    private List<Employee> _fullRoster = [];

    /// <summary>Same defer-until-idle role as _payrollGroupRefreshPending, for
    /// RequestFullRosterRefresh() -- separate since a suppressed change (see
    /// _suppressGroupRefreshOnScopeChange) skips the group refresh but still re-filters the
    /// roster, with no roster reload needed at all.</summary>
    private bool _fullRosterRefreshPending;

    /// <summary>Set by RemoveEmployeeFromGroupAsync/AddEmployeesToGroupAsync/
    /// AddEmployeeToGroupAsync around their own BatchScopeEmployees assignment, so
    /// OnBatchScopeEmployeesChanged skips its full Clear()+recompute-everyone rebuild for just
    /// that assignment -- every caller already patches PayrollGroupRows itself, and adding or
    /// removing a row doesn't touch anyone else's NetPay. Every other BatchScopeEmployees
    /// assignment (a new or loaded run) is a genuinely new roster and gets the full
    /// refresh.</summary>
    private bool _suppressGroupRefreshOnScopeChange;

    /// <summary>Shared CanExecute for the three membership commands: all need an active saved
    /// run to write changes back to, and none should run while another database operation is
    /// already in progress.</summary>
    private readonly IObservable<bool> _canEditGroupMembership;

    public PayrollGroupViewModel(
        IEmployeeSelection selection,
        ActiveRosterProvider rosterProvider,
        IPayrollComputationService payrollComputationService,
        IPayrollRunRepository payrollRunRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope,
        PayrollSummaryViewModel summary,
        AttendanceDataVersion dataVersion)
        : base(statusBarService)
    {
        _selection = selection;
        _rosterProvider = rosterProvider;
        _payrollComputationService = payrollComputationService;
        _payrollRunRepository = payrollRunRepository;
        _busy = busy;
        _scope = scope;
        _summary = summary;
        _dataVersion = dataVersion;

        // Patches just the one row whose NetPay Summary just recomputed -- an opportunistic
        // sync, not a full rebuild (see this class's own doc comment on why it's an event).
        _summary.EmployeeNetPayComputed += (employeeId, netPay) =>
        {
            var row = PayrollGroupRows.FirstOrDefault(r => r.Employee.Id == employeeId);
            row?.NetPay = netPay;
        };

        // PayrollGroupGrid doesn't let the user sort, so the comparer is what keeps both grids
        // alphabetical by employee name rather than BatchScopeEmployees' own tree order. The
        // search box filters both views together, so typing narrows the whole panel.
        PayrollGroupRowsView = new FilteredCollection<PayrollGroupRow>(
            PayrollGroupRows,
            Comparer<PayrollGroupRow>.Create((a, b) => StringComparer.CurrentCulture.Compare(a.EmployeeName, b.EmployeeName)))
        {
            Filter = item => item is PayrollGroupRow row && MatchesSearch(row.Employee, row.DepartmentName),
        };
        AvailableEmployeeRowsView = new FilteredCollection<AvailableEmployeeRow>(
            AvailableEmployeeRows,
            Comparer<AvailableEmployeeRow>.Create((a, b) => StringComparer.CurrentCulture.Compare(a.EmployeeName, b.EmployeeName)))
        {
            Filter = item => item is AvailableEmployeeRow row && MatchesSearch(row.Employee, row.DepartmentName),
        };
        this.WhenAnyValue(x => x.PayrollGroupSearchText)
            .Skip(1)
            .Subscribe(_ =>
            {
                PayrollGroupRowsView.Refresh();
                AvailableEmployeeRowsView.Refresh();
            });

        // The whole group's total, re-summed whenever the set of rows changes or any row's own
        // NetPay is patched -- subscribing afresh to the current rows on each change, so a
        // discarded row's subscription goes with it.
        var rowsChanged = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => PayrollGroupRows.CollectionChanged += handler,
                handler => PayrollGroupRows.CollectionChanged -= handler)
            .Select(_ => RxVoid.Default)
            .StartWith(RxVoid.Default);
        _totalNetPayHelper = Observable.Switch(rowsChanged.Select(_ => PayrollGroupRows
                .Select(row => row.WhenAnyValue(r => r.NetPay).Skip(1))
                .Merge()
                .Select(_ => RxVoid.Default)
                .StartWith(RxVoid.Default)))
            .Select(_ => PayrollGroupRows.Sum(r => r.NetPay ?? 0m))
            .ToProperty(this, x => x.TotalNetPay);

        // The status bar's left-aligned progress indicator follows the group's compute loop.
        this.WhenAnyValue(x => x.IsPayrollGroupLoading, x => x.PayrollGroupLoadPercent,
                (loading, percent) => loading ? percent : (int?)null)
            .DistinctUntilChanged()
            .Skip(1)
            .Subscribe(percent =>
            {
                if (percent is { } value)
                    StatusBar.ShowProgress("Computing payroll group…", value);
                else
                    StatusBar.ClearProgress();
            });

        _hasBatchScopeHelper = _scope.WhenAnyValue(s => s.BatchScopeEmployees)
            .Select(employees => employees.Count > 0)
            .ToProperty(this, x => x.HasBatchScope);
        _scope.WhenAnyValue(s => s.PeriodStart, s => s.PeriodEnd)
            .Skip(1)
            .Subscribe(_ => RequestPayrollGroupRefresh());
        _scope.WhenAnyValue(s => s.BatchScopeEmployees)
            .Skip(1)
            .Subscribe(_ => OnBatchScopeEmployeesChanged());

        _canEditGroupMembership = Observable.CombineLatest(
            _scope.WhenAnyValue(s => s.ActivePayrollRunId),
            _busy.WhenAnyValue(b => b.IsRunning),
            (runId, running) => runId.HasValue && !running);

        // Built before the replay below subscribes, so the commands' CanExecute has already
        // caught up with IsRunning dropping by the time a deferred refresh raises it again.
        ReportFailuresOf(RemoveEmployeeFromGroupCommand, AddEmployeeToGroupCommand, AddEmployeesToGroupCommand);

        // Picks up a group/roster refresh that arrived while something else was running --
        // RequestPayrollGroupRefresh/RequestFullRosterRefresh deliberately don't start a second,
        // concurrent query against the shared, app-lifetime-scoped ScheduleDbContext; this is
        // what stops that deferred change from just going stale instead.
        _busy.WhenAnyValue(b => b.IsRunning)
            .Skip(1)
            .Where(running => !running)
            .Subscribe(_ =>
            {
                if (_payrollGroupRefreshPending)
                {
                    _payrollGroupRefreshPending = false;
                    RequestPayrollGroupRefresh();
                }

                if (_fullRosterRefreshPending)
                {
                    _fullRosterRefreshPending = false;
                    RequestFullRosterRefresh();
                }
            });

        // A freshly-built Payroll tab genuinely starts with no batch scope (EmptyStateOverlay's
        // HasBatchScope binding depends on it). Last, since OnBatchScopeEmployeesChanged reaches
        // into everything wired above.
        if (_scope.BatchScopeEmployees.Count > 0)
            _scope.BatchScopeEmployees = [];
    }

    /// <summary>One PayrollGroupRow per BatchScopeEmployees entry -- what PayrollPage's
    /// left-column grid (Employee Name / Department / Net Pay) shows, through
    /// PayrollGroupRowsView. Rebuilt by RefreshPayrollGroupRowsAsync on every period or scope
    /// change, all at once: every row is computed off-collection first and only then swapped in
    /// with one Reset, so the grid replaces its contents in a single update instead of
    /// clearing and refilling row by row with NetPay flashing blank-then-value. Individual
    /// rows' NetPay is also patched in place as PayrollSummaryViewModel recomputes an
    /// employee's own breakdown (EmployeeNetPayComputed). Empty until a run is started or
    /// loaded this session.</summary>
    public RangeObservableCollection<PayrollGroupRow> PayrollGroupRows { get; } = [];

    /// <summary>What PayrollGroupGrid binds to: PayrollGroupRows filtered on
    /// PayrollGroupSearchText and kept in name order -- same "grid binds to a live-filtered
    /// view over the real collection" shape as ReportViewModel.SummaryRowsView.</summary>
    public FilteredCollection<PayrollGroupRow> PayrollGroupRowsView { get; }

    /// <summary>One AvailableEmployeeRow per roster employee NOT in BatchScopeEmployees -- the
    /// "Not in group" grid, through AvailableEmployeeRowsView. Rebuilt wholesale by
    /// RebuildAvailableEmployeeRows; there's no per-row NetPay to patch, so that's always
    /// cheap.</summary>
    public RangeObservableCollection<AvailableEmployeeRow> AvailableEmployeeRows { get; } = [];

    /// <summary>Same role as PayrollGroupRowsView, for AvailableEmployeeRows.</summary>
    public FilteredCollection<AvailableEmployeeRow> AvailableEmployeeRowsView { get; }

    /// <summary>Sum of every row's NetPay across the whole payroll group -- the totals strip
    /// under PayrollGroupGrid. Deliberately sums PayrollGroupRows, not the search-filtered view:
    /// it reads as "the total that'll be disbursed for this run", which shouldn't silently
    /// shrink because someone typed a name to find one row. A row with no NetPay yet (no Pin)
    /// counts as 0.</summary>
    [ObservableAsProperty]
    public partial decimal TotalNetPay { get; }

    /// <summary>True while RefreshPayrollGroupRowsAsync or AddEmployeesToGroupAsync is
    /// computing rows. Since both build their rows off-collection, the grid sits unchanged for
    /// the whole several-second run; this and PayrollGroupLoadPercent drive the status bar's
    /// own progress indicator so the person still sees something happening. Separate from
    /// _busy.IsVisiblyRunning -- both group loads run with visibly: false, so the Summary
    /// panel's own progress bar deliberately doesn't appear for this background requery.
    /// Always reset in a finally, so a cancelled or failed run doesn't leave the status bar
    /// stuck reading "Computing…".</summary>
    [Reactive]
    public partial bool IsPayrollGroupLoading { get; private set; }

    /// <summary>0-100 -- employees computed so far out of the run's, while
    /// IsPayrollGroupLoading.</summary>
    [Reactive]
    public partial int PayrollGroupLoadPercent { get; private set; }

    /// <summary>Free-text filter for both grids -- comma-separated terms, each matched against
    /// the employee's Employee ID (exact, numeric terms only), first name, last name, or
    /// department name, the same rule EmployeeTreeSearchFilter applies to the trees. Purely a
    /// display filter: never touches the rows themselves or who's selected. Not persisted
    /// across sessions.</summary>
    [Reactive]
    public partial string PayrollGroupSearchText { get; set; } = string.Empty;

    /// <summary>Drives EmptyStateOverlay's Visibility in PayrollPage.xaml -- "is there an
    /// active batch". False (overlay shown, over the whole page) until "New Payroll Run…" or
    /// "Load Payroll Group…" is completed once this session.</summary>
    [ObservableAsProperty]
    public partial bool HasBatchScope { get; }

    /// <summary>See PayrollGroupSearchText for the rule. Reads the Employee itself rather than
    /// its "LastName, FirstName" display string, so a Pin match needs no formatting.</summary>
    private bool MatchesSearch(Employee employee, string departmentName)
    {
        var terms = PayrollGroupSearchText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 || terms.Any(t =>
            (int.TryParse(t, out var id) && employee.Pin == id) ||
            employee.FirstName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            employee.LastName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            departmentName.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A wholesale BatchScopeEmployees replace (a new or loaded run) refreshes the
    /// group and the roster; a suppressed single- or few-row patch (see
    /// _suppressGroupRefreshOnScopeChange) only re-filters the roster in memory, which is right
    /// either way -- a wholesale replace still wants the "Not in group" grid to reflect the new
    /// scope before its roster reload returns.</summary>
    private void OnBatchScopeEmployeesChanged()
    {
        RebuildAvailableEmployeeRows();

        if (_suppressGroupRefreshOnScopeChange) return;
        RequestPayrollGroupRefresh();
        RequestFullRosterRefresh();
    }

    /// <summary>Gate and defer counterpart to PayrollSummaryViewModel's own RequestRefresh(),
    /// for the group table: clears it when there's no batch or the period is backwards, defers
    /// while _busy.IsRunning, otherwise refreshes.</summary>
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

    /// <summary>
    /// One PrepareBatchAsync for every BatchScopeEmployees pin at once, a batch-wide
    /// contribution seed, then ComputeOneFromBatchAsync per employee against that shared result
    /// (looping ComputeOneAsync instead re-ran a company-wide attendance fetch per employee --
    /// see PayrollBatchContext's own doc comment), building every row off-collection and
    /// swapping them in together once all are computed (see PayrollGroupRows).
    ///
    /// visibly: false -- a background requery the person didn't explicitly trigger, so it
    /// shouldn't make the Summary panel's own progress bar appear.
    /// </summary>
    private async Task RefreshPayrollGroupRowsAsync()
    {
        var employees = _scope.BatchScopeEmployees;
        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);
        var succeeded = true;

        // Read at the START of the load -- see _loadedScheduleVersion's own doc comment.
        // Only committed once this whole run has actually succeeded.
        var scheduleVersionAtLoadStart = _dataVersion.ScheduleVersion;
        var holidayVersionAtLoadStart = _dataVersion.HolidayVersion;
        var attendanceInputsAtLoadStart = _dataVersion.AttendanceInputs;

        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            var rows = await ComputeRowsAsync(employees, start, end, cancellationToken);
            PayrollGroupRows.ReplaceAll(rows);
        },
        onError: ex =>
        {
            succeeded = false;
            ShowFailure(ex, "Could not refresh payroll group");
        });

        if (succeeded)
        {
            _loadedScheduleVersion = scheduleVersionAtLoadStart;
            _loadedHolidayVersion = holidayVersionAtLoadStart;
            _loadedAttendanceInputs = attendanceInputsAtLoadStart;
        }
    }

    /// <summary>A computed row for each of <paramref name="employees"/>, reporting progress
    /// through IsPayrollGroupLoading/PayrollGroupLoadPercent. Seeds every employee's
    /// SSS/PhilHealth/Pag-IBIG/Premium Pay/Allowance/Cash Advance rows in at most two round trips
    /// first (SeedContributionsForBatchAsync), so the per-employee loop is pure in-memory
    /// calculation -- which is why it yields for a repaint after each employee (see
    /// RenderYield).</summary>
    private async Task<List<PayrollGroupRow>> ComputeRowsAsync(
        IReadOnlyList<Employee> employees, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        IsPayrollGroupLoading = true;
        PayrollGroupLoadPercent = 0;
        try
        {
            var pins = employees.Select(e => e.Pin).ToHashSet();
            var batch = await _payrollComputationService.PrepareBatchAsync(pins, start, end, cancellationToken);
            batch = await _payrollComputationService.SeedContributionsForBatchAsync(
                employees, start, end, batch, cancellationToken);

            var rows = new List<PayrollGroupRow>(employees.Count);
            for (var i = 0; i < employees.Count; i++)
            {
                var (result, _) = await _payrollComputationService.ComputeOneFromBatchAsync(
                    employees[i], start, end, batch, cancellationToken);
                rows.Add(new PayrollGroupRow { Employee = employees[i], NetPay = result.NetPay });

                PayrollGroupLoadPercent = (i + 1) * 100 / employees.Count;
                await RenderYield.ForRenderAsync();
            }

            return rows;
        }
        finally
        {
            IsPayrollGroupLoading = false;
        }
    }

    /// <summary>
    /// Closes the one gap a page-level "was I on a different tab" check can't: the ONLY way to
    /// change a schedule while a payroll group is loaded is to go to the Schedule page, edit
    /// there, and come back. Called from PayrollPage.OnNavigatedToAsync every visit.
    ///
    /// Genuinely awaited all the way up to OnNavigatedToAsync, not fire-and-forget:
    /// PayrollPage re-dispatches RestorePayrollGroupSelection on every PayrollGroupRows change,
    /// which cascades into PayrollSummaryViewModel.RequestRefresh -- a second user of the same
    /// shared ScheduleDbContext. That dispatch only runs after OnNavigatedToAsync returns, so
    /// awaiting here guarantees this method's own database work has finished by then. Calls
    /// RefreshPayrollGroupRowsAsync directly rather than through the defer-and-replay dance,
    /// which exists for handlers that can't be async; this one just waits its turn on _busy.
    ///
    /// Recomputes the whole group when a holiday changed or attendance inputs moved (both
    /// company-wide, nothing per-employee to narrow by), or when AnyScheduleChangeSince says at
    /// least one loaded employee's schedule moved -- which covers every schedule write in the
    /// app. A no-op, database-wise, whenever nothing relevant changed, since all three checks
    /// are in-memory comparisons. Doesn't check the edited dates against the group's period:
    /// an edit outside it can't change what gets recomputed, so the worst case is one harmless
    /// recompute landing on the same numbers.
    /// </summary>
    internal async Task RecheckOnPageRevisitAsync()
    {
        var pins = _scope.BatchScopeEmployees.Select(e => e.Pin).ToList();
        if (pins.Count == 0) return;

        if (_dataVersion.HolidayVersion != _loadedHolidayVersion
            || _dataVersion.AttendanceInputs != _loadedAttendanceInputs
            || _dataVersion.AnyScheduleChangeSince(pins, _loadedScheduleVersion))
        {
            await RefreshPayrollGroupRowsAsync();
        }
    }

    /// <summary>Gate and defer counterpart to RequestPayrollGroupRefresh(), for the roster
    /// behind AvailableEmployeeRows. Deliberately NOT fired on a period change -- the roster
    /// has nothing to do with the period.</summary>
    private void RequestFullRosterRefresh()
    {
        if (_busy.IsRunning)
        {
            _fullRosterRefreshPending = true;
            return;
        }

        _ = RefreshFullRosterAsync();
    }

    /// <summary>The roster load behind RequestFullRosterRefresh(), inside _busy like every other
    /// read on this tab; visibly: false, since the person didn't click anything for it.</summary>
    private async Task RefreshFullRosterAsync()
    {
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            var (departments, unassigned) = await _rosterProvider.GetAsync(cancellationToken);

            _fullRoster = [.. departments.SelectMany(d => d.Employees), .. unassigned];
            RebuildAvailableEmployeeRows();
        },
        onError: ex => ShowFailure(ex, "Could not load employee roster"));
    }

    /// <summary>_fullRoster minus whoever's currently in BatchScopeEmployees, matched by
    /// Employee.Id -- purely in-memory, so cheap enough for every BatchScopeEmployees
    /// change.</summary>
    private void RebuildAvailableEmployeeRows()
    {
        var currentIds = _scope.BatchScopeEmployees.Select(e => e.Id).ToHashSet();
        AvailableEmployeeRows.ReplaceAll(_fullRoster
            .Where(e => !currentIds.Contains(e.Id))
            .Select(e => new AvailableEmployeeRow { Employee = e }));
    }

    /// <summary>A PayrollGroup table row's click (see PayrollPage.xaml.cs's
    /// PayrollGroupGrid_OnSelectionChanged) -- selects that employee app-wide, which
    /// re-triggers PayrollSummaryViewModel's own refresh. No CanExecute: it only changes who's
    /// selected, never touches the database directly.</summary>
    [ReactiveCommand]
    private void SelectBatchEmployee(Employee employee) => _selection.SelectedEmployee = employee;

    /// <summary>
    /// Right-click "Remove from group" on a PayrollGroupGrid row. Writes the removal to the
    /// database first, then drops the employee from BatchScopeEmployees and their row from
    /// PayrollGroupRows directly -- no full rebuild, since dropping one row doesn't change
    /// anyone else's NetPay. If the removed employee was selected, selection moves to the first
    /// remaining member and their detail panels reload.
    ///
    /// All of that runs inside ONE _busy.RunAsync call: RunAsync is reentrant (a nested call
    /// rides along on the outer one's IsRunning), so one Remove click flips IsRunning -- and
    /// re-notifies every gated button -- once, not twice.
    /// </summary>
    [ReactiveCommand(CanExecute = nameof(_canEditGroupMembership))]
    private async Task RemoveEmployeeFromGroupAsync(Employee? employee)
    {
        // A ContextMenu's CommandParameter can resolve to null if its PlacementTarget chain
        // breaks -- bail rather than crash.
        if (employee is null) return;
        if (_scope.ActivePayrollRunId is not { } runId) return;

        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            await _payrollRunRepository.RemoveEmployeeAsync(runId, employee.Pin, cancellationToken);

            var remaining = _scope.BatchScopeEmployees.Where(e => e.Id != employee.Id).ToList();
            _suppressGroupRefreshOnScopeChange = true;
            _scope.BatchScopeEmployees = remaining;
            _suppressGroupRefreshOnScopeChange = false;

            if (PayrollGroupRows.FirstOrDefault(r => r.Employee.Id == employee.Id) is { } row)
                PayrollGroupRows.Remove(row);

            if (remaining.Count == 0)
            {
                _selection.SelectedEmployee = null;
                return;
            }

            // Calls Summary's RefreshAsync directly, nested in this same _busy window, rather
            // than its RequestRefresh() -- RefreshAsync is internal for exactly this call, and
            // clears Summary's own pending flag itself.
            if (_selection.SelectedEmployee?.Id == employee.Id)
            {
                _selection.SelectedEmployee = remaining[0];
                await _summary.RefreshAsync();
            }
        },
        onError: ex => ShowFailure(ex, "Could not remove employee"));
    }

    /// <summary>Right-click "Add to group" on an AvailableEmployeeGrid row -- the
    /// single-employee counterpart to AddEmployeesToGroupAsync, with the same "DB write, then
    /// patch BatchScopeEmployees/PayrollGroupRows in place, all inside one _busy.RunAsync" shape
    /// as RemoveEmployeeFromGroupAsync. Computes the employee's NetPay before appending their
    /// row, so no blank cell waits for the next full refresh (left null only while the period
    /// is backwards). The success message only shows once the write actually landed.</summary>
    [ReactiveCommand(CanExecute = nameof(_canEditGroupMembership))]
    private async Task AddEmployeeToGroupAsync(Employee? employee)
    {
        if (employee is null) return;
        if (_scope.ActivePayrollRunId is not { } runId) return;

        var added = false;

        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            await _payrollRunRepository.AddEmployeeAsync(runId, employee.Pin, cancellationToken);
            added = true;

            var row = new PayrollGroupRow { Employee = employee };
            if (_scope.PeriodEnd >= _scope.PeriodStart)
            {
                var (result, _) = await _payrollComputationService.ComputeOneAsync(
                    employee, DateOnly.FromDateTime(_scope.PeriodStart), DateOnly.FromDateTime(_scope.PeriodEnd),
                    cancellationToken);
                row.NetPay = result.NetPay;
            }

            PayrollGroupRows.Add(row);

            _suppressGroupRefreshOnScopeChange = true;
            _scope.BatchScopeEmployees = [.. _scope.BatchScopeEmployees, employee];
            _suppressGroupRefreshOnScopeChange = false;
        },
        onError: ex => ShowFailure(ex, "Could not add employee"));

        if (added)
            StatusBar.ShowSuccess($"Added {employee.DisplayName} to the group.");
    }

    /// <summary>
    /// Opens the roster tree (see AddToPayrollGroupViewModel) with the current members already
    /// checked, then writes only the newly-checked employees -- as one batch, so either the
    /// whole set lands or none of it does -- computes them, and appends their rows. Existing
    /// members' rows are left untouched and NOT recomputed.
    ///
    /// The roster is loaded before the picker opens (so it opens with data present), and the
    /// picker is shown outside any _busy window.
    /// </summary>
    [ReactiveCommand(CanExecute = nameof(_canEditGroupMembership))]
    private async Task AddEmployeesToGroupAsync()
    {
        if (_scope.ActivePayrollRunId is not { } runId) return;

        List<DepartmentGroupViewModel> tree = [];

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var (departments, unassigned) = await _rosterProvider.GetAsync(cancellationToken);
            tree = EmployeeTreeBuilder.Build(departments, unassigned);

            // Piggybacks this fetch onto _fullRoster, so the "Not in group" grid picks up
            // whatever's new since the last load without paying for the round trip twice.
            _fullRoster = [.. departments.SelectMany(d => d.Employees), .. unassigned];
        },
        onError: ex => ShowFailure(ex, "Could not load employee list"));

        if (tree.Count == 0) return;

        var picker = new AddToPayrollGroupViewModel(tree, _scope.BatchScopeEmployees);
        if (!await ShowDialogAsync(picker) || picker.CheckedEmployees is not { } picked) return;

        var currentPins = _scope.BatchScopeEmployees.Select(e => e.Pin).ToHashSet();
        var toAdd = picked.Where(e => !currentPins.Contains(e.Pin)).ToList();
        if (toAdd.Count == 0)
        {
            StatusBar.ShowSuccess("No new employees selected.");
            return;
        }

        List<Employee>? addedEmployees = null;
        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);
        var periodIsValid = _scope.PeriodEnd >= _scope.PeriodStart;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // One call, not one per employee: a loop could commit some rows and then be
            // cancelled partway -- and RunAsync swallows cancellation without onError -- leaving
            // the run with members nothing here knows about. Either the whole set lands and
            // addedEmployees is set on the very next line, or nothing lands and it stays null.
            await _payrollRunRepository.AddEmployeesAsync(runId, [.. toAdd.Select(e => e.Pin)], cancellationToken);
            addedEmployees = toAdd;

            // Rows for just the new employees, appended together. Skipped for a backwards
            // period, where the BatchScopeEmployees change below runs its usual Clear().
            if (periodIsValid)
                PayrollGroupRows.AddRange(await ComputeRowsAsync(toAdd, start, end, cancellationToken));
        },
        onError: ex => ShowFailure(ex, "Could not add employees"));

        if (addedEmployees is not { Count: > 0 }) return;

        _suppressGroupRefreshOnScopeChange = periodIsValid;
        _scope.BatchScopeEmployees = [.. _scope.BatchScopeEmployees, .. addedEmployees];
        _suppressGroupRefreshOnScopeChange = false;

        StatusBar.ShowSuccess(
            addedEmployees.Count == 1
                ? $"Added {addedEmployees[0].DisplayName} to the group."
                : $"Added {addedEmployees.Count} employees to the group.");
    }
}
