using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Payroll;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 1 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel
/// as build-order step 2. Owns the single-employee breakdown for whichever employee is
/// selected app-wide: the itemized Gross Pay/Deductions/Net Pay figures (<see cref="Result"/>),
/// every adjustment CRUD method, the read-only attendance grid underneath it (<see
/// cref="AttendanceRows"/>), and Print Current Payslip -- recomputed live every time the
/// selected employee or period changes, or an adjustment is added/edited/deleted.
///
/// PayrollSummaryView/EmployeeAttendancePanel bind to PayrollViewModel (the facade), never to
/// this class -- every member they touch is forwarded back out under the same name via
/// PayrollViewModel.Summary.
///
/// Takes PayrollScopeState and AttendanceBusyState shared-not-owned, the same way every other
/// Payroll child does.</summary>
public partial class PayrollSummaryViewModel : ViewModelBase
{
    /// <summary>The employee selected app-wide -- whose payroll this shows.</summary>
    private readonly IEmployeeSelection _selection;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IPayrollAdjustmentRepository _adjustmentRepository;
    private readonly IPayrollUndertimeWaiverRepository _undertimeWaiverRepository;
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope;

    /// <summary>Already resolved to whatever's effective (PayrollSettings.CompanyName, or
    /// PayslipLineBuilder.DefaultCompanyName if that's blank) by PayrollViewModel, once, up
    /// front. Passed straight through to the payslip preview.</summary>
    private readonly string _companyName;

    /// <summary>Shared with PayrollGroupViewModel and the rest of the app -- read by
    /// RecheckOnPageRevisitAsync, the same role it plays for the group's own check.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>Snapshot of _dataVersion.ScheduleVersion taken the moment RefreshAsync last
    /// successfully ran the real attendance recompute for SelectedEmployee -- read at the
    /// start, committed only on success, like PayrollGroupViewModel's own. Deliberately NOT
    /// touched by an adjustments-only reload (the Add/Edit/Delete/waiver writes), which reuses
    /// the attendance already computed and so never picks up a schedule change itself.</summary>
    private int _loadedScheduleVersion = -1;

    /// <summary>Sibling of _loadedScheduleVersion for _dataVersion.HolidayVersion, compared
    /// with a raw `!=` (holidays are company-wide). An adjustments-only reload already falls
    /// back to a full recompute on a holiday change, since PayrollComputationService's own
    /// attendance cache is HolidayVersion-stamped.</summary>
    private int _loadedHolidayVersion = -1;

    /// <summary>Third sibling, for _dataVersion.AttendanceInputs -- the device-punch/
    /// manual-punch/pairing counters as one comparable value, compared with a raw `!=`: none of
    /// them records which employees it touched. Closes the mirror image of the gap
    /// _loadedScheduleVersion closes: a payroll figure is the schedule compared against the
    /// punches, and nothing on the Payroll page itself can edit punches, so a page revisit is
    /// the earliest moment such a change could need picking up. Null until the first
    /// successful load.</summary>
    private AttendanceInputsVersion? _loadedAttendanceInputs;

    /// <summary>A RequestRefresh() that arrived while _busy.IsRunning (a refresh, or an
    /// Add/Edit/Delete round trip, in flight) -- re-run once IsRunning drops, rather than
    /// starting a second, concurrent load against the shared ScheduleDbContext. Also cleared
    /// by RefreshAsync itself (see there).</summary>
    private bool _refreshPending;

    /// <summary>Cancels the summary load RefreshAsync has in flight, if any, so a
    /// selection/period change mid-load stops it at its next await instead of finishing a
    /// result nothing will display. Owned by this class and only ever linked into its own
    /// load -- deliberately NOT _busy.Cancel(), which would cancel whatever holds the app-wide
    /// busy state right now, possibly an unrelated write. Cancelling doesn't replace
    /// _refreshPending's replay: the cancelled run still has to release _busy before the next
    /// one can start; cancelling just makes the gap shorter.</summary>
    private CancellationTokenSource? _switchCts;

    private readonly IObservable<bool> _canEditAdjustments;
    private readonly IObservable<bool> _canUsePayslip;
    private readonly IObservable<bool> _canRefreshOrCancelPayslip;

    /// <summary>Raised at the end of LoadCoreAsync with the just-computed employee's Id and
    /// NetPay, so PayrollGroupViewModel can patch that one row of its table -- an event rather
    /// than a reach-in, since Group depends on Summary and not the other way around (see the
    /// refactor plan's "Group &lt;-&gt; Summary" wrinkle).</summary>
    public event Action<int, decimal>? EmployeeNetPayComputed;

    public PayrollSummaryViewModel(
        IEmployeeSelection selection,
        IPayrollComputationService payrollComputationService,
        IPayrollAdjustmentRepository adjustmentRepository,
        IPayrollUndertimeWaiverRepository undertimeWaiverRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope,
        AttendanceDataVersion dataVersion,
        string companyName)
        : base(statusBarService)
    {
        _selection = selection;
        _payrollComputationService = payrollComputationService;
        _adjustmentRepository = adjustmentRepository;
        _undertimeWaiverRepository = undertimeWaiverRepository;
        _dataVersion = dataVersion;
        _companyName = companyName;
        _busy = busy;
        _scope = scope;

        _selectedEmployeeHelper = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => _selection.PropertyChanged += handler,
                handler => _selection.PropertyChanged -= handler)
            .Where(e => e.EventArgs.PropertyName is nameof(IEmployeeSelection.SelectedEmployee) or null or "")
            .Select(_ => _selection.SelectedEmployee)
            .StartWith(_selection.SelectedEmployee)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.SelectedEmployee);
        _headerTextHelper = this.WhenAnyValue(x => x.SelectedEmployee)
            .Select(employee => employee?.DisplayName ?? "Select an employee")
            .ToProperty(this, x => x.HeaderText);

        _isBusyHelper = _busy.WhenAnyValue(b => b.IsVisiblyRunning).ToProperty(this, x => x.IsBusy);
        _refreshOrCancelGlyphHelper = this.WhenAnyValue(x => x.IsBusy)
            .Select(busy => busy ? "" : "")
            .ToProperty(this, x => x.RefreshOrCancelGlyph);
        _refreshOrCancelToolTipHelper = this.WhenAnyValue(x => x.IsBusy)
            .Select(busy => busy
                ? "Stop whatever's currently running."
                : "Recalculate this employee's payslip using the latest attendance and adjustments for this period.")
            .ToProperty(this, x => x.RefreshOrCancelToolTip);

        _hasAttendanceRowsHelper = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => AttendanceRows.CollectionChanged += handler,
                handler => AttendanceRows.CollectionChanged -= handler)
            .Select(_ => AttendanceRows.Count > 0)
            .StartWith(false)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.HasAttendanceRows);

        // Checked in the same order RequestRefresh itself would bail, so the message always
        // explains the *actual* reason nothing's showing.
        _emptyStateMessageHelper = Observable.CombineLatest(
                this.WhenAnyValue(x => x.Result, x => x.SelectedEmployee, x => x.IsBusy,
                    (result, employee, busy) => (Result: result, Employee: employee, Busy: busy)),
                _scope.WhenAnyValue(s => s.PeriodStart, s => s.PeriodEnd, s => s.ActivePayrollRunId,
                    (start, end, runId) => (Backwards: end < start, RunId: runId)),
                (mine, scopeState) => DescribeEmpty(mine.Result, mine.Employee, scopeState.Backwards, scopeState.RunId, mine.Busy))
            .ToProperty(this, x => x.EmptyStateMessage);
        _attendanceEmptyStateMessageHelper = this.WhenAnyValue(x => x.EmptyStateMessage, x => x.HasAttendanceRows,
                (message, hasRows) => message ?? (hasRows ? null : "No attendance records for the selected period."))
            .ToProperty(this, x => x.AttendanceEmptyStateMessage);

        var notRunning = _busy.WhenAnyValue(b => b.IsRunning).Select(running => !running);
        _canEditAdjustments = Observable.CombineLatest(notRunning, this.WhenAnyValue(x => x.SelectedEmployee),
                (idle, employee) => idle && employee is not null)
            .DistinctUntilChanged();
        _canEditAdjustmentsNowHelper = _canEditAdjustments.ToProperty(this, x => x.CanEditAdjustmentsNow);
        _canUsePayslip = Observable.CombineLatest(notRunning, this.WhenAnyValue(x => x.Result),
                (idle, result) => idle && result is not null)
            .DistinctUntilChanged();
        _canRefreshOrCancelPayslip = Observable.CombineLatest(this.WhenAnyValue(x => x.IsBusy), _canUsePayslip,
            (busy, canRecalculate) => busy || canRecalculate);

        ReportFailuresOf(AddInlineRowCommand, DeleteAdjustmentCommand, PrintCurrentPayslipCommand,
            RecalculatePayslipCommand, RefreshOrCancelPayslipCommand);

        this.WhenAnyValue(x => x.SelectedEmployee).Skip(1).Subscribe(_ => RequestRefresh());
        _scope.WhenAnyValue(s => s.PeriodStart, s => s.PeriodEnd, s => s.ActivePayrollRunId)
            .Skip(1)
            .Subscribe(_ => RequestRefresh());

        // A change that arrived while something else was running -- see _refreshPending.
        _busy.WhenAnyValue(b => b.IsRunning)
            .Skip(1)
            .Where(running => !running)
            .Subscribe(_ =>
            {
                if (!_refreshPending) return;
                _refreshPending = false;
                RequestRefresh();
            });

        // Picks up whatever SelectedEmployee already is: this ViewModel isn't built until the
        // Payroll tab is first opened, routinely well after last session's selection was
        // restored, so the subscriptions above -- future changes only -- would never load it.
        RequestRefresh();
    }

    /// <summary>The employee selected app-wide -- see <see cref="IEmployeeSelection"/>.</summary>
    [ObservableAsProperty]
    public partial Employee? SelectedEmployee { get; }

    [ObservableAsProperty(InitialValue = "Select an employee")]
    public partial string HeaderText { get; }

    /// <summary>The most recent computed breakdown for SelectedEmployee across the period --
    /// null before the first successful load, or whenever nothing is selected, the period is
    /// backwards, or no payroll group is loaded (see RequestRefresh). PayrollSummaryView binds
    /// straight to it for the figures: PayrollResult is already immutable and shaped the way
    /// the view needs. The adjustment groups are the exception, surfaced through
    /// GrossPayAdjustmentGroupRows/DeductionAdjustmentGroupRows and patched in place so a
    /// fresh list on every load doesn't rebuild every category card on every edit.</summary>
    [Reactive]
    public partial PayrollResult? Result { get; private set; }

    /// <summary>One PayrollAdjustmentGroupRow per gross-pay PayrollAdjustmentType
    /// (Allowance/Incentive/Premium Pay), patched in place by every load -- see
    /// SyncAdjustmentGroupRows and PayrollAdjustmentGroupRow's own doc comment.</summary>
    public ObservableCollection<PayrollAdjustmentGroupRow> GrossPayAdjustmentGroupRows { get; } = [];

    /// <summary>Deductions-column counterpart to GrossPayAdjustmentGroupRows.</summary>
    public ObservableCollection<PayrollAdjustmentGroupRow> DeductionAdjustmentGroupRows { get; } = [];

    /// <summary>This employee's AttendanceSummary rows for the same period, in the same
    /// display-ready shape the Attendance tab's Summary grid uses -- the attendance basis behind
    /// the figures right above it. Replaced in one change per load.</summary>
    public RangeObservableCollection<AttendanceSummaryRow> AttendanceRows { get; } = [];

    [ObservableAsProperty]
    public partial bool HasAttendanceRows { get; }

    /// <summary>Drives the progress bar PayrollSummaryView/EmployeeAttendancePanel show while
    /// a refresh is in flight -- mirrors _busy.IsVisiblyRunning 1:1.</summary>
    [ObservableAsProperty]
    public partial bool IsBusy { get; }

    /// <summary>Why <see cref="Result"/> is null -- shown in place of the breakdown; null
    /// exactly when Result isn't.</summary>
    [ObservableAsProperty]
    public partial string? EmptyStateMessage { get; }

    /// <summary>Same idea, for EmployeeAttendancePanel: EmptyStateMessage's own reason first,
    /// then "no attendance rows" once Result exists but the period has none.</summary>
    [ObservableAsProperty]
    public partial string? AttendanceEmptyStateMessage { get; }

    /// <summary>Whether adjustments can be edited right now (an employee selected, nothing else
    /// running) -- what the amount and description boxes' IsEnabled binds to, since a TextBox
    /// has no CanExecute of its own.</summary>
    [ObservableAsProperty]
    public partial bool CanEditAdjustmentsNow { get; }

    /// <summary>Segoe Fluent Icons Cancel while busy, Refresh otherwise -- see
    /// ReportViewModel.RefreshOrCancelGlyph.</summary>
    [ObservableAsProperty(InitialValue = "")]
    public partial string RefreshOrCancelGlyph { get; }

    /// <summary>Generic on purpose, not "Stop this recalculation": _busy is shared app-wide,
    /// so IsBusy can be true because of anything running anywhere in the app.</summary>
    [ObservableAsProperty]
    public partial string RefreshOrCancelToolTip { get; }

    private static string? DescribeEmpty(PayrollResult? result, Employee? employee, bool periodBackwards, int? runId, bool busy)
    {
        if (result is not null) return null;
        if (employee is null) return "Select an employee to see their payroll breakdown.";
        if (periodBackwards) return "Period end can't be before period start.";
        if (runId is null)
            return "Start a \"New Payroll Run…\" or \"Load Payroll Group…\" to see this employee's payroll breakdown.";
        return busy ? "Loading…" : "Nothing to show yet.";
    }

    /// <summary>
    /// Entry point for every employee-selection, period, and payroll-group change -- NOT used by
    /// the Add/Edit/Delete writes, which reload inside their own _busy window (see
    /// LoadCoreAsync).
    ///
    /// Silently clears everything when nothing is selected, the period is backwards, or no
    /// payroll group is loaded -- all normal states (mid-pick, mid-edit, or before a run is
    /// started), not errors. The payroll-group check matters because the selection is shared
    /// app-wide: someone selected on the Schedule tab before Payroll was ever opened would
    /// otherwise get a breakdown with no payroll group behind it.
    ///
    /// Otherwise defers while _busy.IsRunning -- two date pickers side by side make a quick
    /// Start-then-End edit routine, well within an in-flight load, and a second concurrent load
    /// against the shared ScheduleDbContext threw "A second operation was started on this
    /// context instance before a previous operation completed."
    /// </summary>
    private void RequestRefresh()
    {
        // Before anything else, including the clear-and-return below: whatever is still
        // loading was for the employee/period on screen a moment ago, and an in-flight load
        // left running through the clear would repopulate Result for someone no longer
        // selected.
        _switchCts?.Cancel();

        if (SelectedEmployee is null || _scope.PeriodEnd < _scope.PeriodStart || _scope.ActivePayrollRunId is null)
        {
            Result = null;
            GrossPayAdjustmentGroupRows.Clear();
            DeductionAdjustmentGroupRows.Clear();
            AttendanceRows.Clear();
            _refreshPending = false;
            return;
        }

        if (_busy.IsRunning)
        {
            _refreshPending = true;
            return;
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// Recomputes SelectedEmployee's payroll for the period (scoped to just their Pin) and
    /// republishes Result/AttendanceRows. Trusts its caller to have checked there's a selected
    /// employee, a valid period and a payroll group (see RequestRefresh).
    ///
    /// internal for PayrollGroupViewModel.RemoveEmployeeFromGroupAsync, which calls it
    /// reentrantly from inside its own _busy window. Returns whether it succeeded, since
    /// _busy.RunAsync reports a failure through onError rather than rethrowing. Clears
    /// _refreshPending unconditionally, so that reentrant call can't leave a stale pending
    /// reload for the busy-idle replay -- it always reads the selection and period fresh, so it
    /// already satisfies whatever change set the flag.
    /// </summary>
    internal async Task<bool> RefreshAsync()
    {
        _refreshPending = false;

        // Read at the START -- see PayrollGroupViewModel's own _loadedScheduleVersion.
        var scheduleVersionAtLoadStart = _dataVersion.ScheduleVersion;
        var holidayVersionAtLoadStart = _dataVersion.HolidayVersion;
        var attendanceInputsAtLoadStart = _dataVersion.AttendanceInputs;
        var succeeded = true;

        // Safe to dispose the previous run's source here: _busy's gate serializes callers, so
        // the run it belonged to has already finished.
        _switchCts?.Dispose();
        var switchCts = new CancellationTokenSource();
        _switchCts = switchCts;

        await _busy.RunAsync(
            visibly: true,
            async ct =>
            {
                // Either source ends this run: ct for the app-wide reasons (Cancel, shutdown),
                // switchCts for "the person has already moved on to another employee."
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, switchCts.Token);
                await LoadCoreAsync(adjustmentsOnly: false, linked.Token);
            },
            onError: ex =>
            {
                succeeded = false;

                // A load this class deliberately abandoned isn't a failure. Keyed off our own
                // token rather than the exception type: SqlClient often reports a cancelled
                // query as a SqlException (error 3980) rather than OperationCanceledException,
                // and the shared ScheduleDbContext survives that abort fine.
                if (switchCts.IsCancellationRequested)
                    return;

                ShowFailure(ex, "Could not load payroll");
            });

        // A cancelled run isn't a successful one even though nothing reported an error (RunAsync
        // swallows cancellation), and committing its stamps would make RecheckOnPageRevisitAsync
        // skip the reload it still owes.
        var completed = succeeded && !switchCts.IsCancellationRequested;
        if (completed)
        {
            _loadedScheduleVersion = scheduleVersionAtLoadStart;
            _loadedHolidayVersion = holidayVersionAtLoadStart;
            _loadedAttendanceInputs = attendanceInputsAtLoadStart;
        }

        return completed;
    }

    /// <summary>
    /// The single-employee counterpart to PayrollGroupViewModel.RecheckOnPageRevisitAsync,
    /// called alongside it every visit to the Payroll page: refreshing the group's NetPay column
    /// says nothing about SelectedEmployee's own detailed breakdown if their inputs are what
    /// changed. Same three checks as the group's, against a one-element set and this class's own
    /// stamps. Guards the selection/period/payroll group itself, since it's an entry point
    /// RequestRefresh's guard didn't run for.
    /// </summary>
    internal async Task RecheckOnPageRevisitAsync()
    {
        if (SelectedEmployee is not { Pin: var pin }) return;
        if (_scope.PeriodEnd < _scope.PeriodStart || _scope.ActivePayrollRunId is null) return;

        if (_dataVersion.HolidayVersion != _loadedHolidayVersion
            || _dataVersion.AttendanceInputs != _loadedAttendanceInputs
            || _dataVersion.AnyScheduleChangeSince([pin], _loadedScheduleVersion))
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// The load itself, shared by RefreshAsync and every write below, so a write and its reload
    /// run inside *one* _busy.RunAsync call rather than two.
    ///
    /// <paramref name="adjustmentsOnly"/> is false from RefreshAsync, where the employee or
    /// period genuinely changed and attendance has to be recomputed for real; true from every
    /// write, which can only change an adjustment row or the undertime waiver -- never
    /// attendance -- so it reuses the attendance last computed for this same employee/period
    /// (ComputeOneAdjustmentsOnlyAsync, which still falls back to the real thing on a cache
    /// miss).
    /// </summary>
    private async Task LoadCoreAsync(bool adjustmentsOnly, CancellationToken cancellationToken)
    {
        var employee = SelectedEmployee!;
        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);

        var (result, summaries) = adjustmentsOnly
            ? await _payrollComputationService.ComputeOneAdjustmentsOnlyAsync(employee, start, end, cancellationToken)
            : await _payrollComputationService.ComputeOneAsync(employee, start, end, cancellationToken);
        Result = result;

        // Monthly-rated only -- a Daily-rated employee's unscheduled days are just absences.
        // Here rather than in the computation service, whose batch loops would fire one of
        // these per employee.
        if (employee.EmployeeType == EmployeeType.Monthly && result.UnscheduledDayCount > 0)
            StatusBar.ShowCaution(
                $"{result.UnscheduledDayCount} day(s) this period have no schedule " +
                $"entry for \"{employee.DisplayName}\" -- they won't reduce the " +
                "divisor or count as an absence. Mark them Rest Day if that's what they are.",
                "Unscheduled day(s) in period");

        SyncAdjustmentGroupRows(GrossPayAdjustmentGroupRows, result.GrossPayAdjustmentGroups);
        SyncAdjustmentGroupRows(DeductionAdjustmentGroupRows, result.DeductionAdjustmentGroups);
        AttendanceRows.ReplaceAll(AttendanceSummaryRow.BuildRows(summaries));

        EmployeeNetPayComputed?.Invoke(employee.Id, result.NetPay);
    }

    /// <summary>
    /// Patches <paramref name="rows"/> in place to match <paramref name="groups"/>, matching by
    /// Type rather than position: PremiumHoliday's group is omitted entirely for an employee who
    /// doesn't qualify for premium pay, and an index-based patch then quietly showed one type's
    /// figures under another's header (see PayrollAdjustmentGroupRow's own doc comment). Removes
    /// rows whose Type is gone, then walks <paramref name="groups"/> in order, moving or
    /// inserting -- a Type that persists keeps its exact row instance.
    /// </summary>
    private static void SyncAdjustmentGroupRows(
        ObservableCollection<PayrollAdjustmentGroupRow> rows, IReadOnlyList<PayrollAdjustmentGroup> groups)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!groups.Any(g => g.Type == rows[i].Type))
                rows.RemoveAt(i);
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var existingIndex = IndexOf(rows, row => row.Type == group.Type);

            if (existingIndex < 0)
                rows.Insert(Math.Min(i, rows.Count), new PayrollAdjustmentGroupRow { Type = group.Type });
            else if (existingIndex != i)
                rows.Move(existingIndex, i);

            var row = rows[i];

            // Compared by content: the repository reads AsNoTracking, so every load hands back
            // brand-new PayrollAdjustment instances, and assigning them unconditionally would
            // re-notify (and re-format) every category's amount box on every edit anywhere.
            if (!SingleValueAmountEquals(row.SingleValueAdjustment, group.SingleValueAdjustment))
                row.SingleValueAdjustment = group.SingleValueAdjustment;

            row.Subtotal = group.Subtotal;
            SyncAdjustmentRows(row.Adjustments, group.Adjustments);
        }
    }

    /// <summary>Both null, or both with the same Amount -- the only field the single-value
    /// amount box shows.</summary>
    private static bool SingleValueAmountEquals(PayrollAdjustment? a, PayrollAdjustment? b) =>
        a is null || b is null ? a is null && b is null : a.Amount == b.Amount;

    /// <summary>The itemized-row counterpart to SyncAdjustmentGroupRows, for the genuinely
    /// variable-length row lists, matched by PayrollAdjustment.Id. PayrollAdjustment isn't
    /// observable, so a changed row lands as its new instance; an unchanged one keeps its
    /// existing instance (and item container).</summary>
    private static void SyncAdjustmentRows(
        ObservableCollection<PayrollAdjustment> rows, IReadOnlyList<PayrollAdjustment> source)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!source.Any(a => a.Id == rows[i].Id))
                rows.RemoveAt(i);
        }

        for (var i = 0; i < source.Count; i++)
        {
            var incoming = source[i];
            var existingIndex = IndexOf(rows, row => row.Id == incoming.Id);

            if (existingIndex < 0)
            {
                rows.Insert(Math.Min(i, rows.Count), incoming);
                continue;
            }

            if (existingIndex != i)
                rows.Move(existingIndex, i);

            if (rows[i].Amount != incoming.Amount ||
                rows[i].Description != incoming.Description ||
                rows[i].EnteredBy != incoming.EnteredBy)
            {
                rows[i] = incoming;
            }
        }
    }

    private static int IndexOf<T>(ObservableCollection<T> items, Func<T, bool> match)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (match(items[i])) return i;
        }

        return -1;
    }

    /// <summary>Incentive/OtherCharge's "+ Add Row" button -- no dialog: creates the row
    /// immediately with a blank Description and 0.00, and the person types the real values into
    /// the row that appears (see UpdateInlineDescriptionAsync/UpdateInlineAmountAsync). An
    /// untouched empty row is harmless clutter its own Delete button removes.</summary>
    [ReactiveCommand(CanExecute = nameof(_canEditAdjustments))]
    private async Task AddInlineRowAsync(PayrollAdjustmentType type)
    {
        if (SelectedEmployee is not { } employee) return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.AddAsync(new PayrollAdjustment
            {
                EmployeeId = employee.Pin,
                PeriodStart = DateOnly.FromDateTime(_scope.PeriodStart),
                PeriodEnd = DateOnly.FromDateTime(_scope.PeriodEnd),
                Type = type,
                Amount = 0,
                Description = string.Empty,
                EnteredBy = Environment.UserName,
            }, cancellationToken);

            await LoadCoreAsync(adjustmentsOnly: true, cancellationToken);
        },
        onError: ex => ShowFailure(ex));
    }

    /// <summary>An itemized row's Description box, on LostFocus/Enter (a TextBox has no
    /// Command, so PayrollSummaryView calls this directly). Only Description changes; a no-op
    /// when the trimmed text is already what's saved (LostFocus firing again after Enter
    /// committed it).</summary>
    public async Task UpdateInlineDescriptionAsync(PayrollAdjustment original, string rawDescription)
    {
        if (_busy.IsRunning) return;

        var description = rawDescription.Trim();
        if (description == original.Description) return;

        await UpdateAdjustmentAsync(CopyOf(original, description: description));
    }

    /// <summary>An itemized row's Amount box -- the Description box's counterpart. Text that
    /// doesn't parse to a non-negative amount is a silent no-op; zero is allowed, since a new
    /// row starts at 0.00.</summary>
    public async Task UpdateInlineAmountAsync(PayrollAdjustment original, string rawAmountText)
    {
        if (_busy.IsRunning) return;
        if (!decimal.TryParse(rawAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            return;
        if (amount == original.Amount) return;

        await UpdateAdjustmentAsync(CopyOf(original, amount: amount));
    }

    /// <summary>A fresh copy of <paramref name="original"/> to save -- the one passed in is
    /// the row on screen, which has to stay as it was until the reload replaces it.</summary>
    private static PayrollAdjustment CopyOf(
        PayrollAdjustment original, decimal? amount = null, string? description = null, string? enteredBy = null) => new()
    {
        Id = original.Id,
        EmployeeId = original.EmployeeId,
        PeriodStart = original.PeriodStart,
        PeriodEnd = original.PeriodEnd,
        Type = original.Type,
        Amount = amount ?? original.Amount,
        Description = description ?? original.Description,
        EnteredBy = enteredBy ?? original.EnteredBy,
    };

    private async Task UpdateAdjustmentAsync(PayrollAdjustment updated) =>
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.UpdateAsync(updated, cancellationToken);
            await LoadCoreAsync(adjustmentsOnly: true, cancellationToken);
        },
        onError: ex => ShowFailure(ex));

    /// <summary>Each itemized row's Delete button -- deletes the row outright, after a Yes/No
    /// confirmation (the status bar can't block for an answer).</summary>
    [ReactiveCommand(CanExecute = nameof(_canEditAdjustments))]
    private async Task DeleteAdjustmentAsync(PayrollAdjustment adjustment)
    {
        if (!await ConfirmAsync(
                $"Delete this {adjustment.Type.ToText()} line (\"{adjustment.Description}\", " +
                $"{adjustment.Amount:N2})?",
                "Delete payroll adjustment"))
            return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.DeleteAsync(adjustment.Id, cancellationToken);
            await LoadCoreAsync(adjustmentsOnly: true, cancellationToken);

            StatusBar.ShowSuccess("Payroll adjustment deleted.");
        },
        onError: ex => ShowFailure(ex));
    }

    /// <summary>
    /// A single-value category's amount box, on LostFocus/Enter -- typing the number is the
    /// whole interaction: Description is the type's own label and EnteredBy the current Windows
    /// user. Creates this employee/period's one row for the type, or updates its Amount --
    /// never a second row.
    ///
    /// Text that doesn't parse, or parses negative, is a silent no-op (the box reformats back to
    /// Result's value on the next refresh) -- except blank, for a type whose
    /// BlankAmountMeansZero() (Allowance/Premium Pay/Cash Advance), which counts as an explicit
    /// 0 so the box, the Excel column and the payslip all read 0.00 alike. Zero itself is
    /// accepted: it's how a person deliberately zeroes SSS/PhilHealth/Pag-IBIG. The update
    /// stamps EnteredBy to the current user even for an unchanged-looking figure, which is what
    /// marks a seeded contribution as hand-owned so later loads leave it alone. An amount equal
    /// to the saved one is a no-op.
    /// </summary>
    public async Task SetSingleValueAsync(PayrollAdjustmentType type, string rawAmountText)
    {
        if (_busy.IsRunning) return;
        if (SelectedEmployee is not { } employee) return;

        decimal amount;
        if (string.IsNullOrWhiteSpace(rawAmountText) && type.BlankAmountMeansZero())
            amount = 0m;
        else if (!decimal.TryParse(rawAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount < 0)
            return;

        var existing = Result?.GrossPayAdjustmentGroups
            .Concat(Result.DeductionAdjustmentGroups)
            .FirstOrDefault(g => g.Type == type)?.SingleValueAdjustment;
        if (existing is not null && existing.Amount == amount) return;

        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            if (existing is null)
            {
                await _adjustmentRepository.AddAsync(new PayrollAdjustment
                {
                    EmployeeId = employee.Pin,
                    PeriodStart = start,
                    PeriodEnd = end,
                    Type = type,
                    Amount = amount,
                    Description = type.ToText(),
                    EnteredBy = Environment.UserName,
                }, cancellationToken);
            }
            else
            {
                await _adjustmentRepository.UpdateAsync(
                    CopyOf(existing, amount, enteredBy: Environment.UserName), cancellationToken);
            }

            await LoadCoreAsync(adjustmentsOnly: true, cancellationToken);

            StatusBar.ShowSuccess($"Updated {type.ToText()}.");
        },
        onError: ex => ShowFailure(ex));
    }

    /// <summary>The Undertime row's Exclude/Include toggle -- a plain on/off switch, no
    /// adjustment row (see IPayrollUndertimeWaiverRepository). A toggle only reaches here by
    /// actually flipping, so there's no unchanged-value check.</summary>
    public async Task SetUndertimeWaivedAsync(bool waived)
    {
        if (_busy.IsRunning) return;
        if (SelectedEmployee is not { } employee) return;

        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _undertimeWaiverRepository.SetWaivedAsync(employee.Pin, start, end, waived, cancellationToken);
            await LoadCoreAsync(adjustmentsOnly: true, cancellationToken);

            StatusBar.ShowSuccess(waived ? "Undertime disregarded." : "Undertime restored.");
        },
        onError: ex => ShowFailure(ex));
    }

    /// <summary>"Print Current Payslip" (Print_Feature.md build-order step 4): previews the
    /// already-loaded Result rather than recomputing it. Not while something's running, nor
    /// before a payslip has loaded.</summary>
    [ReactiveCommand(CanExecute = nameof(_canUsePayslip))]
    private async Task PrintCurrentPayslipAsync()
    {
        if (Result is null) return;

        await ShowDialogAsync(new PayslipPreviewViewModel([Result], _companyName));
    }

    /// <summary>Recomputes SelectedEmployee's payslip on demand -- the same compute the
    /// automatic path runs, but reporting success, since this one was a direct click. Calls
    /// RefreshAsync directly: the command's own CanExecute (a payslip loaded, nothing running)
    /// already covers everything RequestRefresh's guard would.</summary>
    [ReactiveCommand(CanExecute = nameof(_canUsePayslip))]
    private async Task RecalculatePayslipAsync()
    {
        if (await RefreshAsync())
            StatusBar.ShowSuccess("Payslip recalculated.");
    }

    /// <summary>The payslip header's one button, toggling in place like
    /// ReportViewModel.RefreshOrCancelSummary: Cancel while anything's running (always fine to
    /// try), Recalculate otherwise. Synchronous, so the button stays enabled -- as Cancel --
    /// while the recalculation it starts runs.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRefreshOrCancelPayslip))]
    private void RefreshOrCancelPayslip()
    {
        if (IsBusy)
            _busy.Cancel();
        else
            RecalculatePayslipCommand.Execute().Subscribe(_ => { }, _ => { });
    }
}
