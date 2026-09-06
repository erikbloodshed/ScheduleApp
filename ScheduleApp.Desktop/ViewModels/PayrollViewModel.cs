using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Payroll;
using ScheduleApp.Payroll;
using ScheduleApp.Payroll.Pdf;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>Drives both right-side panels on the Payroll tab (PayrollSummaryView and
/// EmployeeAttendancePanel -- see PayrollPage.xaml) for whichever employee is selected in
/// the shared MainViewModel.Departments tree. A separate Scoped instance from MainViewModel
/// (see App.xaml.cs's registration) since the tree/selection state itself already belongs on
/// MainViewModel -- shared with the Schedule/Employees tabs, see PayrollPage's own doc
/// comment for why that stays there rather than being duplicated here -- but the payroll
/// period, the itemized breakdown, and the adjustment list are specific to this tab and have
/// no reason to live on MainViewModel.
///
/// Now that PayrollCalculator/PayrollAdjustment/IPayrollAdjustmentRepository all exist
/// (build-order steps 2-3), this class owns the itemized Gross Pay/Deductions/Net Pay
/// breakdown (<see cref="Result"/>) and the read-only attendance grid (<see
/// cref="AttendanceRows"/>) EmployeeAttendancePanel shows underneath it -- both recomputed
/// live, together, via RefreshAsync, every time the selected employee or period changes, or
/// an adjustment is added/edited/deleted below. There's deliberately no separate "Generate"
/// action -- see the Payroll Feature plan's Assumption 6: no "finalize and lock" run record
/// in v1, Payroll just re-asks IPayrollComputationService each time (which itself re-asks
/// IAttendanceRunner and IPayrollAdjustmentRepository and hands the result to
/// PayrollCalculator -- see that service's own doc comment, and build-order step 4 for why
/// it's a separate service rather than a private method here), same spirit as the Attendance
/// tab's own Summary tab recomputing on every relevant change rather than waiting on an
/// explicit button.
/// </summary>
public partial class PayrollViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IPayrollComputationService _payrollComputationService;
    private readonly IStatusBarService _statusBarService;

    // No _adjustmentRepository/_undertimeWaiverRepository fields (build-order step 6's
    // facade cleanup) -- unlike _rosterProvider/_payrollRunRepository/
    // _payrollComputationService/_statusBarService just below and above, which each get
    // handed to two or more children, these two are only ever passed to Summary's own
    // constructor, once, and read by nothing else in this class. Storing them as fields
    // between construction and that single pass-through call bought nothing a bare
    // constructor parameter doesn't already give for free, so the constructor below uses
    // the adjustmentRepository/undertimeWaiverRepository parameters directly instead.

    /// <summary>Passed straight through to PayrollGroupViewModel's, PayrollRunViewModel's,
    /// and, as of build-order step 5, PayrollPrintExportViewModel's own constructors --
    /// replaces the raw IScheduleRepository this class used to hand off directly -- see
    /// ActiveRosterProvider's own doc comment for why Group's own full-roster reads
    /// (RefreshFullRosterAsync/AddEmployeesToGroupAsync), Run's own PayrollWizardDialog/
    /// LoadPayrollGroupDialog trees, and PrintExport's own PayslipScopeDialog tree all now go
    /// through the shared, RosterVersion-gated cache instead of each paying for its own
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync round trip.
    /// This class has no direct use for it anymore -- PrintPayslipsAsync/
    /// ExportPayrollReportAsync, its own last direct callers, moved out to PrintExport as
    /// part of step 5. Kept as a field rather than following _adjustmentRepository/
    /// _undertimeWaiverRepository's own build-order step 6 removal just above -- three
    /// separate pass-through call sites (Group/Run/PrintExport) is enough repetition that a
    /// field earns its keep, unlike those two's single call site. Constructor-injected here
    /// (DI-registered -- see App.xaml.cs) rather than reached for via _mainViewModel, since
    /// none of the Department/Employee tree itself lives on this class (that stays on
    /// MainViewModel -- see this class's own doc comment).</summary>
    private readonly ActiveRosterProvider _rosterProvider;

    /// <summary>Only used to hand PayrollWizardDialog its own "Save Payroll Group" write
    /// (build-order step 8.3 -- see PayrollRunViewModel.NewPayrollRun and
    /// PayrollWizardViewModel.SavePayrollGroupCommand) -- this class has no direct reason
    /// to read/write PayrollRuns itself, same "constructor-injected just to hand off to a
    /// dialog" convention _rosterProvider above already follows for
    /// PayslipScopeDialog/PayrollWizardDialog's own employee tree.</summary>
    private readonly IPayrollRunRepository _payrollRunRepository;

    /// <summary>Constructor-injected (see App.xaml.cs's registration), NOT its own
    /// separate instance the way this used to be -- the same one instance MainViewModel
    /// also takes, because Summary's own RequestRefresh/RefreshAsync (build-order step 2
    /// moved these off this class -- see PayrollSummaryViewModel's own doc comment) and
    /// MainViewModel's own RefreshScheduleForSelectedEmployeeAsync both fire off the exact
    /// same MainViewModel.SelectedEmployee change (PayrollPage shares that class's tree
    /// rather than having one of its own), and both ultimately read/write through the
    /// one shared, app-lifetime-scoped ScheduleDbContext -- see App.xaml.cs's
    /// registration comment for the "second operation started on this context" bug two
    /// separate instances here used to let happen every time an employee was selected
    /// on this tab. Now also shared with AttendanceViewModel's own _busy, for the same
    /// underlying reason: that page's tree/selection is its own (ReportScopeViewModel), so
    /// nothing there fires alongside a Payroll-tab employee/period change, but its
    /// Import/DeviceFetch/Generate Reports work isn't cancelled by navigating away (see
    /// AttendanceViewModel's own _busy field doc comment) and reads/writes the exact same
    /// shared, app-lifetime-scoped ScheduleDbContext -- so it needed the same one gate
    /// too, not because it fires alongside this tab's own changes, but because "alongside"
    /// isn't required for two operations to collide on one DbContext instance.
    /// Sharing does mean CanEditAdjustments/IsBusy (both Summary's own now) can reflect
    /// DB work MainViewModel or AttendanceViewModel kicked off (e.g. a Schedule-tab
    /// employee change, or an Attendance-tab import, happening in the background) as well
    /// as this tab's own -- that's the correct trade-off, not a side effect to work
    /// around: the whole point is that nothing anywhere in the app should be allowed to
    /// touch the DbContext while anything else already is.</summary>
    private readonly AttendanceBusyState _busy;

    /// <summary>Owns PeriodStart/PeriodEnd/BatchScopeEmployees/ActivePayrollRunId's actual
    /// storage as of build-order step 1 of the Payroll refactor plan (see
    /// PayrollScopeState's own doc comment) -- PayrollViewModel keeps the four properties
    /// themselves (below) as thin forwarders so nothing outside this class, XAML included,
    /// needs to change. Constructed here rather than DI-injected, since nothing outside this
    /// class's own children reads or writes it. This same instance is also passed into each
    /// child's own constructor -- Summary (build-order step 2), Group (step 3), Run (step 4),
    /// and PrintExport (step 5, the plan's last extraction) -- see any of their own _scope
    /// fields' doc comments.</summary>
    private readonly PayrollScopeState _scope;

    // Formerly held a 150ms "minimum visible busy duration" timer
    // (MinVisibleBusyDuration/_busyStartedAt/_skipToolbarReenableDelay/
    // _toolbarReenableTimer) that debounced NotifyToolbarCommands()'s re-enabling call
    // after _busy.IsRunning dropped back to false, with a _skipToolbarReenableDelay flag
    // set by RequestRefresh() to bypass the hold for a plain employee-selection reload
    // specifically. That bypass relied on RequestRefresh() being the thing that actually
    // started the busy cycle it was trying to tag -- but PayrollGroupGrid's selection
    // sets MainViewModel.SelectedEmployee, the same property the Schedule tree uses, and
    // MainViewModel.OnSelectedEmployeeChanged (a partial method invoked synchronously by
    // the generated property setter -- see its own doc comment) kicks off Schedule's own
    // reload via that same shared _busy before this class's OnMainViewModelPropertyChanged
    // ever runs (that one only fires once the setter goes on to raise PropertyChanged to
    // external subscribers). So most PayrollGroupGrid clicks had Schedule's reload, not
    // RequestRefresh() here, as the busy cycle's true start -- _skipToolbarReenableDelay
    // never got set on that pass, the 150ms hold fired anyway, and the toolbar/payslip
    // buttons visibly disabled and re-enabled on their own delayed tick regardless of
    // which class's reload the person actually triggered. Tagging "this specific busy
    // cycle was just a selection reload" is fragile once two ViewModels share one busy
    // flag and can re-enter each other's handlers this way.
    //
    // Replaced with the same "no artificial hold, re-enable the instant IsRunning goes
    // false" behavior MainViewModel already gives the Schedule page's own Set/Set Leave/
    // Clear Schedule buttons (see its _busy.PropertyChanged handler's
    // NotifyCanExecuteChanged calls) -- the one behavior of the two that was confirmed to
    // never flicker, since it never tried to distinguish whose reload was whose in the
    // first place.

    /// <summary>Owns concern 1 of the Payroll refactor plan (the single-employee
    /// breakdown) as of build-order step 2 -- see that class's own doc comment. Every
    /// member it exposes is forwarded back out under the same name below (the "Forwarded
    /// members" region), since PayrollSummaryView.xaml/PayrollSummaryView.xaml.cs/
    /// EmployeeAttendancePanel.xaml are all typed/bound directly to this class (the
    /// facade), never to Summary itself -- see this class's own doc comment for why that
    /// stays true across every build-order step of this refactor.</summary>
    public PayrollSummaryViewModel Summary { get; }

    /// <summary>Owns concern 2 of the Payroll refactor plan (the Payroll Group table and
    /// the "Not in group" roster) as of build-order step 3 -- see that class's own doc
    /// comment. Every member it exposes is forwarded back out under the same name below
    /// (the "Forwarded members (PayrollGroupViewModel)" region), since PayrollPage.xaml/
    /// PayrollPage.xaml.cs are typed/bound directly to this class (the facade), never to
    /// Group itself -- same reason Summary's own members are forwarded, see this class's
    /// own doc comment.</summary>
    public PayrollGroupViewModel Group { get; }

    /// <summary>Owns concern 3 of the Payroll refactor plan (the Payroll Run lifecycle --
    /// starting a fresh run via the wizard, or reopening a saved one) as of build-order step
    /// 4 -- see that class's own doc comment. Its only two members (NewPayrollRunCommand/
    /// LoadPayrollGroupCommand) are forwarded back out under the same name below (the
    /// "Forwarded members (PayrollRunViewModel)" region), since PayrollPage.xaml's toolbar
    /// buttons are typed/bound directly to this class (the facade), never to Run itself --
    /// same reason Summary's/Group's own members are forwarded, see this class's own doc
    /// comment.</summary>
    public PayrollRunViewModel Run { get; }

    /// <summary>Owns concern 4 of the Payroll refactor plan (batch print/export -- "Print
    /// Payslips…" and "Export Payroll Report…") as of build-order step 5, the plan's last
    /// extraction -- see that class's own doc comment. Its only two members
    /// (PrintPayslipsCommand/ExportPayrollReportCommand) are forwarded back out under the
    /// same name below (the "Forwarded members (PayrollPrintExportViewModel)" region), since
    /// PayrollPage.xaml's toolbar buttons are typed/bound directly to this class (the
    /// facade), never to PrintExport itself -- same reason Summary's/Group's/Run's own
    /// members are forwarded, see this class's own doc comment.</summary>
    public PayrollPrintExportViewModel PrintExport { get; }

    public PayrollViewModel(
        MainViewModel mainViewModel,
        IPayrollComputationService payrollComputationService,
        IPayrollAdjustmentRepository adjustmentRepository,
        IPayrollUndertimeWaiverRepository undertimeWaiverRepository,
        ActiveRosterProvider rosterProvider,
        IPayrollRunRepository payrollRunRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        PayrollSettings payrollSettings)
    {
        _mainViewModel = mainViewModel;
        _payrollComputationService = payrollComputationService;
        _rosterProvider = rosterProvider;
        _payrollRunRepository = payrollRunRepository;
        _statusBarService = statusBarService;

        // Resolved once, here, rather than passing PayrollSettings itself down to
        // Summary/PrintExport -- neither needs anything else off it, and this mirrors
        // SettingsDialog's own "blank means use the built-in default" fallback (see
        // that class's own _originalCompanyName) so there's exactly one place in the
        // whole app that knows PayslipLineBuilder.DefaultCompanyName is the fallback
        // text, not two. Settings changes only take effect on restart (see
        // MainWindow.SettingsButton_Click's own "takes effect the next time an app
        // reads it at startup" message), so re-resolving this on every print/preview
        // call would never see a different value anyway -- once per PayrollViewModel
        // construction (itself Scoped -- see App.xaml.cs's registration) is enough.
        var companyName = string.IsNullOrWhiteSpace(payrollSettings.CompanyName)
            ? PayslipLineBuilder.DefaultCompanyName
            : payrollSettings.CompanyName;

        // adjustmentRepository/undertimeWaiverRepository aren't assigned to fields here --
        // see the (now-absent) _adjustmentRepository/_undertimeWaiverRepository fields' own
        // build-order step 6 removal note just above the _rosterProvider field's doc
        // comment. Both parameters stay in scope for the rest of this constructor and are
        // used directly, once, in the Summary construction call below.

        // Shared with MainViewModel (same instance -- see App.xaml.cs's registration
        // comment and this field's own doc comment above for why).
        _busy = busy;

        // No _busy.PropertyChanged subscription of this class's own anymore -- its only job
        // (NotifyToolbarCommands, trimmed down over build-order steps 2-4 as Summary/Group/
        // Run each took their own commands off its list) lost its last two entries
        // (PrintPayslipsCommand/ExportPayrollReportCommand) to PrintExport's own subscription
        // as of build-order step 5, the plan's last extraction, so there's nothing left on
        // this class for the method or the subscription to cover. Every command this facade
        // forwards is now notified by whichever child actually owns it -- see any of
        // Summary's/Group's/Run's/PrintExport's own NotifyXCommands() methods.

        // Same current-half-month/second-half default AttendanceViewModel's constructor
        // uses for its own Period fields -- payroll periods follow the same semi-monthly
        // convention, so there's no reason for this tab to default differently. Deliberately
        // never routed through ViewStateStore at all (unlike Attendance's own Period, which
        // is -- see ReportViewModel's own initialPeriodStart/initialPeriodEnd parameters):
        // ViewStateStore is in-memory/session-only and never survives a restart (see its own
        // doc comment), so the two approaches produce the identical on-launch result anyway
        // -- always DateTime.Today's own cutoff, never a stale one -- but skipping the
        // indirection here means this can't regress if ViewStateStore's own scope ever
        // changes again. Passed into PayrollScopeState's constructor rather than assigned
        // via the PeriodStart/PeriodEnd properties below -- see that constructor's own doc
        // comment for why this needs to stay a silent, non-notifying default the same way
        // it was before those two fields moved into PayrollScopeState.
        DateTime today = DateTime.Today;
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        (int startDay, int endDay) = today.Day < 16 ? (1, 15) : (16, daysInMonth);
        _scope = new PayrollScopeState(
            new DateTime(today.Year, today.Month, startDay),
            new DateTime(today.Year, today.Month, endDay));

        // Replaces the four CommunityToolkit.Mvvm-generated OnPeriodStartChanged/
        // OnPeriodEndChanged/OnBatchScopeEmployeesChanged/OnActivePayrollRunIdChanged
        // partial-method hooks that used to fire automatically whenever those properties
        // lived directly on this class -- now that their storage has moved to
        // PayrollScopeState (see that field's own doc comment), this subscription's whole
        // remaining job is the blanket OnPropertyChanged(e.PropertyName) forward, the same
        // "forward under this class's own name" convention AttendanceViewModel's own
        // child-forwarding subscriptions use (see that class's constructor) -- safe here
        // because PayrollScopeState's four property names exactly match the four
        // forwarding properties below. Needed so a XAML binding on (say)
        // PayrollViewModel.PeriodStart still refreshes when _scope.PeriodStart changes,
        // e.g. via that same forwarding property's own setter.
        //
        // This class no longer reacts to any of the four itself as of build-order step 3
        // of the Payroll refactor plan -- OnPeriodStartChanged/OnPeriodEndChanged/
        // OnBatchScopeEmployeesChanged/OnActivePayrollRunIdChanged (the Group reactions)
        // moved to PayrollGroupViewModel's own, independent _scope.PropertyChanged
        // subscription (constructed just below), alongside PayrollSummaryViewModel's own
        // (build-order step 2). Nothing is left here to react with; the forward is all
        // that remains.
        _scope.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        // Build-order step 2 of the Payroll refactor plan -- see PayrollSummaryViewModel's
        // own doc comment for the concern it owns (Result, the adjustment CRUD methods,
        // AttendanceRows, Print Current Payslip) and why it's safe to construct here, after
        // _scope but otherwise wherever this class's own RequestRefresh() call used to sit:
        // its own constructor ends with the exact same "pick up whatever's already
        // selected" RequestRefresh() call that used to sit here directly, and its own
        // _mainViewModel.PropertyChanged/_scope.PropertyChanged/_busy.PropertyChanged
        // subscriptions replace what this constructor used to wire up for that concern
        // (see this class's own trimmed _busy.PropertyChanged/_scope.PropertyChanged
        // handlers above).
        Summary = new PayrollSummaryViewModel(
            _mainViewModel, _payrollComputationService, adjustmentRepository,
            undertimeWaiverRepository, _statusBarService, _busy, _scope, dataVersion, companyName);

        // Relays Summary's own PropertyChanged onto this class under the same property
        // name, so a XAML binding on (say) PayrollViewModel.Result -- which reads through
        // the forwarding property below -- still refreshes when Summary.Result changes.
        // Same blanket-relay convention AttendanceViewModel's own constructor uses for its
        // seven child ViewModels (see that class's constructor) -- safe here because none
        // of Summary's forwarded member names collide with anything already declared
        // directly on this class.
        Summary.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        // Build-order step 3 of the Payroll refactor plan -- see PayrollGroupViewModel's
        // own doc comment for the concern it owns (the Payroll Group table, the "Not in
        // group" roster, and every group-membership command) and why it takes Summary as a
        // constructor dependency (the refactor plan's own "Group <-> Summary" wrinkle).
        // Constructed after Summary for exactly that reason -- Summary has to exist first
        // to be handed in here. Its own constructor ends with the same "empty
        // BatchScopeEmployees explicitly" guarantee that used to sit here directly (see
        // that constructor's own doc comment), so there's nothing left to do here after
        // this call.
        Group = new PayrollGroupViewModel(
            _mainViewModel, _rosterProvider, _payrollComputationService,
            _payrollRunRepository, _statusBarService, _busy, _scope, Summary, dataVersion);

        // Same blanket-relay convention as Summary's own PropertyChanged subscription just
        // above, for the same reason -- see that subscription's own doc comment.
        Group.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        // Build-order step 4 of the Payroll refactor plan -- see PayrollRunViewModel's own
        // doc comment for the concern it owns (starting a fresh payroll run via the wizard,
        // or reopening a saved one) and why, unlike Group, it needs no reference to Summary
        // or Group themselves. Constructed after them anyway, simply to keep this
        // constructor's own ordering matching the plan's "Suggested build order" (steps 2-3
        // before step 4), not because anything here actually depends on either.
        Run = new PayrollRunViewModel(
            _mainViewModel, _rosterProvider, _payrollRunRepository, _payrollComputationService,
            _statusBarService, _busy, _scope);

        // No PropertyChanged relay for Run, unlike Summary/Group just above -- its only two
        // forwarded members (NewPayrollRunCommand/LoadPayrollGroupCommand) are commands whose
        // CanExecute changes are signaled via CommunityToolkit.Mvvm's own CanExecuteChanged,
        // not PropertyChanged, and neither command's reference ever changes after
        // construction -- so there's nothing on Run a relay would ever actually forward. Same
        // reason AttendanceViewModel's own constructor doesn't wire one up for
        // AttendanceImportViewModel/DeviceFetchViewModel/ManualEntryEditorViewModel, its own
        // three command-only children.

        // Build-order step 5 of the Payroll refactor plan, the plan's last extraction -- see
        // PayrollPrintExportViewModel's own doc comment for the concern it owns (batch
        // print/export) and why, like Run, it needs no reference to Summary or Group
        // themselves -- and, unlike Run, no reference to _mainViewModel either (see that
        // class's own doc comment for the correction). Constructed last simply to keep this
        // constructor's own ordering matching the plan's "Suggested build order" (steps 2-4
        // before step 5), not because anything here actually depends on any of the three
        // built above.
        PrintExport = new PayrollPrintExportViewModel(
            _rosterProvider, _payrollComputationService, _statusBarService, _busy, _scope, companyName);

        // No PropertyChanged relay for PrintExport, for the exact same reason Run doesn't get
        // one just above -- its only two forwarded members (PrintPayslipsCommand/
        // ExportPayrollReportCommand) are commands whose CanExecute changes are signaled via
        // CommunityToolkit.Mvvm's own CanExecuteChanged, not PropertyChanged.
    }

    // ---- Forwarded members (PayrollSummaryViewModel) ----
    //
    // Every property/command PayrollSummaryView.xaml/PayrollSummaryView.xaml.cs/
    // EmployeeAttendancePanel.xaml bind to or call, forwarded from Summary -- see that
    // class's own doc comment for why (its members are typed/bound to this class directly,
    // never to Summary itself). Nothing here has its own logic; it's here so none of that
    // XAML/code-behind needed to change as part of build-order step 2 of the Payroll
    // refactor plan.

    public Employee? SelectedEmployee => Summary.SelectedEmployee;
    public string HeaderText => Summary.HeaderText;
    public PayrollResult? Result => Summary.Result;
    public ObservableCollection<PayrollAdjustmentGroupRow> GrossPayAdjustmentGroupRows => Summary.GrossPayAdjustmentGroupRows;
    public ObservableCollection<PayrollAdjustmentGroupRow> DeductionAdjustmentGroupRows => Summary.DeductionAdjustmentGroupRows;
    public ObservableCollection<AttendanceSummaryRow> AttendanceRows => Summary.AttendanceRows;
    public bool HasAttendanceRows => Summary.HasAttendanceRows;
    public bool IsBusy => Summary.IsBusy;
    public string? EmptyStateMessage => Summary.EmptyStateMessage;
    public string? AttendanceEmptyStateMessage => Summary.AttendanceEmptyStateMessage;
    public bool CanEditAdjustmentsNow => Summary.CanEditAdjustmentsNow;

    public IAsyncRelayCommand<PayrollAdjustmentType> AddInlineRowCommand => Summary.AddInlineRowCommand;
    public Task UpdateInlineDescriptionAsync(PayrollAdjustment original, string rawDescription) =>
        Summary.UpdateInlineDescriptionAsync(original, rawDescription);
    public Task UpdateInlineAmountAsync(PayrollAdjustment original, string rawAmountText) =>
        Summary.UpdateInlineAmountAsync(original, rawAmountText);
    public IAsyncRelayCommand<PayrollAdjustment> DeleteAdjustmentCommand => Summary.DeleteAdjustmentCommand;
    public Task SetSingleValueAsync(PayrollAdjustmentType type, string rawAmountText) =>
        Summary.SetSingleValueAsync(type, rawAmountText);
    public Task SetUndertimeWaivedAsync(bool waived) => Summary.SetUndertimeWaivedAsync(waived);
    public IRelayCommand PrintCurrentPayslipCommand => Summary.PrintCurrentPayslipCommand;
    public IAsyncRelayCommand RecalculatePayslipCommand => Summary.RecalculatePayslipCommand;

    // ---- End forwarded members (PayrollSummaryViewModel) ----

    /// <summary>Thin forwarder to <see cref="PayrollScopeState.PeriodStart"/> -- storage
    /// moved there as build-order step 1 of the Payroll refactor plan (see _scope's own
    /// doc comment), but the property stays here, under this same name, since
    /// PayrollPage.xaml's DatePicker binds to it directly. The reaction this used to feed
    /// (OnPeriodStartChanged) now lives on PayrollGroupViewModel's own, independent
    /// _scope.PropertyChanged subscription (build-order step 3) -- this property no longer
    /// needs to feed anything itself, just the DatePicker's own two-way binding. A
    /// same-value set is still a no-op -- PayrollScopeState's own generated setter carries
    /// that guarantee now, exactly like this property's generated setter used to before the
    /// field moved.</summary>
    public DateTime PeriodStart
    {
        get => _scope.PeriodStart;
        set => _scope.PeriodStart = value;
    }

    /// <summary>Thin forwarder to <see cref="PayrollScopeState.PeriodEnd"/> -- see
    /// PeriodStart's own doc comment just above, which applies here identically.</summary>
    public DateTime PeriodEnd
    {
        get => _scope.PeriodEnd;
        set => _scope.PeriodEnd = value;
    }

    /// <summary>The employee scope most recently confirmed via "New Payroll Run…"
    /// (PayrollRunViewModel.NewPayrollRun, once Finish returns a saved run) -- empty until
    /// that's run at least once this session. Also settable via the old "Start Payroll
    /// Period…" flow before build-order step 9.4 removed it; NewPayrollRun is now the only
    /// writer. Not cleared by an ordinary employee-selection or period change afterward
    /// (see PayrollRunViewModel.NewPayrollRun's own doc comment for why a batch stays
    /// "active" across those); only
    /// ever replaced wholesale by a fresh confirmed scope, never appended to. What Phase 5's
    /// checklist panel reads to know which employees to list.
    ///
    /// Thin forwarder to <see cref="PayrollScopeState.BatchScopeEmployees"/> as of
    /// build-order step 1 of the Payroll refactor plan (see _scope's own doc comment) --
    /// this property, its name, and everything documented above stay exactly as they
    /// were.</summary>
    public IReadOnlyList<Employee> BatchScopeEmployees
    {
        get => _scope.BatchScopeEmployees;
        set => _scope.BatchScopeEmployees = value;
    }

    /// <summary>The PayrollRun (see PayrollRun.cs/IPayrollRunRepository) most recently
    /// saved and confirmed via the wizard's Finish -- set alongside BatchScopeEmployees
    /// above by PayrollRunViewModel.NewPayrollRun. Null until a run has been saved and
    /// confirmed via the wizard this session.
    ///
    /// Thin forwarder to <see cref="PayrollScopeState.ActivePayrollRunId"/> -- storage
    /// moved there as build-order step 1 of the Payroll refactor plan (see _scope's own
    /// doc comment).</summary>
    public int? ActivePayrollRunId
    {
        get => _scope.ActivePayrollRunId;
        set => _scope.ActivePayrollRunId = value;
    }

    // ---- Forwarded members (PayrollGroupViewModel) ----
    //
    // Every property/command PayrollPage.xaml/PayrollPage.xaml.cs bind to or call for the
    // Payroll Group table and "Not in group" roster, forwarded from Group -- see that
    // class's own doc comment for why (its members are typed/bound to this class directly,
    // never to Group itself). Nothing here has its own logic; it's here so none of that
    // XAML/code-behind needed to change as part of build-order step 3 of the Payroll
    // refactor plan.

    public ObservableCollection<PayrollGroupRow> PayrollGroupRows => Group.PayrollGroupRows;
    public ICollectionView PayrollGroupRowsView => Group.PayrollGroupRowsView;
    public ObservableCollection<AvailableEmployeeRow> AvailableEmployeeRows => Group.AvailableEmployeeRows;
    public ICollectionView AvailableEmployeeRowsView => Group.AvailableEmployeeRowsView;
    public decimal TotalNetPay => Group.TotalNetPay;
    public bool IsPayrollGroupLoading => Group.IsPayrollGroupLoading;
    public int PayrollGroupLoadPercent => Group.PayrollGroupLoadPercent;
    public string PayrollGroupSearchText
    {
        get => Group.PayrollGroupSearchText;
        set => Group.PayrollGroupSearchText = value;
    }
    public bool HasBatchScope => Group.HasBatchScope;

    public IRelayCommand<Employee> SelectBatchEmployeeCommand => Group.SelectBatchEmployeeCommand;
    public IAsyncRelayCommand<Employee?> RemoveEmployeeFromGroupCommand => Group.RemoveEmployeeFromGroupCommand;
    public IAsyncRelayCommand<Employee?> AddEmployeeToGroupCommand => Group.AddEmployeeToGroupCommand;
    public IAsyncRelayCommand AddEmployeesToGroupCommand => Group.AddEmployeesToGroupCommand;

    // ---- End forwarded members (PayrollGroupViewModel) ----

    /// <summary>Called from PayrollPage.OnNavigatedToAsync, every visit -- not really a
    /// "forwarded member" of either child the way everything above/below it is, since it
    /// deliberately reaches both: Summary.RecheckOnPageRevisitAsync() for
    /// SelectedEmployee's own detailed breakdown, then Group.RecheckOnPageRevisitAsync()
    /// for the group table's NetPay column -- see either one's own doc comment for what it
    /// actually checks before paying for a recompute, and for why Summary's own version has
    /// to exist at all now (it's what replaces the accidental "SelectBatchEmployeeCommand's
    /// own cascade happened to touch SelectedEmployee" path that used to update that panel
    /// as an unintended side effect, back before this page-revisit mechanism made the whole
    /// chain properly serialized -- see Summary.RecheckOnPageRevisitAsync's own doc comment
    /// for the full story).
    ///
    /// Summary before Group -- SelectedEmployee's own detail panel is the smaller, faster
    /// check either way (one employee, not the whole group), so there's no real cost to
    /// this order, but it means a person looking at one specific employee's breakdown sees
    /// it settle first rather than waiting on the whole table. Both genuinely awaited
    /// straight through by the same caller, same "closes the race with
    /// RestorePayrollGroupSelection's own later-queued cascade" reasoning as
    /// Group.RecheckOnPageRevisitAsync's own doc comment describes -- sequential here, not
    /// parallel, for the same "one shared, app-lifetime-scoped ScheduleDbContext" reason
    /// every other pair of calls in this app has to be.</summary>
    internal async Task RecheckOnPageRevisitAsync()
    {
        await Summary.RecheckOnPageRevisitAsync();
        await Group.RecheckOnPageRevisitAsync();
    }

    // ---- Forwarded members (PayrollRunViewModel) ----
    //
    // PayrollPage.xaml's "New Payroll Run…"/"Load Payroll Group…" toolbar and empty-state
    // buttons, forwarded from Run -- see that class's own doc comment for why (its members
    // are typed/bound to this class directly, never to Run itself). Nothing here has its
    // own logic; it's here so none of that XAML needed to change as part of build-order
    // step 4 of the Payroll refactor plan.

    public IRelayCommand NewPayrollRunCommand => Run.NewPayrollRunCommand;
    public IAsyncRelayCommand LoadPayrollGroupCommand => Run.LoadPayrollGroupCommand;

    // ---- End forwarded members (PayrollRunViewModel) ----

    // ---- Forwarded members (PayrollPrintExportViewModel) ----
    //
    // PayrollPage.xaml's "Print Payslips…"/"Export Payroll Report…" toolbar buttons,
    // forwarded from PrintExport -- see that class's own doc comment for why (its members
    // are typed/bound to this class directly, never to PrintExport itself). Nothing here has
    // its own logic; it's here so none of that XAML needed to change as part of build-order
    // step 5 of the Payroll refactor plan, the plan's last extraction.

    public IAsyncRelayCommand PrintPayslipsCommand => PrintExport.PrintPayslipsCommand;
    public IAsyncRelayCommand ExportPayrollReportCommand => PrintExport.ExportPayrollReportCommand;

    // ---- End forwarded members (PayrollPrintExportViewModel) ----
}