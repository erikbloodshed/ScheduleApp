using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>Concern 1 of the Payroll refactor plan (see PayrollViewModel_Refactor_Plan.md's
/// "What's tangled together" and "Full member mapping") -- extracted from PayrollViewModel
/// as build-order step 2. Owns the single-employee breakdown for whichever employee is
/// selected in the shared MainViewModel.Departments tree: the itemized Gross Pay/
/// Deductions/Net Pay figures (<see cref="Result"/>), every adjustment CRUD method, the
/// read-only attendance grid underneath it (<see cref="AttendanceRows"/>), and Print
/// Current Payslip -- recomputed live via RefreshAsync every time the selected employee or
/// period changes, or an adjustment is added/edited/deleted below, same as this all worked
/// before the split (see PayrollViewModel's own doc comment for the broader "why no
/// separate Generate action" story, which still applies unchanged here).
///
/// PayrollSummaryView.xaml's DataContext and PayrollSummaryView.xaml.cs/
/// EmployeeAttendancePanel.xaml's own bindings are all typed directly to PayrollViewModel
/// (the facade), never to this class -- so every member here that XAML or code-behind
/// touches is forwarded back out under the same name via PayrollViewModel.Summary (see that
/// class's own "Forwarded members" region). Nothing in this class itself needs to know that;
/// it's exactly as constructible/testable in isolation as ReportViewModel is for the
/// Attendance tab.
///
/// Takes PayrollScopeState and AttendanceBusyState the same way the eventual
/// PayrollGroupViewModel/PayrollRunViewModel/PayrollPrintExportViewModel will (see the
/// refactor plan's own "Dependency graph") -- both are shared, constructed on
/// PayrollViewModel and passed in here, not owned by this class.</summary>
public partial class PayrollSummaryViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IPayrollAdjustmentRepository _adjustmentRepository;
    private readonly IPayrollUndertimeWaiverRepository _undertimeWaiverRepository;
    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope;

    /// <summary>Already resolved to whatever's effective (PayrollSettings.CompanyName,
    /// or PayslipLineBuilder.DefaultCompanyName if that's blank/unset) -- see
    /// PayrollViewModel's own constructor for where that resolution happens once, up
    /// front, rather than this class reading PayrollSettings itself. Passed straight
    /// through to PayslipPreviewDialog by PrintCurrentPayslip below.</summary>
    private readonly string _companyName;

    /// <summary>Shared with PayrollGroupViewModel and the rest of the app -- see
    /// App.xaml.cs's own registration of this class. Read by RecheckOnPageRevisitAsync
    /// below, the same role it plays for PayrollGroupViewModel's own version of this same
    /// check -- see that method's own doc comment.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>Snapshot of _dataVersion.ScheduleVersion taken the moment RefreshAsync last
    /// successfully ran ComputeOneAsync's own real attendance recompute for SelectedEmployee
    /// -- same "read at the start, commit only on success" shape as PayrollGroupViewModel's
    /// own _loadedScheduleVersion (see that field's own doc comment for why). Deliberately
    /// NOT touched by an adjustments-only reload (AddInlineRowAsync/UpdateInlineDescriptionAsync/
    /// DeleteAdjustmentAsync/the waiver toggle below) -- those reuse the attendance summary
    /// ComputeOneAdjustmentsOnlyAsync already had cached rather than re-running
    /// IAttendanceRunner.RunAsync (see LoadCoreAsync's own adjustmentsOnly doc comment), so
    /// they never actually pick up a schedule change either; only RefreshAsync's own
    /// adjustmentsOnly: false path does.</summary>
    private int _loadedScheduleVersion = -1;

    /// <summary>Sibling of _loadedScheduleVersion above, for _dataVersion.HolidayVersion --
    /// snapshotted and committed the same way by RefreshAsync, and compared with a raw `!=`
    /// in RecheckOnPageRevisitAsync (holidays are company-wide -- see
    /// AttendanceDataVersion.HolidayVersion's own doc comment -- so there's no per-pin
    /// AnyScheduleChangeSince equivalent to run). Unlike _loadedScheduleVersion, a holiday
    /// change genuinely can affect an adjustments-only reload's numbers too, but
    /// PayrollComputationService's own _lastAttendanceRun cache is HolidayVersion-stamped
    /// now (see that field), so ComputeOneAdjustmentsOnlyAsync already falls back to a full
    /// recompute on a holiday change without this class having to force adjustmentsOnly:
    /// false itself.</summary>
    private int _loadedHolidayVersion = -1;

    /// <summary>Set by RequestRefresh() when an employee-selection or period change
    /// arrives while _busy.IsRunning is already true (a refresh, or an Add/Edit/Delete
    /// round trip, in flight), and cleared by the _busy.PropertyChanged handler below once
    /// it uses this to decide whether to re-run RequestRefresh() after IsRunning drops
    /// back to false -- and by RefreshAsync() itself, unconditionally, at the start of
    /// every run (see that method's own doc comment for why). See RequestRefresh's own doc
    /// comment for why this exists at all -- without it, a second Period edit landing
    /// before the first one's LoadCoreAsync had finished would start a second, concurrent
    /// LoadCoreAsync against the same shared ScheduleDbContext instance instead of
    /// queueing. PayrollGroupViewModel's own _payrollGroupRefreshPending/
    /// _fullRosterRefreshPending fields play the exact same role for its own
    /// concern -- kept as separate flags on separate classes now that the two live apart,
    /// same "independently pending for different reasons" story those two fields' own doc
    /// comments already tell.</summary>
    private bool _refreshPending;

    /// <summary>Raised at the end of LoadCoreAsync with the just-computed employee's Id and
    /// NetPay -- replaces a direct reach-in this class used to make into
    /// PayrollViewModel.PayrollGroupRows (PayrollGroupViewModel's eventual collection) back
    /// when both concerns lived on the same class. See the refactor plan's own "Group &lt;-&gt;
    /// Summary" wrinkle for the full story of why this couldn't stay a direct call once the
    /// two split apart in different directions (Group ends up depending on Summary, not the
    /// other way around).
    ///
    /// Unsubscribed as of this build-order step (step 2) -- PayrollGroupViewModel doesn't
    /// exist yet, so there's nothing to wire up (see the plan's own build-order step 2:
    /// "fine even before Group exists -- just leave the event unsubscribed for now").
    /// PayrollGroupRows' NetPay for whichever employee is currently selected just waits for
    /// the next full RefreshPayrollGroupRowsAsync in the meantime, instead of updating the
    /// instant this employee's own detail panel reloads -- a temporary, purely cosmetic gap
    /// that step 3 (extracting PayrollGroupViewModel) closes by subscribing to this from its
    /// own constructor, the same patch-in-place reaction this class used to run directly.
    /// </summary>
    public event Action<int, decimal>? EmployeeNetPayComputed;

    public PayrollSummaryViewModel(
        MainViewModel mainViewModel,
        IPayrollComputationService payrollComputationService,
        IPayrollAdjustmentRepository adjustmentRepository,
        IPayrollUndertimeWaiverRepository undertimeWaiverRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        PayrollScopeState scope,
        AttendanceDataVersion dataVersion,
        string companyName)
    {
        _mainViewModel = mainViewModel;
        _payrollComputationService = payrollComputationService;
        _adjustmentRepository = adjustmentRepository;
        _undertimeWaiverRepository = undertimeWaiverRepository;
        _statusBarService = statusBarService;
        _dataVersion = dataVersion;
        _companyName = companyName;

        // Shared with PayrollViewModel and its other eventual children -- see this field's
        // own doc comment and PayrollViewModel's own _busy/_scope doc comments for why.
        _busy = busy;
        _scope = scope;

        _busy.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                // IsBusy is a plain projection of IsVisiblyRunning under its own name, so
                // PayrollSummaryView/EmployeeAttendancePanel's progress bar doesn't need to
                // know AttendanceBusyState exists -- same "forward under this class's own
                // name" idea PayrollViewModel's own now-removed IsBusy handler used to
                // follow, moved here unchanged since IsBusy is this class's own member now.
                case nameof(AttendanceBusyState.IsVisiblyRunning):
                    OnPropertyChanged(nameof(IsBusy));
                    NotifyEmptyStates();
                    break;

                // Every refresh this class runs (employee change, period edit, or an
                // Add/Edit/Delete round trip) was triggered by something the person did
                // directly -- there's no silent tab-reselect case the way Attendance has --
                // so IsRunning and IsVisiblyRunning always flip together here; CanExecute is
                // still keyed off IsRunning specifically (not IsBusy) to match
                // ManualEntryEditorViewModel's own CanAddManualEntry/CanEditManualEntry/
                // CanDeleteManualEntry convention.
                case nameof(AttendanceBusyState.IsRunning):
                    // Disable the instant _busy.IsRunning goes true, re-enable the instant
                    // it goes false -- no artificial hold in between. Same "PropertyChanged
                    // -> NotifyCanExecuteChanged, immediately, every time" behavior every
                    // other child's own NotifyXCommands() follows for its own commands (see
                    // PayrollGroupViewModel.NotifyGroupCommands/PayrollRunViewModel.
                    // NotifyRunCommands/PayrollPrintExportViewModel.NotifyPrintExportCommands).
                    NotifyAdjustmentCommands();

                    // Picks up an employee-selection or period change that arrived while
                    // a previous refresh (or an Add/Edit/Delete round trip) was still in
                    // flight -- RequestRefresh() below deliberately does NOT start a
                    // second, concurrent LoadCoreAsync against the shared, app-lifetime-
                    // scoped ScheduleDbContext while _busy.IsRunning is already true (see
                    // its own doc comment); this is what stops that deferred change from
                    // just going stale instead. Guarded on _refreshPending so a run that
                    // finishes with nothing new queued (the overwhelmingly common case)
                    // doesn't trigger a pointless extra reload of what it just loaded.
                    if (!_busy.IsRunning && _refreshPending)
                    {
                        _refreshPending = false;
                        RequestRefresh();
                    }
                    break;
            }
        };

        // Splits out of what used to be one switch (on PayrollViewModel, covering all four
        // eventual concerns at once) into just this class's own three cases -- PeriodStart/
        // PeriodEnd/ActivePayrollRunId all produce the identical two-call reaction here
        // (unlike PayrollGroupViewModel's own Group-only reaction to these same three
        // properties, which differs per property), so one shared case block covers all
        // three rather than three near-identical named methods. BatchScopeEmployees isn't
        // listed at all -- nothing here reads it, so this class has no reaction to it.
        _scope.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PayrollScopeState.PeriodStart):
                case nameof(PayrollScopeState.PeriodEnd):
                case nameof(PayrollScopeState.ActivePayrollRunId):
                    NotifyEmptyStates();
                    RequestRefresh();
                    break;
            }
        };

        // Both this and MainViewModel are Scoped with the app's one and only
        // IServiceScope living for the whole session (see App.xaml.cs's registration
        // comment), so this subscription's lifetime matches _mainViewModel's own --
        // nothing to unsubscribe, same as DepartmentGroupViewModel.AttachChildNotifications'
        // child-PropertyChanged subscriptions.
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;

        // Picks up whatever SelectedEmployee already is at construction time. This class
        // is constructed by PayrollViewModel, itself Scoped but -- unlike MainViewModel --
        // not built until the Payroll tab is navigated to for the first time (see
        // PayrollPage's constructor), which is routinely well after
        // MainViewModel.LoadAsync() has already restored last session's SelectedEmployee
        // (SchedulePage is the app's default page, so that restore -- and its one-time
        // SelectedEmployee change notification -- has already come and gone by then). The
        // _mainViewModel.PropertyChanged subscription above only reacts to *future*
        // changes, so without this call, that already-current selection would never
        // trigger a load: Result stays null, nothing sets IsBusy, and EmptyStateMessage
        // falls through to "Nothing to show yet." even though a perfectly valid employee is
        // selected. Safe to call unconditionally here the same way
        // OnMainViewModelPropertyChanged/the _scope.PropertyChanged reaction above do --
        // RequestRefresh() itself already no-ops (just clearing Result/AttendanceRows) when
        // nothing is selected, and queues via _refreshPending rather than double-loading if
        // _busy happens to already be running something MainViewModel kicked off.</summary>
        RequestRefresh();
    }

    /// <summary>Read straight off MainViewModel rather than duplicated here -- see
    /// PayrollViewModel's own doc comment for why the tree/selection itself stays on
    /// MainViewModel.</summary>
    public Employee? SelectedEmployee => _mainViewModel.SelectedEmployee;

    public string HeaderText => SelectedEmployee?.DisplayName ?? "Select an employee";

    /// <summary>The most recent computed breakdown for SelectedEmployee across
    /// PeriodStart..PeriodEnd -- null before the first successful load, or whenever nothing
    /// is selected/the period is backwards (see RefreshAsync). PayrollSummaryView binds
    /// straight to this for ComputedGrossPay/ComputedDeductions/TotalGrossPay/
    /// TotalDeductions/NetPay rather than this class copying those into a parallel set of
    /// properties -- PayrollResult is already immutable and already shaped exactly the way
    /// the view needs it (see PayrollResult's own doc comment), so a copy would add nothing.
    /// GrossPayAdjustmentGroups/DeductionAdjustmentGroups are the one exception -- those two
    /// are surfaced instead through GrossPayAdjustmentGroupRows/DeductionAdjustmentGroupRows
    /// below, patched in place rather than bound straight through, since a fresh
    /// PayrollAdjustmentGroup list every load would otherwise force PayrollSummaryView's
    /// ItemsControls to rebuild every category card on every single edit (see those
    /// properties' own doc comments).</summary>
    [ObservableProperty]
    private PayrollResult? result;

    /// <summary>PayrollSummaryView's Gross Pay column ItemsControl binds to this instead of
    /// Result.GrossPayAdjustmentGroups directly -- one PayrollAdjustmentGroupRow per
    /// PayrollAdjustmentType where IsDeduction() is false (Allowance/Incentive/Premium Pay,
    /// same fixed order as the source list), patched in place by LoadCoreAsync's
    /// SyncAdjustmentGroupRows call on every load instead of the ItemsControl tearing down and
    /// rebuilding every category card whenever Result itself is reassigned -- see
    /// PayrollAdjustmentGroupRow's own doc comment for why that matters. Empty until the first
    /// successful load, same as Result being null; cleared by RequestRefresh's empty-state
    /// branch alongside Result/AttendanceRows.</summary>
    public ObservableCollection<PayrollAdjustmentGroupRow> GrossPayAdjustmentGroupRows { get; } = [];

    /// <summary>Deductions-column counterpart to GrossPayAdjustmentGroupRows above -- what
    /// PayrollSummaryView's Deductions column ItemsControl binds to instead of
    /// Result.DeductionAdjustmentGroups, same "patched in place, not rebuilt" reasoning.</summary>
    public ObservableCollection<PayrollAdjustmentGroupRow> DeductionAdjustmentGroupRows { get; } = [];

    /// <summary>This employee's AttendanceSummary rows for the same period, in the same
    /// display-ready shape (AttendanceSummaryRow) the Attendance tab's own Summary grid uses
    /// -- what EmployeeAttendancePanel's grid binds to, letting a person see the attendance
    /// basis behind the figures in PayrollSummaryView right above it. Cleared and rebuilt by
    /// RefreshAsync every time (same Clear()-then-Add pattern as ReportViewModel.SummaryRows
    /// -- see its RunCoreAsync), rather than replaced wholesale, so the DataGrid this is
    /// bound to updates in place instead of a full ItemsSource swap.</summary>
    public ObservableCollection<AttendanceSummaryRow> AttendanceRows { get; } = [];

    public bool HasAttendanceRows => AttendanceRows.Count > 0;

    /// <summary>Drives the progress bar PayrollSummaryView/EmployeeAttendancePanel show
    /// while a refresh is in flight -- see the IsVisiblyRunning case in the constructor's
    /// _busy.PropertyChanged handler for why this always mirrors it 1:1 here (there's no
    /// silent-auto-load case on this tab the way Attendance's tab-reselect has).</summary>
    public bool IsBusy => _busy.IsVisiblyRunning;

    /// <summary>Explains why <see cref="Result"/> is currently null -- shown by
    /// PayrollSummaryView's placeholder in place of the itemized breakdown (via
    /// NotNullToVisibilityConverter bound to each). Null exactly when Result itself is
    /// non-null, so the two are mutually exclusive from the view's perspective. Checked in
    /// the same order RequestRefresh itself would bail, so this always explains the *actual*
    /// reason nothing's showing rather than a generic fallback -- including the
    /// ActivePayrollRunId check RequestRefresh's own guard now has, so this doesn't fall
    /// through to the generic "Nothing to show yet." for what's actually a distinct, common
    /// state (this tab freshly opened, or a session where a payroll group hasn't been
    /// started/loaded yet).</summary>
    public string? EmptyStateMessage
    {
        get
        {
            if (Result is not null) return null;
            if (SelectedEmployee is null) return "Select an employee to see their payroll breakdown.";
            if (_scope.PeriodEnd < _scope.PeriodStart) return "Period end can't be before period start.";
            if (_scope.ActivePayrollRunId is null)
                return "Start a \"New Payroll Run…\" or \"Load Payroll Group…\" to see this employee's payroll breakdown.";
            return IsBusy ? "Loading…" : "Nothing to show yet.";
        }
    }

    /// <summary>Same idea as EmptyStateMessage, for EmployeeAttendancePanel specifically --
    /// falls through to EmptyStateMessage's own reason first (no employee, bad period), and
    /// only adds its own message once Result exists but this employee simply has no
    /// attendance rows for the period (e.g. nothing scheduled yet).</summary>
    public string? AttendanceEmptyStateMessage =>
        EmptyStateMessage ?? (HasAttendanceRows ? null : "No attendance records for the selected period.");

    /// <summary>Raises change notifications for both empty-state messages above -- called
    /// wherever Result, HasAttendanceRows, SelectedEmployee, IsBusy, or the period changes,
    /// since each of those can flip what either message says.</summary>
    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(AttendanceEmptyStateMessage));
    }

    partial void OnResultChanged(PayrollResult? value)
    {
        NotifyEmptyStates();
        PrintCurrentPayslipCommand.NotifyCanExecuteChanged();
        RecalculatePayslipCommand.NotifyCanExecuteChanged();
    }

    /// <summary>SelectedEmployee changing only ever affects three things here:
    /// CanEditAdjustments (AddInlineRowCommand/DeleteAdjustmentCommand/
    /// CanEditAdjustmentsNow, via SelectedEmployee itself), CanPrintCurrentPayslip (via
    /// Result, which RequestRefresh below is about to update for the new selection
    /// anyway), and the display-only HeaderText/EmptyStateMessage projections. Every
    /// other toolbar command (NewPayrollRunCommand/LoadPayrollGroupCommand, now
    /// PayrollRunViewModel's own as of build-order step 4, and PrintPayslipsCommand/
    /// ExportPayrollReportCommand, now PayrollPrintExportViewModel's own as of build-order
    /// step 5) reads _busy.IsRunning alone -- see each one's own CanExecute -- so a
    /// SelectedEmployee change can never actually change what they'd return, and
    /// deliberately isn't notified here; they're already covered, correctly and
    /// consistently, by PayrollRunViewModel's own NotifyRunCommands()/
    /// PayrollPrintExportViewModel's own NotifyPrintExportCommands() respectively, whenever
    /// _busy.IsRunning itself changes.</summary>
    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedEmployee)) return;

        OnPropertyChanged(nameof(SelectedEmployee));
        OnPropertyChanged(nameof(HeaderText));
        NotifyEmptyStates();
        NotifyAdjustmentCommands();
        RequestRefresh();
    }

    /// <summary>Entry point for every employee-selection, period, and payroll-group change
    /// this class cares about (see the constructor's _scope.PropertyChanged subscription
    /// and OnMainViewModelPropertyChanged above) -- NOT called directly by
    /// AddInlineRowAsync/DeleteAdjustmentAsync/SetSingleValueAsync below, which already
    /// reload inside their own _busy.RunAsync window (see LoadCoreAsync's own doc comment
    /// for why that has to be one call, not two nested ones).
    ///
    /// Silently clears Result/AttendanceRows (rather than showing an error) when nothing is
    /// selected, the period is backwards, or no payroll group is loaded yet (ActivePayrollRunId
    /// is null), since all three are perfectly normal states -- the first two while the person
    /// is still picking an employee or mid-edit on a date field, the third for the entire time
    /// between opening this tab and running "New Payroll Run…"/"Load Payroll Group…" -- same
    /// "don't nag about an incomplete selection" spirit as ReportViewModel's own auto-run only
    /// firing once CanRun() passes, just without a separate validation-message path here since
    /// there's no explicit action being blocked, only a silent recompute. The ActivePayrollRunId
    /// check matters because SelectedEmployee is read straight off MainViewModel (see that
    /// property's own doc comment) rather than owned by this tab -- someone can already be
    /// selected in the Schedule/Employees tree from before this tab was ever opened, and without
    /// this guard that stale selection would compute and show a payroll breakdown for them even
    /// though no payroll group has been started or loaded here.
    ///
    /// Otherwise, defers to _busy.IsRunning before actually starting RefreshAsync: this
    /// class's own _busy is shared, app-lifetime-scoped ScheduleDbContext-backed state (see
    /// PayrollViewModel's own _busy doc comment), and AttendanceBusyState.RunAsync itself does
    /// nothing to stop two overlapping calls from both reaching the database at once -- it only
    /// tracks IsRunning, the same way every other user of this class does (see
    /// ReportViewModel.CanRun/TryAutoRun for the precedent this mirrors). Two DatePickers
    /// sitting right next to each other (see PayrollSummaryView.xaml) means picking a new
    /// Period Start and then a new Period End is a completely ordinary thing to do in quick
    /// succession, well within the time an in-flight IAttendanceRunner.RunAsync/
    /// IPayrollAdjustmentRepository round trip is still running -- without this guard, the
    /// second change's own RefreshAsync call would start a second LoadCoreAsync against the
    /// exact same ScheduleDbContext instance the first one is still using, which is what
    /// actually threw "A second operation was started on this context instance before a
    /// previous operation completed." A change that arrives while already busy sets
    /// _refreshPending instead, so the _busy.PropertyChanged handler above can re-run this
    /// once IsRunning drops back to false rather than dropping it on the floor.</summary>
    private void RequestRefresh()
    {
        if (SelectedEmployee is null || _scope.PeriodEnd < _scope.PeriodStart || _scope.ActivePayrollRunId is null)
        {
            Result = null;
            GrossPayAdjustmentGroupRows.Clear();
            DeductionAdjustmentGroupRows.Clear();
            AttendanceRows.Clear();
            OnPropertyChanged(nameof(HasAttendanceRows));
            NotifyEmptyStates();
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

    /// <summary>Re-runs IAttendanceRunner (scoped to just SelectedEmployee.Pin -- see
    /// AttendanceRunRequest.TargetPins) and IPayrollAdjustmentRepository for the current
    /// period, feeds both into PayrollCalculator, and republishes Result/AttendanceRows.
    /// Normally only reached from RequestRefresh() above, once that's already confirmed
    /// there's a selected employee, a valid (non-backwards) period, and that nothing else is
    /// currently using _busy -- see RequestRefresh's own doc comment for why that guard has
    /// to happen before this, not inside it.
    ///
    /// internal rather than private as of build-order step 2 of the Payroll refactor plan
    /// (see the plan's own "Group &lt;-&gt; Summary" wrinkle) -- PayrollGroupViewModel's own
    /// RemoveEmployeeFromGroupAsync calls this directly, reentrant, from inside its own
    /// _busy.RunAsync window, the same way it always has (back when both methods lived on
    /// one class); see that method's own doc comment for why the reload has to be nested
    /// inside its one busy window rather than a second, separate call.
    ///
    /// Returns whether the requery actually succeeded -- added for RecalculatePayslipAsync
    /// below (this method's only other caller besides RequestRefresh's own fire-and-forget
    /// `_ = RefreshAsync();` and RemoveEmployeeFromGroupAsync's own reentrant call), which
    /// needs to know whether to show a success message: _busy.RunAsync itself swallows the
    /// exception and reports it via onError rather than rethrowing (see that method's own
    /// doc comment), so without this the caller has no way to tell a clean run apart from
    /// one onError already reported. Every existing caller already just awaits/discards the
    /// task without reading a return value, so this is a non-breaking signature change --
    /// same "Task&lt;bool&gt; discard compiles the same as Task discard" reasoning
    /// PayrollGroupViewModel.RefreshPayrollGroupRowsAsync briefly relied on for its own
    /// now-removed manual Reload button.
    ///
    /// _refreshPending is cleared unconditionally here, not just by RequestRefresh's own
    /// guard/defer branches, so that reentrant call can't leave a stale
    /// _refreshPending=true sitting around for the busy-idle handler above to redundantly
    /// re-run once RemoveEmployeeFromGroupAsync's own outer _busy.RunAsync window closes --
    /// the reselect-and-reload this method is about to do already satisfies whatever
    /// selection/period change most recently set that flag (LoadCoreAsync always reads
    /// SelectedEmployee/PeriodStart/PeriodEnd fresh, not a snapshot), so there's nothing
    /// left to re-run. Every other path into this method already has _refreshPending false
    /// by the time it gets here (RequestRefresh's own branches keep it in sync before ever
    /// reaching the `_ = RefreshAsync()` call at its own bottom), so this is a no-op for
    /// them -- equivalent to, and replacing, an explicit `_refreshPending = false;` that
    /// used to sit at RemoveEmployeeFromGroupAsync's own call site instead, back when that
    /// field was reachable from there directly.</summary>
    internal async Task<bool> RefreshAsync()
    {
        _refreshPending = false;

        // Read at the START, not the end -- same "a version bump racing in mid-load still
        // needs to be noticed on the next revisit rather than silently absorbed as already
        // accounted for" reasoning as PayrollGroupViewModel's own _loadedScheduleVersion
        // (see that field's own doc comment). Only committed to the field below once this
        // whole run has actually succeeded.
        var scheduleVersionAtLoadStart = _dataVersion.ScheduleVersion;
        var holidayVersionAtLoadStart = _dataVersion.HolidayVersion;
        var succeeded = true;

        // adjustmentsOnly: false, explicitly -- this is the employee/period-change path, so
        // it always needs LoadCoreAsync's real attendance recompute (see that parameter's own
        // doc comment), never the adjustments-only reuse the Add/Edit/Delete/waiver-toggle
        // methods below ask for. Spelled out as a lambda rather than passed as a bare method
        // group the way this used to be: LoadCoreAsync now takes a second parameter, and a
        // bare `LoadCoreAsync` here would rely on the compiler filling that parameter's
        // default in via a method-group-to-delegate conversion -- correct, but easy to misread
        // as "same as before" at a glance. Spelled out keeps this call site's own intent
        // (always a full reload) as explicit as every other call site's now has to be.
        await _busy.RunAsync(visibly: true, ct => LoadCoreAsync(ct, adjustmentsOnly: false),
            onError: ex =>
            {
                succeeded = false;
                _statusBarService.ShowError(ex.Message, "Could not load payroll");
            });

        if (succeeded)
        {
            _loadedScheduleVersion = scheduleVersionAtLoadStart;
            _loadedHolidayVersion = holidayVersionAtLoadStart;
        }

        return succeeded;
    }

    /// <summary>The single-employee counterpart to PayrollGroupViewModel.
    /// RecheckOnPageRevisitAsync -- called alongside it (via PayrollViewModel's own
    /// combined forwarding) from PayrollPage.OnNavigatedToAsync, every visit. Closes a gap
    /// that method's own group-table recompute doesn't: refreshing PayrollGroupRows'
    /// NetPay column for everyone in the group says nothing about SelectedEmployee's own
    /// detailed breakdown (Result/AttendanceRows/the adjustment lists below) if that
    /// employee's schedule is what actually changed -- and before this existed, that panel
    /// only ever happened to pick up the change as a side effect of
    /// PayrollGroupViewModel.RestorePayrollGroupSelection's own SelectBatchEmployeeCommand
    /// cascade setting MainViewModel.SelectedEmployee -- which, now that
    /// RecheckOnPageRevisitAsync/RefreshPayrollGroupRowsAsync run properly serialized and
    /// SelectedEmployee ends up reassigned the exact same Employee reference it already
    /// held, no longer even raises PropertyChanged, let alone reaches RequestRefresh. This
    /// is the real mechanism that accidental one was standing in for.
    ///
    /// Same AnyScheduleChangeSince check PayrollGroupViewModel's own version runs, just
    /// against a one-element set (SelectedEmployee's own Pin) instead of the whole group,
    /// and against this class's own _loadedScheduleVersion snapshot rather than
    /// PayrollGroupViewModel's. Guards SelectedEmployee/period/ActivePayrollRunId the same
    /// way RequestRefresh's own early-return does -- RefreshAsync itself trusts its caller
    /// to have already checked these (see RequestRefresh's own doc comment), and this is a
    /// second, independent entry point that can't assume RequestRefresh's own guard ran
    /// first.</summary>
    internal async Task RecheckOnPageRevisitAsync()
    {
        if (SelectedEmployee is not { Pin: { } pin }) return;
        if (_scope.PeriodEnd < _scope.PeriodStart || _scope.ActivePayrollRunId is null) return;

        // A holiday change since this employee's payslip was loaded moves their Holiday Pay
        // regardless of whether their schedule moved -- recompute unconditionally (see
        // _loadedHolidayVersion's own doc comment). Checked before the schedule check below
        // so it isn't short-circuited past when only holidays changed.
        if (_dataVersion.HolidayVersion != _loadedHolidayVersion)
        {
            await RefreshAsync();
            return;
        }

        if (!_dataVersion.AnyScheduleChangeSince([pin], _loadedScheduleVersion)) return;

        await RefreshAsync();
    }

    /// <summary>The actual load/compute work, factored out of RefreshAsync so
    /// AddInlineRowAsync/UpdateInlineDescriptionAsync/UpdateInlineAmountAsync/
    /// DeleteAdjustmentAsync/SetSingleValueAsync below can run their
    /// own write followed by this same reload inside *one* _busy.RunAsync call, rather than
    /// awaiting a second, nested one -- AttendanceBusyState.RunAsync sets IsRunning=false
    /// unconditionally in its own finally block, so a nested call finishing early would
    /// incorrectly clear IsRunning while the outer call (the Add/Edit/Delete) is still in
    /// flight. Assumes the caller has already confirmed SelectedEmployee.Pin is set and
    /// PeriodStart..PeriodEnd is a valid (non-backwards) range -- both already checked by
    /// RequestRefresh before this is reached from there (via RefreshAsync), and trivially true
    /// from those Add/Update/Delete methods, which can't run at all without a selected
    /// employee (see CanEditAdjustments) and never touch the period pickers themselves.
    ///
    /// <paramref name="adjustmentsOnly"/> (Payroll Summary performance fix): false from
    /// RefreshAsync's own employee/period-change path above, where SelectedEmployee/the
    /// period genuinely just changed and attendance has to be recomputed for real via
    /// IPayrollComputationService.ComputeOneAsync. true from every Add/Edit/Delete/
    /// waiver-toggle method below instead, whose own write can only ever change a
    /// PayrollAdjustment row or the undertime-waived flag -- never this employee's actual
    /// attendance (schedule/punches) -- so their reload goes through
    /// ComputeOneAdjustmentsOnlyAsync, which reuses the attendance ComputeOneAsync most
    /// recently computed for this same employee/period instead of re-running
    /// IAttendanceRunner.RunAsync's own company-wide fetch for data that provably hasn't
    /// changed. See that method's own doc comment for the cache it reuses and why a miss
    /// there still falls back to the real thing rather than risking a stale result -- this
    /// parameter only ever decides which of the two IPayrollComputationService entry points
    /// gets called; everything below this line (Result assignment, SyncAdjustmentGroupRows,
    /// AttendanceRows rebuild, EmployeeNetPayComputed) is identical either way.</summary>
    private async Task LoadCoreAsync(CancellationToken cancellationToken, bool adjustmentsOnly)
    {
        var employee = SelectedEmployee!;
        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);

        var (result, summaries) = adjustmentsOnly
            ? await _payrollComputationService.ComputeOneAdjustmentsOnlyAsync(employee, start, end, cancellationToken)
            : await _payrollComputationService.ComputeOneAsync(employee, start, end, cancellationToken);
        Result = result;

        // Monthly-rated only -- a Daily-rated employee's unscheduled days are already
        // just absences, nothing to flag. Deliberately placed here rather than inside
        // PayrollComputationService.ComputeOneAsync itself: that method is also reused
        // by PayrollGroupViewModel/PayrollPrintExportViewModel's own batch loops over every
        // checked employee, and firing a caution toast per employee in a 50-person batch run
        // would just spam and overwrite itself. LoadCoreAsync is the single-employee-in-view
        // path, which matches "the person is watching this employee's payroll."
        if (employee.EmployeeType == EmployeeType.Monthly && result.UnscheduledDayCount > 0)
            _statusBarService.ShowCaution(
                $"{result.UnscheduledDayCount} day(s) this period have no schedule " +
                $"entry for \"{employee.DisplayName}\" -- they won't reduce the " +
                "divisor or count as an absence. Mark them Rest Day if that's what they are.",
                "Unscheduled day(s) in period");

        // Patch GrossPayAdjustmentGroupRows/DeductionAdjustmentGroupRows in place rather than
        // leaving PayrollSummaryView bound straight to Result.GrossPayAdjustmentGroups/
        // DeductionAdjustmentGroups -- those are a brand new IReadOnlyList<PayrollAdjustmentGroup>
        // every single call (PayrollCalculator has no reason to reuse the previous one), so a
        // direct binding would force the ItemsControl to tear down and rebuild every category
        // card -- and every itemized row inside it -- on every single edit, not just the one
        // category/row that actually changed. See SyncAdjustmentGroupRows' own doc comment.
        SyncAdjustmentGroupRows(GrossPayAdjustmentGroupRows, result.GrossPayAdjustmentGroups);
        SyncAdjustmentGroupRows(DeductionAdjustmentGroupRows, result.DeductionAdjustmentGroups);

        AttendanceRows.Clear();
        foreach (var row in AttendanceSummaryRow.BuildRows(summaries))
            AttendanceRows.Add(row);
        OnPropertyChanged(nameof(HasAttendanceRows));
        NotifyEmptyStates();

        // Was an opportunistic direct patch into PayrollViewModel.PayrollGroupRows (this
        // employee's own row, so the grid doesn't show a stale NetPay) -- see
        // EmployeeNetPayComputed's own doc comment for why this is an event now instead,
        // and for why it's fine that nothing is listening yet as of this build-order step.
        EmployeeNetPayComputed?.Invoke(employee.Id, result.NetPay);
    }

    /// <summary>Patches `rows` in place to match `groups`, called once for
    /// GrossPayAdjustmentGroupRows/GrossPayAdjustmentGroups and once for
    /// DeductionAdjustmentGroupRows/DeductionAdjustmentGroups by LoadCoreAsync above.
    ///
    /// Matches rows to groups by Type rather than by position -- rewritten for Pay
    /// Eligibility Flags plan Phase 6, replacing an earlier index-based version that
    /// assumed `groups` always covers the same fixed set of PayrollAdjustmentTypes, in the
    /// same enum-declaration order, on every single call. That held for
    /// DeductionAdjustmentGroups (never gated) but not for GrossPayAdjustmentGroups once
    /// PremiumHoliday's group started being entirely omitted -- not left present at
    /// zero -- for an employee with Employee.QualifiesForPremiumPay == false (that plan's
    /// Phase 4). Under the old index-based version, switching from an eligible employee to
    /// an ineligible one (or back) left `rows[i].Type` pointing at whichever type first
    /// claimed that slot while `rows[i]`'s SingleValueAdjustment/Subtotal/Adjustments got
    /// silently overwritten with a *different* type's figures for every index at or past
    /// the mismatch -- e.g. the card headed "Premium Pay" quietly showing the newly-loaded
    /// employee's Allowance amount instead. See PayrollAdjustmentGroupRow's own class doc
    /// comment for the fuller trace.
    ///
    /// First pass removes any row whose Type no longer appears in `groups` at all (e.g. the
    /// PremiumHoliday row, when the newly-loaded employee is ineligible). Second pass walks
    /// `groups` in order, finding each one's existing row by Type (moving it into position
    /// if `groups`' order shifted -- it doesn't today, since Enum.GetValues order is stable,
    /// but nothing here depends on that) or inserting a new one if this Type has never had a
    /// row before (a brand-new employee, or a type just re-enabled after being absent). A
    /// row whose Type persists across calls keeps its exact existing instance either way, the
    /// same "patch in place, don't rebuild" goal the class doc comment describes -- only a
    /// Type appearing or disappearing costs an actual insert/remove.</summary>
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
            var existingIndex = IndexOfGroupRow(rows, group.Type);

            if (existingIndex < 0)
            {
                rows.Insert(Math.Min(i, rows.Count), new PayrollAdjustmentGroupRow { Type = group.Type });
            }
            else if (existingIndex != i)
            {
                rows.Move(existingIndex, i);
            }

            var row = rows[i];

            // Guarded by content equality rather than assigned unconditionally: Repository.
            // GetForEmployeePeriodAsync queries AsNoTracking (see PayrollAdjustmentRepository),
            // so PayrollCalculator hands back a brand new PayrollAdjustment instance for every
            // row on every single LoadCoreAsync call -- including categories nobody touched.
            // PayrollAdjustment isn't itself observable, and the generated ObservableProperty
            // setter below only skips work on reference equality, so an unconditional
            // assignment here would raise PropertyChanged for SingleValueAdjustment/
            // SingleValueAmount (and re-format that category's own amount box) on every single
            // edit anywhere on the page, not just when this category's own figure actually
            // changed -- the same problem SyncAdjustmentRows' own content check below exists to
            // avoid for itemized rows.
            if (!SingleValueAmountEquals(row.SingleValueAdjustment, group.SingleValueAdjustment))
                row.SingleValueAdjustment = group.SingleValueAdjustment;

            row.Subtotal = group.Subtotal;
            SyncAdjustmentRows(row.Adjustments, group.Adjustments);
        }
    }

    /// <summary>Linear search by Type, the counterpart to IndexOfAdjustment below for
    /// SyncAdjustmentGroupRows -- `rows` is at most eight items (one per
    /// PayrollAdjustmentType), so a plain scan costs nothing worth indexing for.</summary>
    private static int IndexOfGroupRow(ObservableCollection<PayrollAdjustmentGroupRow> rows, PayrollAdjustmentType type)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type == type) return i;
        }

        return -1;
    }

    /// <summary>Whether two SingleValueAdjustment candidates represent the same displayed
    /// figure -- both null, or both non-null with the same Amount. Only Amount is compared
    /// (not Id/Description/EnteredBy/CreatedAt) since Amount is the only field
    /// PayrollAdjustmentGroupRow.SingleValueAmount, and therefore the single-value amount box's
    /// own binding, ever actually surfaces.</summary>
    private static bool SingleValueAmountEquals(PayrollAdjustment? a, PayrollAdjustment? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Amount == b.Amount;
    }

    /// <summary>Patches one PayrollAdjustmentGroupRow's own Adjustments collection in place to
    /// match `source` -- the itemized-row counterpart to SyncAdjustmentGroupRows above, for
    /// Incentive/OtherCharge's genuinely variable-length row lists (SyncAdjustmentGroupRows'
    /// own fixed-set guarantee doesn't hold here: a row can be added or deleted at any time).
    /// Matches by PayrollAdjustment.Id -- always a real database id by the time this runs, since
    /// `source` comes from PayrollResult, itself built from IPayrollAdjustmentRepository.
    /// GetForEmployeePeriodAsync's own freshly-queried rows, never from an unsaved in-memory one.
    /// Removes rows no longer present, then walks `source` in order, inserting/moving/replacing
    /// as needed so `rows` ends up in the same order with the same content -- an unchanged row
    /// keeps its exact existing instance (and therefore its own ItemsControl item container)
    /// rather than every row being replaced just because one sibling changed.</summary>
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
            var existingIndex = IndexOfAdjustment(rows, incoming.Id);

            if (existingIndex < 0)
            {
                rows.Insert(Math.Min(i, rows.Count), incoming);
                continue;
            }

            if (existingIndex != i)
                rows.Move(existingIndex, i);

            // PayrollAdjustment itself isn't observable, so a changed Description/Amount has to
            // land as a brand new instance for the row's own OneWay-bound Description/Amount
            // TextBoxes (see InlineAdjustmentRowTemplate) to actually pick it up -- but an
            // adjustment whose content hasn't changed keeps its existing instance untouched,
            // rather than every row being swapped out just because this method ran.
            if (rows[i].Amount != incoming.Amount ||
                rows[i].Description != incoming.Description ||
                rows[i].EnteredBy != incoming.EnteredBy)
            {
                rows[i] = incoming;
            }
        }
    }

    private static int IndexOfAdjustment(ObservableCollection<PayrollAdjustment> rows, int id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id == id) return i;
        }

        return -1;
    }

    /// <summary>Bound to Incentive/OtherCharge's own "+ Add Row" button in PayrollSummaryView
    /// (see AdjustmentGroupTemplate's inline-itemized body) -- no dialog round trip: this
    /// creates the new row immediately with a blank Description and a 0.00 Amount, and the
    /// person types the real values directly into the row that appears (see
    /// UpdateInlineDescriptionAsync/UpdateInlineAmountAsync below and
    /// PayrollSummaryView.xaml.cs's InlineDescriptionBox_.../InlineAmountBox_... handlers,
    /// which commit each field the same LostFocus/Enter way SingleValueAmountBox does). An
    /// empty, zero-amount row left untouched is harmless clutter rather than bad data -- its
    /// own Delete button (the same DeleteAdjustmentCommand every row uses) removes it same as
    /// any other row.</summary>
    [RelayCommand(CanExecute = nameof(CanEditAdjustments))]
    private async Task AddInlineRowAsync(PayrollAdjustmentType type)
    {
        if (SelectedEmployee is null) return;
        var pin = SelectedEmployee.Pin;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.AddAsync(new PayrollAdjustment
            {
                EmployeeId = pin,
                PeriodStart = DateOnly.FromDateTime(_scope.PeriodStart),
                PeriodEnd = DateOnly.FromDateTime(_scope.PeriodEnd),
                Type = type,
                Amount = 0,
                Description = string.Empty,
                EnteredBy = Environment.UserName,
            }, cancellationToken);

            // Reloads inside this same busy window rather than a second, separate
            // RefreshAsync call -- see LoadCoreAsync's own doc comment for why that has to
            // be one call, not two nested ones. adjustmentsOnly: true -- this write only
            // ever adds a PayrollAdjustment row, never changes attendance, so this reuses
            // the last computed attendance instead of re-running it (see LoadCoreAsync's
            // own doc comment on that parameter).
            await LoadCoreAsync(cancellationToken, adjustmentsOnly: true);
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Bound to an inline-itemized row's own Description box (see
    /// PayrollSummaryView.xaml.cs's InlineDescriptionBox_LostFocus/KeyDown) the same way
    /// SetSingleValueAsync is bound to the single-value amount box -- TextBox has no Command/
    /// CommandParameter of its own, so this is called directly rather than through an
    /// ICommand. Keeps the row's existing Amount/EnteredBy as-is, only Description changes;
    /// a no-op if the trimmed text matches what's already there (e.g. LostFocus firing again
    /// after Enter already committed it), same reasoning as SetSingleValueAsync's own
    /// unchanged-value check.</summary>
    public async Task UpdateInlineDescriptionAsync(PayrollAdjustment original, string rawDescription)
    {
        if (_busy.IsRunning) return;

        var description = rawDescription.Trim();
        if (description == original.Description) return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.UpdateAsync(new PayrollAdjustment
            {
                Id = original.Id,
                EmployeeId = original.EmployeeId,
                PeriodStart = original.PeriodStart,
                PeriodEnd = original.PeriodEnd,
                Type = original.Type,
                Amount = original.Amount,
                Description = description,
                EnteredBy = original.EnteredBy,
            }, cancellationToken);

            // adjustmentsOnly: true -- a Description edit never touches attendance, see
            // LoadCoreAsync's own doc comment on this parameter.
            await LoadCoreAsync(cancellationToken, adjustmentsOnly: true);
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Bound to an inline-itemized row's own Amount box (see
    /// PayrollSummaryView.xaml.cs's InlineAmountBox_LostFocus/KeyDown) -- the Description
    /// counterpart to UpdateInlineDescriptionAsync above, same "called directly, not through
    /// an ICommand" reasoning. rawAmountText that doesn't parse to a valid non-negative amount
    /// is a silent no-op, same as SetSingleValueAsync's own parse check, except zero is
    /// allowed here (unlike SetSingleValueAsync's amount &gt; 0) since a freshly added row
    /// starts at 0.00 before its real amount is typed in, and re-committing that starting
    /// value shouldn't be rejected as invalid.</summary>
    public async Task UpdateInlineAmountAsync(PayrollAdjustment original, string rawAmountText)
    {
        if (_busy.IsRunning) return;
        if (!decimal.TryParse(rawAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            return;
        if (amount == original.Amount) return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.UpdateAsync(new PayrollAdjustment
            {
                Id = original.Id,
                EmployeeId = original.EmployeeId,
                PeriodStart = original.PeriodStart,
                PeriodEnd = original.PeriodEnd,
                Type = original.Type,
                Amount = amount,
                Description = original.Description,
                EnteredBy = original.EnteredBy,
            }, cancellationToken);

            // adjustmentsOnly: true -- an Amount edit never touches attendance, see
            // LoadCoreAsync's own doc comment on this parameter.
            await LoadCoreAsync(cancellationToken, adjustmentsOnly: true);
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Bound to each itemized row's own Delete button (InlineAdjustmentRowTemplate
    /// and its Deductions-column copy) -- deletes a
    /// PayrollAdjustment row by Id outright, which is the correct behavior for those since an
    /// itemized category's Subtotal is just however many rows happen to exist; removing one
    /// is supposed to remove it. Not used by a single-value category's own amount box --
    /// that box's Edit button (see PayrollSummaryView.xaml.cs's
    /// EditSingleValueButton_Click) only focuses and selects the box's existing text so a
    /// person can type straight over it; the actual commit still goes through
    /// SetSingleValueAsync below the same as it always did, deleting a row was never how a
    /// single-value figure got changed. Same Yes/No MessageBox
    /// confirmation as ManualEntryEditorViewModel.DeleteManualEntryAsync -- the status bar
    /// can't block for an answer, so a destructive action still goes through MessageBox (see
    /// StatusBarNotificationExtensions' own doc comment).</summary>
    [RelayCommand(CanExecute = nameof(CanEditAdjustments))]
    private async Task DeleteAdjustmentAsync(PayrollAdjustment adjustment)
    {
        var confirm = MessageBox.Show(
            $"Delete this {adjustment.Type.ToText()} line (\"{adjustment.Description}\", " +
            $"{adjustment.Amount:N2})?",
            "Delete payroll adjustment", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _adjustmentRepository.DeleteAsync(adjustment.Id, cancellationToken);

            // adjustmentsOnly: true -- deleting a row never touches attendance, see
            // LoadCoreAsync's own doc comment on this parameter.
            await LoadCoreAsync(cancellationToken, adjustmentsOnly: true);

            _statusBarService.ShowSuccess("Payroll adjustment deleted.");
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Bound to a single-value category's own inline amount box in
    /// PayrollSummaryView (see AdjustmentGroupTemplate and
    /// PayrollSummaryView.xaml.cs's SingleValueAmountBox_LostFocus/KeyDown), which call this
    /// directly rather than through an ICommand -- TextBox has no Command/CommandParameter of
    /// its own the way ButtonBase does, so there's nothing for a RelayCommand to bind to
    /// here. Not gated by [RelayCommand(CanExecute = ...)] for that same reason; the box's
    /// own IsEnabled (bound to CanEditAdjustmentsNow below) already keeps it from being typed
    /// into in the first place, and the guard on the first line below covers the case where a
    /// commit was already in flight when a second one lands.
    ///
    /// Unlike AddInlineRowAsync's own row, there's no dialog and no per-row Description to
    /// type -- Description is fixed to the type's own label and EnteredBy defaults to the
    /// current Windows user (see PayrollAdjustment.Description/EnteredBy's own doc comments
    /// for why a plain label is fine here), so typing a number into the box is the entire
    /// interaction. Creates this employee/period's one row for the type if none exists yet,
    /// or updates its Amount in place if one already does -- never a second row, the same
    /// invariant PayrollAdjustmentRepository.AddAsync/UpdateAsync's own guards enforce
    /// server-side. rawAmountText that doesn't parse, or parses negative, is a silent no-op --
    /// PayrollSummaryView's binding already reformats the box back to Result's own value on
    /// the next refresh, so there's nothing further to correct here for a bad keystroke.
    /// Blank/whitespace text is different for type.BlankAmountMeansZero() (Allowance/Premium
    /// Pay/Cash Advance -- see that property's own doc comment): rather than falling into the
    /// no-op branch above, it's treated exactly like an explicit "0", so clearing the box and
    /// tabbing away creates (or updates) that row to Amount = 0 instead of leaving no row at
    /// all -- keeps the box, PayrollExcelExporter's column, and the printed payslip line all
    /// reading 0.00 the same way once a person has looked at the field, rather than the
    /// payslip quietly omitting a category the box itself shows as blank. False for
    /// SSS/PhilHealth/Pag-IBIG, where blank still falls through to the ordinary no-op below --
    /// see BlankAmountMeansZero's own doc comment for why. An amount of exactly zero IS
    /// accepted and persisted either way (unlike the old amount &gt; 0 check this used to
    /// have) -- typing 0 is how a person explicitly sets SSS/PhilHealth/Pag-IBIG
    /// to "no contribution this period" rather than leaving it at whatever was last
    /// auto-seeded. The Update branch below stamps EnteredBy to the current Windows user
    /// (rather than keeping existing.EnteredBy) precisely so this distinction is visible
    /// later: for SSS/PhilHealth/Pag-IBIG specifically,
    /// IPayrollComputationService.SeedOrReseedContributionsAsync only corrects a row's Amount
    /// back toward the employee default/period rule while EnteredBy still reads its own
    /// SeededEnteredBy sentinel -- the moment a person types a number in
    /// here (this amount included, even if it happens to match what was already showing),
    /// that row is hand-owned and left alone on every later load -- typing an explicit 0
    /// and committing it is how a person deliberately zeroes one of these out now that
    /// there's no separate Clear action (the Edit button beside the box just selects its
    /// text; the commit is still this method). An amount that matches the existing row's
    /// Amount unchanged is still a silent no-op regardless (e.g. Enter's own commit already
    /// ran, then LostFocus fires too) -- that shouldn't cost a round trip either.</summary>
    public async Task SetSingleValueAsync(PayrollAdjustmentType type, string rawAmountText)
    {
        if (_busy.IsRunning) return;
        if (SelectedEmployee is null) return;
        var pin = SelectedEmployee.Pin;

        decimal amount;
        if (string.IsNullOrWhiteSpace(rawAmountText) && type.BlankAmountMeansZero())
        {
            amount = 0m;
        }
        else if (!decimal.TryParse(rawAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount < 0)
        {
            return;
        }

        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);
        var existing = Result?.GrossPayAdjustmentGroups
            .Concat(Result.DeductionAdjustmentGroups)
            .FirstOrDefault(g => g.Type == type)?.SingleValueAdjustment;

        if (existing is not null && existing.Amount == amount) return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            if (existing is null)
            {
                await _adjustmentRepository.AddAsync(new PayrollAdjustment
                {
                    EmployeeId = pin,
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
                await _adjustmentRepository.UpdateAsync(new PayrollAdjustment
                {
                    Id = existing.Id,
                    EmployeeId = existing.EmployeeId,
                    PeriodStart = existing.PeriodStart,
                    PeriodEnd = existing.PeriodEnd,
                    Type = type,
                    Amount = amount,
                    Description = existing.Description,
                    EnteredBy = Environment.UserName,
                }, cancellationToken);
            }

            // Reloads inside this same busy window rather than a second, separate
            // RefreshAsync call -- see LoadCoreAsync's own doc comment for why that has to
            // be one call, not two nested ones. adjustmentsOnly: true -- a single-value
            // Add/Update never touches attendance, see LoadCoreAsync's own doc comment on
            // this parameter.
            await LoadCoreAsync(cancellationToken, adjustmentsOnly: true);

            _statusBarService.ShowSuccess($"Updated {type.ToText()}.");
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Called from the Undertime row's own Exclude/Include toggle button in
    /// PayrollSummaryView (see PayrollSummaryView.xaml.cs's
    /// UndertimeWaivedToggle_Click), which calls this directly rather than
    /// through an ICommand -- same "no CanExecute needed, the control's own
    /// IsEnabled already covers it" reasoning as SetSingleValueAsync above, this
    /// button's IsEnabled is bound to CanEditAdjustmentsNow the same way that one's
    /// amount box is.
    ///
    /// Unlike every Add/Edit/Delete method on this class, there's no
    /// PayrollAdjustment row to write -- IPayrollUndertimeWaiverRepository.
    /// SetWaivedAsync is a plain on/off switch (see that interface's own doc
    /// comment), so this is a thin pass-through plus the same reload-inside-one-
    /// busy-window pattern every other write here uses (see LoadCoreAsync's own
    /// doc comment for why that has to be one call, not two nested ones). No
    /// "already matches, skip the round trip" short-circuit the way
    /// SetSingleValueAsync has for an unchanged amount -- a checkbox can only
    /// reach here by actually flipping state (Checked never fires again while
    /// already checked), so there's nothing to compare against first.</summary>
    public async Task SetUndertimeWaivedAsync(bool waived)
    {
        if (_busy.IsRunning) return;
        if (SelectedEmployee is null) return;
        var pin = SelectedEmployee.Pin;

        var start = DateOnly.FromDateTime(_scope.PeriodStart);
        var end = DateOnly.FromDateTime(_scope.PeriodEnd);

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _undertimeWaiverRepository.SetWaivedAsync(pin, start, end, waived, cancellationToken);

            // Same "reload inside this same busy window" reasoning as every other
            // write above. adjustmentsOnly: true -- the waiver flag isn't attendance
            // itself, see LoadCoreAsync's own doc comment on this parameter.
            await LoadCoreAsync(cancellationToken, adjustmentsOnly: true);

            _statusBarService.ShowSuccess(waived ? "Undertime disregarded." : "Undertime restored.");
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Shared by AddInlineRowCommand/DeleteAdjustmentCommand -- parameterless
    /// (rather than typed to PayrollAdjustmentType/PayrollAdjustment) the same way
    /// MainViewModel.CanSetScheduleForSelection backs a command that takes a ScheduleType?
    /// parameter: neither of these two checks actually depends on which type/row was
    /// clicked, only on whether an employee is selected and nothing else is already
    /// running.</summary>
    private bool CanEditAdjustments() => !_busy.IsRunning && SelectedEmployee is not null;

    /// <summary>Plain public projection of CanEditAdjustments() above, for
    /// PayrollSummaryView's single-value amount box and inline-itemized Description/Amount
    /// boxes to bind their own IsEnabled straight to -- TextBox has no CanExecute the way a
    /// Command-bound Button does, so this is what stands in for one. Kept in sync at the same
    /// two spots CanEditAdjustments' own NotifyAdjustmentCommands()/OnMainViewModelPropertyChanged
    /// calls are, since exactly the same two conditions (nothing else running, an employee with
    /// a Pin selected) decide both.</summary>
    public bool CanEditAdjustmentsNow => CanEditAdjustments();

    /// <summary>The five command/property notifications gated on _busy.IsRunning (or on
    /// SelectedEmployee, which affects the exact same set -- see CanEditAdjustments) --
    /// factored out so the constructor's _busy.PropertyChanged handler and
    /// OnMainViewModelPropertyChanged above call the exact same list rather than two copies
    /// that could drift apart. Named distinctly from PayrollViewModel's own (broader)
    /// NotifyToolbarCommands, which at the time this was written (build-order step 2) still
    /// covered every command PayrollViewModel itself owned directly. That method has since
    /// been retired entirely -- Group, Run, and PrintExport each gained their own
    /// NotifyGroupCommands()/NotifyRunCommands()/NotifyPrintExportCommands() as they were
    /// extracted in steps 3-5, leaving nothing on the facade for NotifyToolbarCommands to
    /// cover (see the facade's own constructor doc comment). This method's own job started
    /// as just the four commands/properties that moved here as of build-order step 2;
    /// RecalculatePayslipCommand joined later, once that command existed to gate, the same
    /// way ReloadPayrollGroupCommand once joined PayrollGroupViewModel's own
    /// NotifyGroupCommands() (see that method's own doc comment -- Reload has since been
    /// removed again, but the "commands get added here once they exist" pattern it
    /// established is what this follows).</summary>
    private void NotifyAdjustmentCommands()
    {
        AddInlineRowCommand.NotifyCanExecuteChanged();
        DeleteAdjustmentCommand.NotifyCanExecuteChanged();
        PrintCurrentPayslipCommand.NotifyCanExecuteChanged();
        RecalculatePayslipCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditAdjustmentsNow));
    }

    /// <summary>Build-order step 4 (Print_Feature.md): "Print Current Payslip" on
    /// PayrollPage, for whoever's selected right now. Uses <see cref="Result"/> --
    /// the PayrollResult already loaded for SelectedEmployee/PeriodStart..PeriodEnd
    /// -- rather than recomputing it; there's no reason to re-run
    /// IAttendanceRunner/IPayrollAdjustmentRepository a second time for data that's
    /// already sitting right here and current. Goes straight through
    /// PayslipPreviewDialog (see that class's own doc comment for why: it's what
    /// build-order step 4 itself validates real print output against before the
    /// batch UI gets built on top in step 5) rather than offering any kind of
    /// skip-the-preview shortcut of its own -- a single slip is cheap enough to
    /// render that there's no real cost to always showing it.</summary>
    [RelayCommand(CanExecute = nameof(CanPrintCurrentPayslip))]
    private void PrintCurrentPayslip()
    {
        if (Result is null) return;

        var dialog = new PayslipPreviewDialog([Result], _companyName) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    /// <summary>Same !_busy.IsRunning guard as CanEditAdjustments -- opening the
    /// preview dialog itself does no database work (Result is already loaded), but
    /// this still shouldn't be clickable mid-refresh, the same reasoning
    /// CanEditAdjustments' own doc comment gives for its three commands. Doesn't
    /// also require SelectedEmployee is not null the way CanEditAdjustments does --
    /// Result being non-null already implies that (RequestRefresh clears Result to
    /// null whenever SelectedEmployee isn't valid -- see that method), so checking
    /// both would just be redundant.</summary>
    private bool CanPrintCurrentPayslip() => !_busy.IsRunning && Result is not null;

    /// <summary>Manual counterpart to RequestRefresh() -- bound to PayrollSummaryView's
    /// icon-only Refresh button at the right edge of the employee-name header, for
    /// recomputing SelectedEmployee's own payslip on demand (e.g. after attendance for
    /// the period changed, or an adjustment was saved elsewhere) without needing to nudge
    /// a period picker or reselect the employee to trigger a reload indirectly. Runs the
    /// exact same RefreshAsync() compute RequestRefresh's own automatic path uses --
    /// there's no cheaper "what actually changed" signal to react to here, the same
    /// reasoning PayrollGroupViewModel's own (now-removed) manual Reload button used to
    /// give for the whole group; this replaces that button's one real use case (a one-off
    /// recalculation nothing automatic would otherwise trigger) with a per-employee
    /// equivalent that lives right next to the payslip it recalculates instead of off in
    /// the group grid's own header.
    ///
    /// Reports success explicitly, unlike the automatic path (RequestRefresh, silent so an
    /// employee-selection or period-field edit doesn't spam the status bar) -- same "silent
    /// for a background reaction, a status message for a direct click" split
    /// PayrollRunViewModel's own NewPayrollRun/LoadPayrollGroupAsync already follow. Calls
    /// RefreshAsync() directly rather than through RequestRefresh()'s own guard/defer
    /// wrapper -- CanRecalculatePayslip below already keeps this command disabled unless a
    /// payslip is already loaded (which itself implies SelectedEmployee/the period/
    /// ActivePayrollRunId all passed RequestRefresh's own checks the last time it ran) and
    /// while _busy.IsRunning, so there's nothing left here for that wrapper to guard
    /// against, and awaiting the real work directly is what lets this method see the
    /// returned success flag at all.</summary>
    [RelayCommand(CanExecute = nameof(CanRecalculatePayslip))]
    private async Task RecalculatePayslipAsync()
    {
        if (await RefreshAsync())
            _statusBarService.ShowSuccess("Payslip recalculated.");
    }

    /// <summary>Same !_busy.IsRunning-plus-Result-not-null gate as CanPrintCurrentPayslip,
    /// for the same reason: there's nothing to recalculate before a payslip has actually
    /// loaded once, and this shouldn't be clickable while a refresh (or an Add/Edit/Delete
    /// round trip) is already using _busy.</summary>
    private bool CanRecalculatePayslip() => !_busy.IsRunning && Result is not null;
}
