using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Payroll;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs PayrollWizardDialog's three-step "New Payroll Run…" wizard (build-order Group C
/// of the payroll-refactor plan): Period + Label, then Employees, then Calculate &amp;
/// Review. One instance per dialog open, built by whoever opens the dialog rather than
/// resolved from DI -- same "not DI-registered" convention PayslipScopeViewModel already
/// follows (see that class's own doc comment) -- so there's no stale period/label/
/// tree-selection state to reset between runs the way a Scoped, container-owned ViewModel
/// would need to guard against.
///
/// Step 3's own content was built in slices (build-order step 8): 8.3 wired up "Save
/// Payroll Group" itself -- straight off GetSelectedEmployees() below, via
/// IPayrollRunRepository -- ahead of the Employee/Net Pay/Gross Pay/Deductions review grid,
/// so the actual write was testable in total isolation from the grid/busy-state work that
/// came after it. 8.1/8.2 (CalculateReviewAsync below, looping IPayrollComputationService
/// once per checked employee) fill in that grid -- run automatically the moment Step 3 is
/// reached (see NextAsync), not behind a separate button, since there's nothing else to
/// configure first the way Step 1/Step 2's own content needs a person's input before it
/// means anything. This class owns the shell (which step is showing, Back/Next/Finish),
/// the first two steps' real content -- Period/Label (build-order step 6) and the Employee
/// tree (build-order step 7, reusing the same EmployeeTreeBuilder/EmployeeTreeSearchFilter/
/// DepartmentGroupViewModel machinery PayslipScopeViewModel's own tree is built from) --
/// plus Step 3's calculate-and-review grid and Save.
///
/// The dialog closes itself as accepted when NextCommand reports true (Finish, on the last
/// step); its Cancel button just closes the window. Finish itself never saves anything;
/// whatever Step 3's own "Save Payroll Group" already wrote (SavedRun, or null if it was
/// never clicked) is what the opener reads afterward.
/// </summary>
public partial class PayrollWizardViewModel : ReactiveViewModel
{
    public const int StepCount = 3;

    /// <summary>Replaces the raw IScheduleRepository this class used to read
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync through
    /// directly -- see ActiveRosterProvider's own doc comment for why Step 2's tree load now
    /// goes through the shared, RosterVersion-gated cache instead, the same swap
    /// ReportScopeViewModel/PayslipScopeViewModel make for their own, identically-shaped
    /// tree loads.</summary>
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly IPayrollRunRepository _payrollRunRepository;
    private readonly IPayrollComputationService _payrollComputationService;

    private readonly IObservable<bool> _canBack;
    private readonly IObservable<bool> _canNext;
    private readonly IObservable<bool> _canSelectAllTree;
    private readonly IObservable<bool> _canClearTreeSelection;
    private readonly IObservable<bool> _canSavePayrollGroup;

    public PayrollWizardViewModel(
        ActiveRosterProvider rosterProvider,
        IPayrollRunRepository payrollRunRepository,
        IPayrollComputationService payrollComputationService,
        DateTime? defaultPeriodStart = null,
        DateTime? defaultPeriodEnd = null)
    {
        _rosterProvider = rosterProvider;
        _payrollRunRepository = payrollRunRepository;
        _payrollComputationService = payrollComputationService;

        // ----- Step indicator / navigation -----
        _isLastStepHelper = this.WhenAnyValue(x => x.CurrentStepIndex)
            .Select(step => step == StepCount - 1)
            .ToProperty(this, x => x.IsLastStep);
        _nextButtonTextHelper = this.WhenAnyValue(x => x.IsLastStep)
            .Select(isLast => isLast ? "Finish" : "Next")
            .ToProperty(this, x => x.NextButtonText);

        // ----- Step 1 -----
        // The label follows the period (UpdateAutoLabel) until a person types over it; a typed
        // label stays theirs until it matches what auto-fill last wrote.
        this.WhenAnyValue(x => x.PeriodStart, x => x.PeriodEnd).Subscribe(_ => UpdateAutoLabel());
        this.WhenAnyValue(x => x.Label).Skip(1).Subscribe(label => _labelManuallyEdited = label != _lastAutoLabel);

        var periodAndLabel = this.WhenAnyValue(x => x.PeriodStart, x => x.PeriodEnd, x => x.Label,
            (start, end, label) => (Start: start, End: end, Label: label));
        _isPeriodAndLabelValidHelper = periodAndLabel
            .Select(p => p.Start is { } start && p.End is { } end && end >= start && !string.IsNullOrWhiteSpace(p.Label))
            .ToProperty(this, x => x.IsPeriodAndLabelValid);
        _periodValidationMessageHelper = periodAndLabel
            .Select(p => DescribePeriodProblem(p.Start, p.End, p.Label))
            .ToProperty(this, x => x.PeriodValidationMessage);

        // ----- Step 2 -----
        var selectionMoved = Observable.Switch(this.WhenAnyValue(x => x.LoadedTree)
            .Select(tree => EmployeeTreeBuilder.SelectionChanges(tree).StartWith(RxVoid.Default)));
        _totalEmployeeCountHelper = this.WhenAnyValue(x => x.LoadedTree)
            .Select(tree => tree.Sum(d => d.Employees.Count))
            .ToProperty(this, x => x.TotalEmployeeCount);
        _selectedEmployeeCountHelper = selectionMoved
            .Select(_ => CountSelected())
            .ToProperty(this, x => x.SelectedEmployeeCount);
        // Every checkbox change, not just a new count -- a swap can change which departments
        // are fully checked without changing how many people are.
        _selectionScopeTextHelper = selectionMoved
            .Select(_ => DescribeSelection())
            .ToProperty(this, x => x.SelectionScopeText);
        this.WhenAnyValue(x => x.SearchText)
            .Skip(1)
            .Subscribe(searchText => EmployeeTreeSearchFilter.Apply(Departments, searchText, VisibleDepartments));

        // ----- Step 3 -----
        _isReviewReadyHelper = this.WhenAnyValue(x => x.IsCalculating, x => x.CalculationErrorMessage,
                (isCalculating, error) => !isCalculating && error is null)
            .ToProperty(this, x => x.IsReviewReady);
        // ReviewResults only changes inside CalculateReviewAsync, between IsCalculating going
        // true and back to false, so its totals are re-read each time that flips.
        var reviewSettled = this.WhenAnyValue(x => x.IsCalculating);
        _totalNetPayHelper = reviewSettled.Select(_ => ReviewResults.Sum(r => r.NetPay)).ToProperty(this, x => x.TotalNetPay);
        _totalGrossPayHelper = reviewSettled.Select(_ => ReviewResults.Sum(r => r.TotalGrossPay)).ToProperty(this, x => x.TotalGrossPay);
        _totalDeductionsHelper = reviewSettled.Select(_ => ReviewResults.Sum(r => r.TotalDeductions)).ToProperty(this, x => x.TotalDeductions);
        _savedRunSummaryHelper = this.WhenAnyValue(x => x.SavedRun)
            .Select(run => run is null ? null : $"Saved as \"{run.Label}\" (run #{run.Id}).")
            .ToProperty(this, x => x.SavedRunSummary);

        // ----- What each command needs -----
        _canBack = this.WhenAnyValue(x => x.CurrentStepIndex, x => x.IsCalculating,
            (step, isCalculating) => step > 0 && !isCalculating);
        _canNext = this.WhenAnyValue(x => x.CurrentStepIndex, x => x.IsCalculating, x => x.IsPeriodAndLabelValid,
            x => x.SelectedEmployeeCount,
            (step, isCalculating, periodValid, selected) => !isCalculating && step switch
            {
                0 => periodValid,
                1 => selected > 0,
                _ => true,
            });
        _canSelectAllTree = this.WhenAnyValue(x => x.SelectedEmployeeCount, x => x.TotalEmployeeCount,
            (selected, total) => selected < total);
        _canClearTreeSelection = this.WhenAnyValue(x => x.SelectedEmployeeCount).Select(selected => selected > 0);
        _canSavePayrollGroup = this.WhenAnyValue(x => x.IsSaving, x => x.IsCalculating, x => x.SavedRun,
            x => x.SelectedEmployeeCount,
            (isSaving, isCalculating, savedRun, selected) => !isSaving && !isCalculating && savedRun is null && selected > 0);

        // Through the properties, so the label is filled in straight away when a caller hands
        // in a sensible starting period (e.g. the Payroll tab's own current period) -- the
        // person opening this wizard sees an already-filled Label rather than typing dates
        // before anything downstream reacts.
        PeriodStart = defaultPeriodStart;
        PeriodEnd = defaultPeriodEnd;
    }

    // ----- Step indicator / navigation (build-order step 5) -----

    /// <summary>Which step is showing, 0-2. The dialog shows each step's content, and bolds
    /// its label in the step indicator, off this.</summary>
    [Reactive]
    public partial int CurrentStepIndex { get; set; }

    [ObservableAsProperty]
    public partial bool IsLastStep { get; }

    /// <summary>"Next" on every step but the last, where it reads "Finish" instead --
    /// same button, same NextCommand, just relabeled, so there's no separate FinishCommand a
    /// person could somehow invoke from the wrong step.</summary>
    [ObservableAsProperty(InitialValue = "Next")]
    public partial string NextButtonText { get; }

    /// <summary>Blocked while IsCalculating -- same "don't let a person navigate out from under
    /// an in-flight round trip" reasoning NextCommand applies to itself; there's no
    /// CancellationToken plumbed through CalculateReviewAsync for Back to cancel it with (see
    /// that method's own doc comment), so blocking the button is what stands in for that
    /// instead.</summary>
    [ReactiveCommand(CanExecute = nameof(_canBack))]
    private void Back() => CurrentStepIndex--;

    /// <summary>Advances the step indicator and, the moment that lands on Step 3 (index 2),
    /// kicks off CalculateReviewAsync -- see that method's own doc comment for why this is
    /// the one place that call happens rather than something Step 3's own view triggers on
    /// load. Awaited (not fire-and-forget) so the IsCalculating gate on Back/Next actually
    /// reflects an in-flight calculation for as long as one is running, the same way
    /// SavePayrollGroupAsync's IsSaving does for that command. On the last step this is
    /// Finish instead: it reports true, which closes the dialog as accepted.
    ///
    /// Leaving each step needs that step's own content to be usable afterward -- Step 1 a
    /// real (non-backwards) period and a non-blank label (see IsPeriodAndLabelValid), Step 2
    /// at least one employee checked. Same "check at least one employee to continue" rule
    /// PayslipScopeDialog enforces on confirm -- here it just keeps Next disabled instead,
    /// since a wizard's Next is naturally a no-op until the step ahead has something to show.
    /// Finish is always enabled once you can reach it.</summary>
    [ReactiveCommand(CanExecute = nameof(_canNext))]
    private async Task<bool> NextAsync()
    {
        if (IsLastStep)
            return true;

        CurrentStepIndex++;

        if (CurrentStepIndex == 2)
            await CalculateReviewAsync();

        return false;
    }

    // ----- Step 1: Period + Label (build-order step 6) -----

    [Reactive]
    public partial DateTime? PeriodStart { get; set; }

    [Reactive]
    public partial DateTime? PeriodEnd { get; set; }

    /// <summary>Shown as this run's own title text wherever it's listed later (see
    /// PayrollRun.Label's own doc comment for the "auto-filled, freely editable"
    /// convention this implements) and carried straight through to PayrollRun.Label on
    /// save (build-order step 8). UpdateAutoLabel keeps this in sync with
    /// PeriodStart/PeriodEnd until a person types something that doesn't match the last
    /// auto-generated value, at which point _labelManuallyEdited latches true and this
    /// stops being touched automatically.</summary>
    [Reactive]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>The last value UpdateAutoLabel itself wrote to Label -- a typed label is
    /// compared against this (not against some fixed "was it ever touched" flag), so
    /// retyping back to exactly what auto-fill last produced un-latches
    /// _labelManuallyEdited rather than leaving it stuck permanently latched from one
    /// earlier keystroke.</summary>
    private string _lastAutoLabel = string.Empty;

    private bool _labelManuallyEdited;

    /// <summary>Regenerates Label from PeriodStart/PeriodEnd (e.g. "May 1-15, 2026" -- see
    /// PayrollRun.Label's own doc comment) whenever either date changes, unless a person
    /// has already hand-edited it away from the last auto-generated value (see
    /// _labelManuallyEdited). No-ops -- leaves Label exactly as it already is -- while
    /// either date is still unset or the period is backwards, rather than clearing it back
    /// to blank; a person who typed a label before finishing the date pickers shouldn't
    /// see it vanish out from under them.</summary>
    private void UpdateAutoLabel()
    {
        if (_labelManuallyEdited) return;
        if (PeriodStart is not { } start || PeriodEnd is not { } end || end < start) return;

        _lastAutoLabel = BuildAutoLabel(start, end);
        Label = _lastAutoLabel;
    }

    /// <summary>"May 1-15, 2026" for a period within one month/year; "Apr 29 - May 5,
    /// 2026" once it crosses a month boundary (short month names on both ends, so a
    /// same-year cross-month period still reads as one line); "Dec 29, 2025 - Jan 4, 2026"
    /// once it also crosses a year boundary, so neither end's year is left ambiguous.
    /// </summary>
    private static string BuildAutoLabel(DateTime start, DateTime end)
    {
        if (start.Year == end.Year && start.Month == end.Month)
            return $"{start:MMMM} {start.Day}-{end.Day}, {start:yyyy}";

        if (start.Year == end.Year)
            return $"{start:MMM} {start.Day} - {end:MMM} {end.Day}, {start:yyyy}";

        return $"{start:MMM d, yyyy} - {end:MMM d, yyyy}";
    }

    [ObservableAsProperty]
    public partial bool IsPeriodAndLabelValid { get; }

    /// <summary>Inline hint shown under the period/label fields explaining why Next is
    /// currently disabled -- null (hidden) once IsPeriodAndLabelValid, same "explain the
    /// actual reason, not a generic fallback" idea PayrollViewModel.EmptyStateMessage
    /// already follows. Checked in the same order a person would naturally fill the
    /// fields: dates first, then a backwards period, then the label.</summary>
    [ObservableAsProperty]
    public partial string? PeriodValidationMessage { get; }

    private static string? DescribePeriodProblem(DateTime? start, DateTime? end, string label)
    {
        if (start is null || end is null) return "Choose a period start and end date.";
        if (end < start) return "Period end can't be before period start.";
        if (string.IsNullOrWhiteSpace(label)) return "Enter a label for this payroll run.";
        return null;
    }

    // ----- Step 2: Employees (build-order step 7) -----

    /// <summary>Same Department/Employee checkbox tree shape as
    /// PayslipScopeViewModel.Departments -- built from the same EmployeeTreeBuilder,
    /// filtered by the same EmployeeTreeSearchFilter (see SearchText below) -- just owned
    /// here instead of a dialog-specific ViewModel of its own, since this wizard has no
    /// separate window per step. Loaded once, when the dialog opens, not reloaded on every
    /// visit to this step, so IsSelected state (and SearchText) survives clicking Back to
    /// Step 1 and Next again, the same way PeriodStart/PeriodEnd/Label above survive it just
    /// by staying set on this one shared ViewModel instance.</summary>
    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = [];

    /// <summary>The departments SearchText hasn't hidden, each showing its VisibleEmployees --
    /// what this dialog's SfTreeView binds to (see EmployeeTreeSearchFilter.Apply).</summary>
    public ObservableCollection<DepartmentGroupViewModel> VisibleDepartments { get; } = [];

    /// <summary>The tree LoadEmployeeTreeAsync built, once it's in Departments -- what the
    /// selection counts and scope text follow.</summary>
    [Reactive]
    internal partial IReadOnlyList<DepartmentGroupViewModel> LoadedTree { get; private set; } = [];

    /// <summary>Same comma-separated Employee ID/first name/last name/department matching
    /// as PayslipScopeViewModel.SearchText -- see EmployeeTreeSearchFilter, shared between
    /// both.</summary>
    [Reactive]
    public partial string SearchText { get; set; } = string.Empty;

    [ReactiveCommand]
    private async Task LoadEmployeeTreeAsync()
    {
        Departments.Clear();

        var (departments, unassigned) = await _rosterProvider.GetAsync();

        var tree = EmployeeTreeBuilder.Build(departments, unassigned);
        foreach (var group in tree)
            Departments.Add(group);
        LoadedTree = tree;

        // Every employee (and therefore every department) starts checked -- same
        // "whole company is the common case" default PayslipScopeViewModel's own
        // LoadEmployeeTreeAsync uses (see that class's own doc comment).
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = true;

        EmployeeTreeSearchFilter.Apply(Departments, SearchText, VisibleDepartments);
    }

    [ObservableAsProperty]
    public partial int SelectedEmployeeCount { get; }

    [ObservableAsProperty]
    public partial int TotalEmployeeCount { get; }

    /// <summary>Same wording/logic as PayslipScopeViewModel.SelectionScopeText -- see that
    /// property's own doc comment.</summary>
    [ObservableAsProperty(InitialValue = "No employees loaded yet")]
    public partial string SelectionScopeText { get; }

    private int CountSelected() => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    private string DescribeSelection()
    {
        int total = CountSelected();
        int all = Departments.Sum(d => d.Employees.Count);

        if (all == 0) return "No employees loaded yet";
        if (total == all) return "Whole company (all employees checked)";
        if (total == 0) return "Nothing selected -- check at least one employee to continue";

        var fullyCheckedDepartments = Departments
            .Where(d => d.IsSelected == true && d.Employees.Count > 0)
            .ToList();

        if (fullyCheckedDepartments.Count > 0 && fullyCheckedDepartments.Sum(d => d.Employees.Count) == total)
        {
            return fullyCheckedDepartments.Count == 1
                ? $"Department: {fullyCheckedDepartments[0].Name}"
                : $"{fullyCheckedDepartments.Count} departments: " +
                    string.Join(", ", fullyCheckedDepartments.Select(d => d.Name));
        }

        return total == 1 ? "1 employee selected" : $"{total} employees selected";
    }

    [ReactiveCommand(CanExecute = nameof(_canSelectAllTree))]
    private void SelectAllTree()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = true;
    }

    [ReactiveCommand(CanExecute = nameof(_canClearTreeSelection))]
    private void ClearTreeSelection()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = false;
    }

    /// <summary>Every currently-checked employee. What CalculateReviewAsync below loops
    /// IPayrollComputationService over, and what SavePayrollGroupAsync reads independently
    /// of that grid -- see that method's own doc comment.</summary>
    public List<Employee> GetSelectedEmployees() =>
        [.. Departments
            .SelectMany(d => d.Employees)
            .Where(n => n.IsSelected)
            .Select(n => n.Employee)];

    // ----- Step 3: Calculate & Review (build-order step 8 -- 8.1/8.2's own review grid,
    // driven by CalculateReviewAsync below, plus 8.3's "Save Payroll Group") -----

    /// <summary>One row per employee CalculateReviewAsync computed, in the same order
    /// GetSelectedEmployees() itself returns them -- exactly what PayrollWizardDialog's own
    /// grid shows for the Employee/Net Pay/Gross Pay/Deductions review, straight off
    /// PayrollResult's own EmployeeName/TotalGrossPay/TotalDeductions/NetPay rather than a
    /// wizard-specific row type, since PayrollResult already carries everything that grid
    /// needs -- same values PayrollSummaryView itself renders for one employee at a time on
    /// the Payroll tab, which is what the build-order plan's own checkpoint ("numbers match
    /// what the Payroll tab shows for the same employee/period today") is checking against.
    /// Cleared and rebuilt on every CalculateReviewAsync run rather than updated in place --
    /// same "fresh set swapped in" convention PayrollGroupRow's own doc comment
    /// describes for RefreshPayrollGroupRowsAsync -- there's nothing here to preserve
    /// between runs since a person can't edit anything on this grid directly.</summary>
    public ObservableCollection<PayrollResult> ReviewResults { get; } = [];

    /// <summary>True while CalculateReviewAsync runs -- blocks navigating away from Step 3
    /// and blocks Save, the same way a save in flight blocks navigating away and blocks a
    /// second Save.</summary>
    [Reactive]
    public partial bool IsCalculating { get; private set; }

    /// <summary>Same "explain the actual reason inline" idea as PeriodValidationMessage/
    /// SaveErrorMessage elsewhere on this class -- null (nothing wrong) once
    /// CalculateReviewAsync finishes cleanly. Cleared at the start of every
    /// CalculateReviewAsync attempt, not just on success, so retrying (by clicking Back
    /// then Next again) doesn't leave a stale message on screen underneath a fresh
    /// one.</summary>
    [Reactive]
    public partial string? CalculationErrorMessage { get; private set; }

    /// <summary>True once there's an actual grid to show -- neither still calculating nor
    /// failed. What PayrollWizardDialog's grid (and the totals strip under it) shows itself
    /// off, alongside the "Calculating…" pulse (IsCalculating) and the error text
    /// (CalculationErrorMessage) each following their own condition instead -- the three are
    /// mutually exclusive by construction, since CalculateReviewAsync always leaves exactly
    /// one of "still running", "CalculationErrorMessage set", or "ReviewResults populated"
    /// true when it finishes.</summary>
    [ObservableAsProperty]
    public partial bool IsReviewReady { get; }

    /// <summary>Sum of every row's own NetPay/TotalGrossPay/TotalDeductions -- the totals
    /// strip under the grid, re-read whenever a calculation starts or settles.</summary>
    [ObservableAsProperty]
    public partial decimal TotalNetPay { get; }

    [ObservableAsProperty]
    public partial decimal TotalGrossPay { get; }

    [ObservableAsProperty]
    public partial decimal TotalDeductions { get; }

    /// <summary>Build-order steps 8.1/8.2: IPayrollComputationService.PrepareBatchAsync once
    /// for every employee GetSelectedEmployees() currently returns, then
    /// ComputeOneFromBatchAsync per employee against that shared result -- same
    /// batch-then-per-employee shape PayrollPrintExportViewModel.PrintPayslipsAsync/
    /// ExportPayrollReportAsync use (see PayrollBatchContext's own doc comment for why
    /// looping ComputeOneAsync here instead used to repeat a company-wide attendance fetch
    /// once per selected employee, which matters more here than almost anywhere else in
    /// Payroll: this runs on every single visit to Step 3, including Back-then-Next with no
    /// selection change at all) -- and populates ReviewResults from it. Called once,
    /// automatically, from NextAsync the moment Step 3 is reached (see that method's own doc
    /// comment), and again every time Step 3 is reached afterward (Back to Step 2, change
    /// who's checked, Next again) -- nothing here is cached across visits, same "recomputed
    /// live every time, nothing on PayrollResult is itself persisted" convention that
    /// class's own doc comment describes for the Payroll tab itself, so a changed selection
    /// is always what Step 3 actually shows rather than a stale grid from an earlier visit.
    ///
    /// No CancellationToken threaded through the loop -- same as LoadEmployeeTreeAsync
    /// above, this dialog has no shared busy state to hand one down from (see this class's
    /// own doc comment) -- Back/Next being blocked while IsCalculating is what stands in for
    /// cancellation instead of an actual CancellationTokenSource.
    ///
    /// Deliberately doesn't feed SavePayrollGroupAsync below -- that command still reads
    /// GetSelectedEmployees() directly, straight from Step 2's own tree, rather than from
    /// ReviewResults, so a save can never end up saving a set of employees that doesn't
    /// match the grid a person is currently looking at (or a grid that failed to load) --
    /// see SavePayrollGroupAsync's own doc comment.</summary>
    private async Task CalculateReviewAsync()
    {
        IsCalculating = true;
        CalculationErrorMessage = null;
        ReviewResults.Clear();

        try
        {
            var start = DateOnly.FromDateTime(PeriodStart!.Value);
            var end = DateOnly.FromDateTime(PeriodEnd!.Value);
            var selected = GetSelectedEmployees();

            var pins = selected.Select(e => e.Pin).ToHashSet();
            var batch = await _payrollComputationService.PrepareBatchAsync(
                pins, start, end, CancellationToken.None);

            // Same batch-wide seed-before-compute shape PayrollGroupViewModel.
            // RefreshPayrollGroupRowsAsync uses -- see SeedContributionsForBatchAsync's own
            // doc comment. Matters more here than almost anywhere else in Payroll (per this
            // method's own doc comment above): this runs on every single visit to Step 3.
            batch = await _payrollComputationService.SeedContributionsForBatchAsync(
                selected, start, end, batch, CancellationToken.None);

            foreach (var employee in selected)
            {
                var (result, _) = await _payrollComputationService.ComputeOneFromBatchAsync(
                    employee, start, end, batch, CancellationToken.None);
                ReviewResults.Add(result);
            }
        }
        catch (Exception ex)
        {
            CalculationErrorMessage = $"Couldn't calculate payroll for review: {ex.Message}";
            ReviewResults.Clear();
        }
        finally
        {
            IsCalculating = false;
        }
    }

    /// <summary>True while SavePayrollGroupAsync's write is in flight.</summary>
    [Reactive]
    public partial bool IsSaving { get; private set; }

    /// <summary>Null until SavePayrollGroupCommand succeeds; the created PayrollRun
    /// afterward, Id/CreatedAt included (see IPayrollRunRepository.CreateAsync). What the
    /// opener (PayrollRunViewModel.NewPayrollRunAsync) reads once the dialog closes;
    /// SavedRunSummary below is what the dialog itself shows.</summary>
    [Reactive]
    public partial PayrollRun? SavedRun { get; private set; }

    /// <summary>The exact Employee objects SavedRun.Employees was built from -- captured
    /// once, at the moment SavePayrollGroupCommand succeeds, rather than left as a live
    /// GetSelectedEmployees() read. Nothing stops a person from clicking Back to Step 2 and
    /// changing the tree after saving -- Save itself is blocked once SavedRun is set, but
    /// not Back -- so without this, the employees the opener reads (and therefore
    /// PayrollRunViewModel.NewPayrollRun's own BatchScopeEmployees/checklist) could silently
    /// diverge from what's actually sitting in the PayrollRunEmployees table: the in-session
    /// checklist would show whatever's checked right now, while a later "Load Payroll
    /// Group…" of this same run would show what was saved. Freezing the selection here the
    /// moment it's written keeps both readings of "this run's employees" identical,
    /// regardless of anything Step 2 does afterward.</summary>
    [Reactive]
    public partial IReadOnlyList<Employee>? SavedEmployees { get; private set; }

    /// <summary>Plain success line shown under the Save button once SavedRun is set --
    /// null (hidden) beforehand.</summary>
    [ObservableAsProperty]
    public partial string? SavedRunSummary { get; }

    /// <summary>Same "explain the actual reason inline" idea as PeriodValidationMessage
    /// above, shown under the Save button instead of under the period fields. Null once
    /// there's nothing wrong -- cleared at the start of every SavePayrollGroupAsync
    /// attempt, not just on success, so retrying after a failure doesn't leave the old
    /// message on screen underneath a fresh one.</summary>
    [Reactive]
    public partial string? SaveErrorMessage { get; set; }

    /// <summary>Builds and persists one PayrollRun from this step's own Label/PeriodStart/
    /// PeriodEnd (Step 1) and GetSelectedEmployees() (Step 2) -- deliberately not from
    /// ReviewResults, even though that grid is on screen right above this button by the
    /// time it's clickable (see CalculateReviewAsync's own doc comment for why the two stay
    /// independent); the membership this saves is exactly what Step 2's tree has checked,
    /// the same set PrintPayslipsAsync's own batch loop would compute over. CreatedBy
    /// defaults to Environment.UserName, same no-user-system convention as
    /// PayrollAdjustment.EnteredBy (see PayrollRun.CreatedBy's own doc comment).
    /// Deliberately does nothing to Finish -- this is its own button, separate from the
    /// wizard's own navigation.
    ///
    /// Enabled once there's someone checked, nothing is calculating or saving, and nothing
    /// has been saved yet -- IPayrollRunRepository has no update/append (see that
    /// interface's own doc comment: re-running the wizard makes a new run rather than editing
    /// an old one), so a second click would just create a duplicate row. Blocking it while
    /// CalculateReviewAsync is still filling in the grid above isn't about correctness (Save
    /// doesn't read ReviewResults), just keeping the button's state in step with the screen.
    ///
    /// GetSelectedEmployees() is read exactly once, into selectedEmployees, and that same
    /// list backs both run.Employees (as Pins) and SavedEmployees (as Employee objects) --
    /// see SavedEmployees' own doc comment for why capturing it here matters.</summary>
    [ReactiveCommand(CanExecute = nameof(_canSavePayrollGroup))]
    private async Task SavePayrollGroupAsync()
    {
        IsSaving = true;
        SaveErrorMessage = null;

        try
        {
            var selectedEmployees = GetSelectedEmployees();

            var run = new PayrollRun
            {
                Label = Label,
                PeriodStart = DateOnly.FromDateTime(PeriodStart!.Value),
                PeriodEnd = DateOnly.FromDateTime(PeriodEnd!.Value),
                CreatedBy = Environment.UserName,
                Employees = [.. selectedEmployees.Select(e => new PayrollRunEmployee { EmployeeId = e.Pin })],
            };

            // No CancellationToken here (unlike PayrollViewModel's own _busy.RunAsync-wrapped
            // commands) -- this dialog has no shared busy state to hand one down from, and a
            // single CreateAsync round trip has nothing worth cancelling mid-flight the way a
            // per-employee seed/compute loop would.
            var saved = await _payrollRunRepository.CreateAsync(run);
            SavedEmployees = selectedEmployees;
            SavedRun = saved;
        }
        catch (Exception ex)
        {
            SaveErrorMessage = $"Couldn't save this payroll run: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
