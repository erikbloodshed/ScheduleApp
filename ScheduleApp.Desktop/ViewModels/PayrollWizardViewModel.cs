using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs PayrollWizardDialog's three-step "New Payroll Run…" wizard (build-order Group C
/// of the payroll-refactor plan): Period + Label, then Employees, then Calculate &amp;
/// Review. One instance per dialog open, constructed directly by whoever opens the dialog
/// rather than resolved from DI -- same "not DI-registered" convention
/// PayslipScopeViewModel already follows (see that class's own doc comment) -- so there's
/// no stale period/label/tree-selection state to reset between runs the way a Scoped,
/// container-owned ViewModel would need to guard against.
///
/// Step 3's own content was built in slices (build-order step 8): 8.3 wired up "Save
/// Payroll Group" itself -- straight off GetSelectedEmployees() below, via
/// IPayrollRunRepository -- ahead of the Employee/Net Pay/Gross Pay/Deductions review grid,
/// so the actual write was testable in total isolation from the grid/busy-state work that
/// came after it. 8.1/8.2 (CalculateReviewAsync below, looping IPayrollComputationService
/// once per checked employee) fill in that grid -- run automatically the moment Step 3 is
/// reached (see NextAsync), not behind a separate button, since there's nothing else to
/// configure first the way Step 1/Step 2's own content needs a person's input before it
/// means anything. This class currently owns the shell (which step is showing,
/// Back/Next/Finish), the first two steps' real content -- Period/Label (build-order step
/// 6) and the Employee tree (build-order step 7, reusing the same
/// EmployeeTreeBuilder/EmployeeTreeSearchFilter/DepartmentGroupViewModel machinery
/// PayslipScopeViewModel's own tree is built from) -- plus Step 3's calculate-and-review
/// grid and Save.
/// </summary>
public partial class PayrollWizardViewModel : ObservableObject
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

        // Runs through the ordinary property setters (not backing fields) so
        // OnPeriodStartChanged/OnPeriodEndChanged's own UpdateAutoLabel call fires
        // immediately when a caller hands in a sensible starting period (e.g. the Payroll
        // tab's own current period) -- the person opening this wizard sees an already-filled
        // Label rather than typing dates before anything downstream reacts.
        PeriodStart = defaultPeriodStart;
        PeriodEnd = defaultPeriodEnd;
    }

    // ----- Step indicator / navigation (build-order step 5) -----

    [ObservableProperty]
    private int currentStepIndex;

    public bool IsFirstStep => CurrentStepIndex == 0;
    public bool IsLastStep => CurrentStepIndex == StepCount - 1;

    public bool IsStep1Visible => CurrentStepIndex == 0;
    public bool IsStep2Visible => CurrentStepIndex == 1;
    public bool IsStep3Visible => CurrentStepIndex == 2;

    /// <summary>Drives the step indicator row's own bold/highlighted label -- true only
    /// for whichever step is currently showing, same one-of-three idea as
    /// IsStep1Visible/IsStep2Visible/IsStep3Visible above, just for the indicator instead
    /// of the content area underneath it.</summary>
    public bool IsStep1Active => CurrentStepIndex == 0;
    public bool IsStep2Active => CurrentStepIndex == 1;
    public bool IsStep3Active => CurrentStepIndex == 2;

    /// <summary>"Next" on every step but the last, where it reads "Finish" instead --
    /// same button, same NextCommand (see NextCommand/CanGoNext below), just relabeled, so
    /// there's no separate FinishCommand a person could somehow invoke from the wrong
    /// step.</summary>
    public string NextButtonText => IsLastStep ? "Finish" : "Next";

    partial void OnCurrentStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(IsStep1Visible));
        OnPropertyChanged(nameof(IsStep2Visible));
        OnPropertyChanged(nameof(IsStep3Visible));
        OnPropertyChanged(nameof(IsStep1Active));
        OnPropertyChanged(nameof(IsStep2Active));
        OnPropertyChanged(nameof(IsStep3Active));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when the dialog itself should close -- true from Finish, false from
    /// Cancel. Finish itself never saves anything on its own; whatever Step 3's own "Save
    /// Payroll Group" already wrote (SavedRun, or null if it was never clicked) is what a
    /// caller reads after ShowDialog() returns. A plain .NET event rather
    /// than this class touching Window.DialogResult/Close itself, so PayrollWizardDialog's
    /// own code-behind stays the only place that knows this is backing an actual Window --
    /// it subscribes to this once, in its constructor.</summary>
    public event EventHandler<bool>? RequestClose;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => CurrentStepIndex--;

    /// <summary>Also blocked while IsCalculating -- same "don't let a person navigate out
    /// from under an in-flight round trip" reasoning CanGoNext below applies to itself;
    /// there's no CancellationToken plumbed through CalculateReviewAsync for Back to
    /// cancel it with (see that method's own doc comment), so blocking the button is what
    /// stands in for that instead.</summary>
    private bool CanGoBack() => !IsFirstStep && !IsCalculating;

    /// <summary>Advances the step indicator and, the moment that lands on Step 3 (index 2),
    /// kicks off CalculateReviewAsync -- see that method's own doc comment for why this is
    /// the one place that call happens rather than something Step 3's own view triggers on
    /// load. Awaited (not fire-and-forget) so CanGoNext/CanGoBack's own IsCalculating check
    /// actually reflects an in-flight calculation for as long as one is running, the same
    /// way SavePayrollGroupAsync's IsSaving does for that command.</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (IsLastStep)
        {
            RequestClose?.Invoke(this, true);
            return;
        }

        CurrentStepIndex++;

        if (CurrentStepIndex == 2)
            await CalculateReviewAsync();
    }

    /// <summary>Gates leaving each step on that step's own content actually being usable
    /// afterward -- Step 1 needs a real (non-backwards) period and a non-blank label (see
    /// IsPeriodAndLabelValid), Step 2 needs at least one employee checked. Same "check at
    /// least one employee to continue" rule PayslipScopeDialog enforces via a warning
    /// message box on confirm -- here it just keeps Next disabled instead, since a
    /// wizard's Next is naturally a no-op until the step ahead has something to show,
    /// rather than something a person confirms and then gets bounced back from. Step 3 has
    /// nothing of its own to validate yet -- Finish is always enabled once you can reach
    /// it; build-order step 8 is what gives it something to actually do before that's
    /// true.</summary>
    private bool CanGoNext() => !IsCalculating && CurrentStepIndex switch
    {
        0 => IsPeriodAndLabelValid,
        1 => SelectedEmployeeCount > 0,
        _ => true,
    };

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    // ----- Step 1: Period + Label (build-order step 6) -----

    [ObservableProperty]
    private DateTime? periodStart;

    [ObservableProperty]
    private DateTime? periodEnd;

    /// <summary>Shown as this run's own title text wherever it's listed later (see
    /// PayrollRun.Label's own doc comment for the "auto-filled, freely editable"
    /// convention this implements) and carried straight through to PayrollRun.Label on
    /// save (build-order step 8). UpdateAutoLabel below keeps this in sync with
    /// PeriodStart/PeriodEnd until a person types something that doesn't match the last
    /// auto-generated value, at which point _labelManuallyEdited latches true and this
    /// stops being touched automatically.</summary>
    [ObservableProperty]
    private string label = string.Empty;

    /// <summary>The last value UpdateAutoLabel itself wrote to Label -- OnLabelChanged
    /// compares the newly-typed value against this (not against some fixed "was it ever
    /// touched" flag), so retyping back to exactly what auto-fill would have produced
    /// anyway un-latches _labelManuallyEdited rather than leaving it stuck permanently
    /// latched from one earlier keystroke.</summary>
    private string _lastAutoLabel = string.Empty;

    private bool _labelManuallyEdited;

    partial void OnPeriodStartChanged(DateTime? value)
    {
        UpdateAutoLabel();
        NotifyPeriodAndLabelValidity();
    }

    partial void OnPeriodEndChanged(DateTime? value)
    {
        UpdateAutoLabel();
        NotifyPeriodAndLabelValidity();
    }

    partial void OnLabelChanged(string value)
    {
        _labelManuallyEdited = value != _lastAutoLabel;
        NotifyPeriodAndLabelValidity();
    }

    private void NotifyPeriodAndLabelValidity()
    {
        OnPropertyChanged(nameof(IsPeriodAndLabelValid));
        OnPropertyChanged(nameof(PeriodValidationMessage));
        NextCommand.NotifyCanExecuteChanged();
    }

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

    public bool IsPeriodAndLabelValid =>
        PeriodStart is { } start && PeriodEnd is { } end && end >= start && !string.IsNullOrWhiteSpace(Label);

    /// <summary>Inline hint shown under the period/label fields explaining why Next is
    /// currently disabled -- null (hidden) once IsPeriodAndLabelValid, same "explain the
    /// actual reason, not a generic fallback" idea PayrollViewModel.EmptyStateMessage
    /// already follows. Checked in the same order a person would naturally fill the
    /// fields: dates first, then a backwards period, then the label.</summary>
    public string? PeriodValidationMessage
    {
        get
        {
            if (PeriodStart is null || PeriodEnd is null) return "Choose a period start and end date.";
            if (PeriodEnd < PeriodStart) return "Period end can't be before period start.";
            if (string.IsNullOrWhiteSpace(Label)) return "Enter a label for this payroll run.";
            return null;
        }
    }

    // ----- Step 2: Employees (build-order step 7) -----

    /// <summary>Same Department/Employee checkbox tree shape as
    /// PayslipScopeViewModel.Departments -- built from the same EmployeeTreeBuilder,
    /// filtered by the same EmployeeTreeSearchFilter (see SearchText below) -- just owned
    /// here instead of a dialog-specific ViewModel of its own, since this wizard has no
    /// separate window per step. Loaded once, from PayrollWizardDialog's own Loaded
    /// handler (see that class), not reloaded on every visit to this step, so IsSelected
    /// state (and SearchText) survives clicking Back to Step 1 and Next again, the same
    /// way PeriodStart/PeriodEnd/Label above survive it just by staying set on this one
    /// shared ViewModel instance.</summary>
    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = new();

    /// <summary>Same comma-separated Employee ID/first name/last name/department matching
    /// as PayslipScopeViewModel.SearchText -- see EmployeeTreeSearchFilter, shared between
    /// both.</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => EmployeeTreeSearchFilter.Apply(Departments, value);

    [RelayCommand]
    private async Task LoadEmployeeTreeAsync()
    {
        Departments.Clear();

        var (departments, unassigned) = await _rosterProvider.GetAsync();

        foreach (var group in EmployeeTreeBuilder.Build(departments, unassigned, OnEmployeeNodeSelectionChanged))
            Departments.Add(group);

        // Every employee (and therefore every department) starts checked -- same
        // "whole company is the common case" default PayslipScopeViewModel's own
        // LoadEmployeeTreeAsync uses (see that class's own doc comment).
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = true;

        NotifyEmployeeSelectionChanged();

        EmployeeTreeSearchFilter.Apply(Departments, SearchText);
    }

    private void OnEmployeeNodeSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EmployeeNodeViewModel.IsSelected)) return;

        NotifyEmployeeSelectionChanged();
    }

    private void NotifyEmployeeSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedEmployeeCount));
        OnPropertyChanged(nameof(SelectionScopeText));
        SelectAllTreeCommand.NotifyCanExecuteChanged();
        ClearTreeSelectionCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        SavePayrollGroupCommand.NotifyCanExecuteChanged();
    }

    public int SelectedEmployeeCount => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    public int TotalEmployeeCount => Departments.Sum(d => d.Employees.Count);

    /// <summary>Same wording/logic as PayslipScopeViewModel.SelectionScopeText -- see that
    /// property's own doc comment.</summary>
    public string SelectionScopeText
    {
        get
        {
            int total = SelectedEmployeeCount;
            int all = TotalEmployeeCount;

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
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllTree))]
    private void SelectAllTree()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = true;
    }

    private bool CanSelectAllTree() => SelectedEmployeeCount < TotalEmployeeCount;

    [RelayCommand(CanExecute = nameof(CanClearTreeSelection))]
    private void ClearTreeSelection()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = false;
    }

    private bool CanClearTreeSelection() => SelectedEmployeeCount > 0;

    /// <summary>Every currently-checked employee. What CalculateReviewAsync below loops
    /// IPayrollComputationService over, and what SavePayrollGroupAsync reads independently
    /// of that grid -- see that method's own doc comment.</summary>
    public List<Employee> GetSelectedEmployees() =>
        Departments
            .SelectMany(d => d.Employees)
            .Where(n => n.IsSelected)
            .Select(n => n.Employee)
            .ToList();

    // ----- Step 3: Calculate & Review (build-order step 8 -- 8.1/8.2's own review grid,
    // driven by CalculateReviewAsync below, plus 8.3's "Save Payroll Group") -----

    /// <summary>One row per employee CalculateReviewAsync computed, in the same order
    /// GetSelectedEmployees() itself returns them -- exactly what PayrollWizardDialog's own
    /// DataGrid shows for the Employee/Net Pay/Gross Pay/Deductions review grid, straight off
    /// PayrollResult's own EmployeeName/TotalGrossPay/TotalDeductions/NetPay rather than a
    /// wizard-specific row type, since PayrollResult already carries everything that grid
    /// needs -- same values PayrollSummaryView itself renders for one employee at a time on
    /// the Payroll tab, which is what the build-order plan's own checkpoint ("numbers match
    /// what the Payroll tab shows for the same employee/period today") is checking against.
    /// Cleared and rebuilt on every CalculateReviewAsync run rather than updated in place --
    /// same "fresh set swapped in" convention PayrollGroupRow's own doc comment
    /// describes for RefreshPayrollGroupRowsAsync -- there's nothing here to preserve
    /// between runs since a person can't edit anything on this grid directly.</summary>
    public ObservableCollection<PayrollResult> ReviewResults { get; } = new();

    [ObservableProperty]
    private bool isCalculating;

    /// <summary>Notifies IsReviewReady (see that property's own doc comment) alongside the
    /// same Back/Next/Save re-gating OnIsSavingChanged already does for IsSaving below --
    /// a calculation in flight blocks navigating away from Step 3 and blocks Save the same
    /// way a save in flight blocks navigating away and blocks a second Save.</summary>
    partial void OnIsCalculatingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReviewReady));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        SavePayrollGroupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Same "explain the actual reason inline" idea as PeriodValidationMessage/
    /// SaveErrorMessage elsewhere on this class -- null (nothing wrong) once
    /// CalculateReviewAsync finishes cleanly. Cleared at the start of every
    /// CalculateReviewAsync attempt, not just on success, so retrying (by clicking Back
    /// then Next again) doesn't leave a stale message on screen underneath a fresh
    /// one.</summary>
    [ObservableProperty]
    private string? calculationErrorMessage;

    partial void OnCalculationErrorMessageChanged(string? value) => OnPropertyChanged(nameof(IsReviewReady));

    /// <summary>True once there's an actual grid to show -- neither still calculating nor
    /// failed. What PayrollWizardDialog.xaml's DataGrid (and the totals strip under it)
    /// binds its own Visibility to, alongside the "Calculating…" pulse (IsCalculating) and
    /// the error text (CalculationErrorMessage) each binding to their own condition
    /// instead -- the three are mutually exclusive by construction, since
    /// CalculateReviewAsync always leaves exactly one of "still running",
    /// "CalculationErrorMessage set", or "ReviewResults populated" true when it
    /// finishes.</summary>
    public bool IsReviewReady => !IsCalculating && CalculationErrorMessage is null;

    /// <summary>Sum of every row's own NetPay/TotalGrossPay/TotalDeductions -- the totals
    /// strip under the grid. Plain decimal.Sum over ReviewResults rather than an
    /// [ObservableProperty] of its own, since nothing ever sets these directly; the
    /// OnPropertyChanged calls at the end of CalculateReviewAsync below are what tell the
    /// view to re-read them once ReviewResults has actually finished changing, the same
    /// "batch the notification, not the individual Adds" idea NotifyEmployeeSelectionChanged
    /// already applies to the Step 2 tree.</summary>
    public decimal TotalNetPay => ReviewResults.Sum(r => r.NetPay);

    public decimal TotalGrossPay => ReviewResults.Sum(r => r.TotalGrossPay);

    public decimal TotalDeductions => ReviewResults.Sum(r => r.TotalDeductions);

    /// <summary>Build-order steps 8.1/8.2: IPayrollComputationService.PrepareBatchAsync once
    /// for every employee GetSelectedEmployees() currently returns, then
    /// ComputeOneFromBatchAsync per employee against that shared result -- same
    /// batch-then-per-employee shape PayrollPrintExportViewModel.PrintPayslipsAsync/
    /// ExportPayrollReportAsync use (see PayrollBatchContext's own doc comment for why
    /// looping ComputeOneAsync here instead used to repeat a company-wide attendance fetch
    /// once per selected employee, which matters more here than almost anywhere else in
    /// Payroll: this runs on every single visit to Step 3, including Back-then-Next with no
    /// selection change at all) -- and populates ReviewResults from it. Called once, automatically, from NextAsync the
    /// moment Step 3 is reached (see that method's own doc comment), and again every time
    /// Step 3 is reached afterward (Back to Step 2, change who's checked, Next again) --
    /// nothing here is cached across visits, same "recomputed live every time, nothing on
    /// PayrollResult is itself persisted" convention that class's own doc comment describes
    /// for the Payroll tab itself, so a changed selection is always what Step 3 actually
    /// shows rather than a stale grid from an earlier visit.
    ///
    /// No CancellationToken threaded through the loop -- same as LoadEmployeeTreeAsync
    /// above, this dialog has no shared busy state to hand one down from (see this class's
    /// own doc comment) -- CanGoBack/CanGoNext blocking navigation while IsCalculating is
    /// what stands in for cancellation instead of an actual CancellationTokenSource.
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
            OnPropertyChanged(nameof(TotalNetPay));
            OnPropertyChanged(nameof(TotalGrossPay));
            OnPropertyChanged(nameof(TotalDeductions));
        }
    }

    [ObservableProperty]
    private bool isSaving;

    partial void OnIsSavingChanged(bool value) => SavePayrollGroupCommand.NotifyCanExecuteChanged();

    /// <summary>Null until SavePayrollGroupCommand succeeds; the created PayrollRun
    /// afterward, Id/CreatedAt included (see IPayrollRunRepository.CreateAsync). Not an
    /// [ObservableProperty] since nothing here binds to it directly -- SavedRunSummary
    /// below is what the view actually shows, and both are raised together from the
    /// command. Build-order step 9.2 reads this off PayrollWizardDialog's own passthrough
    /// once Finish needs to carry it back to PayrollRunViewModel; nothing outside this class
    /// reads it yet.</summary>
    public PayrollRun? SavedRun { get; private set; }

    /// <summary>The exact Employee objects SavedRun.Employees was built from -- captured
    /// once, at the moment SavePayrollGroupCommand succeeds, rather than left as a live
    /// GetSelectedEmployees() read. Nothing stops a person from clicking Back to Step 2 and
    /// changing the tree after saving -- CanSavePayrollGroup blocks a second Save once
    /// SavedRun is set, but not Back itself -- so without this, PayrollWizardDialog.
    /// SelectedEmployees (and therefore PayrollRunViewModel.NewPayrollRun's own
    /// BatchScopeEmployees/checklist) could silently diverge from what's actually sitting in
    /// the PayrollRunEmployees table: the in-session checklist would show whatever's checked
    /// right now, while a later "Load Payroll Group…" of this same run would show what was
    /// saved. Freezing the selection here the moment it's written keeps both readings of
    /// "this run's employees" identical, regardless of anything Step 2 does afterward.</summary>
    public IReadOnlyList<Employee>? SavedEmployees { get; private set; }

    /// <summary>Plain success line shown under the Save button once SavedRun is set --
    /// null (hidden) beforehand. A separate string rather than binding straight to
    /// SavedRun.Label so the view's NotNullToVisibility converter has something that's
    /// actually null until there's something to show.</summary>
    public string? SavedRunSummary =>
        SavedRun is { } run ? $"Saved as \"{run.Label}\" (run #{run.Id})." : null;

    /// <summary>Same "explain the actual reason inline" idea as PeriodValidationMessage
    /// above, shown under the Save button instead of under the period fields. Null once
    /// there's nothing wrong -- cleared at the start of every SavePayrollGroupAsync
    /// attempt, not just on success, so retrying after a failure doesn't leave the old
    /// message on screen underneath a fresh one.</summary>
    [ObservableProperty]
    private string? saveErrorMessage;

    /// <summary>Builds and persists one PayrollRun from this step's own Label/PeriodStart/
    /// PeriodEnd (Step 1) and GetSelectedEmployees() (Step 2) -- deliberately not from
    /// ReviewResults, even though that grid is on screen right above this button by the
    /// time it's clickable (see CalculateReviewAsync's own doc comment for why the two stay
    /// independent); the membership this saves is exactly what Step 2's tree has checked,
    /// the same set PrintPayslipsAsync's own batch loop would compute over. CreatedBy
    /// defaults to Environment.UserName, same no-user-
    /// system convention as PayrollAdjustment.EnteredBy (see PayrollRun.CreatedBy's own
    /// doc comment). Deliberately does nothing to Finish/RequestClose -- this is its own
    /// button, separate from the wizard's own navigation, so build-order step 9's
    /// PayrollPage wiring can be tested independently of it (see CanSavePayrollGroup for
    /// why a second click is a no-op rather than a second row).
    ///
    /// GetSelectedEmployees() is read exactly once, into selectedEmployees, and that same
    /// list backs both run.Employees (as Pins) and SavedEmployees (as Employee objects) --
    /// see SavedEmployees' own doc comment for why capturing it here, instead of leaving
    /// PayrollWizardDialog.SelectedEmployees as a live re-read, matters.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePayrollGroup))]
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
                Employees = selectedEmployees
                    .Select(e => new PayrollRunEmployee { EmployeeId = e.Pin })
                    .ToList(),
            };

            // No CancellationToken here (unlike PayrollViewModel's own _busy.RunAsync-wrapped
            // commands) -- this dialog has no shared busy state to hand one down from, and a
            // single CreateAsync round trip has nothing worth cancelling mid-flight the way a
            // per-employee seed/compute loop would.
            SavedRun = await _payrollRunRepository.CreateAsync(run);
            SavedEmployees = selectedEmployees;
            OnPropertyChanged(nameof(SavedRun));
            OnPropertyChanged(nameof(SavedEmployees));
            OnPropertyChanged(nameof(SavedRunSummary));
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

    /// <summary>Blocks a second save once SavedRun is already set -- IPayrollRunRepository
    /// has no update/append (see that interface's own doc comment: re-running the wizard
    /// makes a new run rather than editing an old one), so a second click here would just
    /// create a duplicate row rather than doing anything useful. Also requires at least
    /// one selected employee actually has a Pin (same filter GetSelectedEmployees()
    /// itself applies) -- in practice always true by the time Step 3 is reachable, since
    /// CanGoNext already required SelectedEmployeeCount > 0 to leave Step 2, but Pin-less
    /// employees don't count toward that, so this stays defensive rather than assuming.
    /// The added !IsCalculating check just keeps Save from being clickable while
    /// CalculateReviewAsync is still filling in the grid above it -- Save itself doesn't
    /// read ReviewResults (see its own doc comment), so this isn't about correctness, just
    /// keeping the button's own enabled state in step with what's actually on screen.
    /// </summary>
    private bool CanSavePayrollGroup() =>
        !IsSaving && !IsCalculating && SavedRun is null && GetSelectedEmployees().Count > 0;
}
