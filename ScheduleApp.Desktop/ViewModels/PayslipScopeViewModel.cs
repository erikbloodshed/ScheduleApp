using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ScheduleApp.Core.Models;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs <c>PayslipScopeDialog</c>'s Department/Employee checkbox tree --
/// adapted from <see cref="Attendance.ReportScopeViewModel"/> (see
/// Print_Feature.md's own UI section: "Print Payslips…" opens a dialog with a
/// department/employee checkbox tree "adapted from ReportScopeViewModel") for
/// the one place this differs: there's no ViewStateStore-backed "remember the
/// last scope across sessions" -- Print_Feature.md never asks for that, and a
/// print run is a one-off action a person takes and then closes the dialog,
/// unlike the Attendance tab's report scope, which stays open on-screen and
/// re-runs live as the tree changes. Otherwise the exact same tree shape,
/// checkbox behavior (checking a department checks every employee in it;
/// checking/unchecking individuals narrows further; every employee starts
/// checked, representing the whole company, since printing everyone's
/// payslip each payroll cycle is the common case, same "zero-effort default"
/// reasoning), and live search filter as ReportScopeViewModel -- see that
/// class's own doc comment for the full behavioral rundown, all of which
/// applies here unchanged.
///
/// Not registered in DI, same as ReportScopeViewModel isn't -- constructed
/// directly by PayslipScopeDialog's own constructor (see
/// AttendanceViewModel's constructor for the precedent: `new
/// ReportScopeViewModel(rosterProvider, viewStateStore)`), which receives
/// ActiveRosterProvider from whichever ViewModel opens the dialog
/// (PayrollViewModel's PrintExport/Run children, or Schedule's own ImportExport).
/// </summary>
public partial class PayslipScopeViewModel : ReactiveViewModel
{
    /// <summary>Replaces the raw IScheduleRepository this class used to read
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync through
    /// directly -- see ActiveRosterProvider's own doc comment for why this tree's load now
    /// goes through the shared, RosterVersion-gated cache instead, the same swap
    /// ReportScopeViewModel/PayrollWizardViewModel make for their own, identically-shaped
    /// tree loads.</summary>
    private readonly ActiveRosterProvider _rosterProvider;

    private readonly IObservable<bool> _canSelectAllTree;
    private readonly IObservable<bool> _canClearTreeSelection;

    /// <summary>Passed through to GetSelectedEmployees -- see that method's own doc comment.</summary>
    private readonly bool _requirePin;

    public PayslipScopeViewModel(ActiveRosterProvider rosterProvider, bool requirePin = true)
    {
        _rosterProvider = rosterProvider;
        _requirePin = requirePin;

        // Something about the selection may have moved: a new tree arrived, or one of the
        // current tree's checkboxes changed. A discarded tree's checkboxes no longer count.
        var selectionMoved = Observable.Switch(this.WhenAnyValue(x => x.LoadedTree)
            .Select(tree => EmployeeTreeBuilder.SelectionChanges(tree).StartWith(RxVoid.Default)));

        _totalEmployeeCountHelper = this.WhenAnyValue(x => x.LoadedTree)
            .Select(tree => tree.Sum(d => d.Employees.Count))
            .ToProperty(this, x => x.TotalEmployeeCount);
        _selectedEmployeeCountHelper = selectionMoved
            .Select(_ => CountSelected())
            .ToProperty(this, x => x.SelectedEmployeeCount);
        // Recomputed on every checkbox change, not just on a new count: unchecking one
        // employee and checking another keeps the count but can change which departments
        // are fully checked.
        _selectionScopeTextHelper = selectionMoved
            .Select(_ => DescribeSelection())
            .ToProperty(this, x => x.SelectionScopeText);

        _canSelectAllTree = this.WhenAnyValue(x => x.SelectedEmployeeCount, x => x.TotalEmployeeCount,
            (selected, total) => selected < total);
        _canClearTreeSelection = this.WhenAnyValue(x => x.SelectedEmployeeCount).Select(selected => selected > 0);

        this.WhenAnyValue(x => x.SearchText)
            .Skip(1)
            .Subscribe(searchText => EmployeeTreeSearchFilter.Apply(Departments, searchText, VisibleDepartments));
    }

    /// <summary>The dialog's window title -- the only things that differ between its callers
    /// are this, the description above the period pickers, and the confirm button's caption and
    /// tooltip (see PayslipScopeDialog's own doc comment).</summary>
    public string Title { get; init; } = "Print Payslips";

    public string Description { get; init; } = string.Empty;

    public string ConfirmText { get; init; } = "Print…";

    public string ConfirmToolTip { get; init; } = string.Empty;

    /// <summary>The employees to start checked, or null for everyone -- what the dialog loads
    /// the tree with when it opens (see LoadEmployeeTreeAsync).</summary>
    public IReadOnlyCollection<Employee>? PresetSelection { get; init; }

    /// <summary>The period the run covers. Its opener starts it at the Payroll tab's current
    /// period; either end may be cleared in the pickers, which Accept turns away.</summary>
    [Reactive]
    public partial DateTime? PeriodStart { get; set; }

    [Reactive]
    public partial DateTime? PeriodEnd { get; set; }

    /// <summary>What Accept settled on -- null until it succeeds.</summary>
    public PayslipScope? AcceptedScope { get; private set; }

    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = [];

    /// <summary>The tree LoadEmployeeTreeAsync last built, once it's in Departments -- what
    /// the selection counts and scope text follow.</summary>
    [Reactive]
    internal partial IReadOnlyList<DepartmentGroupViewModel> LoadedTree { get; private set; } = [];

    /// <summary>The departments SearchText hasn't hidden, each showing its VisibleEmployees --
    /// what this dialog's SfTreeView binds to (see EmployeeTreeSearchFilter.Apply).</summary>
    public ObservableCollection<DepartmentGroupViewModel> VisibleDepartments { get; } = [];

    /// <summary>Same comma-separated Employee ID/first name/last name/department
    /// matching as ReportScopeViewModel.SearchText -- see
    /// <see cref="EmployeeTreeSearchFilter"/>, shared between both.</summary>
    [Reactive]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>presetSelection is null for both the "whole company" default described in
    /// this class's own doc comment (PayslipScopeDialog's own default, used any time there's
    /// no active payroll group) and for the wizard/report-scope trees that never call this
    /// overload at all. Non-null only when PayrollPrintExportViewModel.PrintPayslipsAsync/
    /// ExportPayrollReportAsync have an active batch (HasActiveBatchScope) to hand in --
    /// build-order step 11 -- in which case only those employees start checked instead of
    /// everyone. Matched by Employee.Id rather than reference equality: presetSelection's
    /// Employee instances come from BatchScopeEmployees, a different repository round trip
    /// than the tree this method just built, so they're never the same objects even when
    /// they represent the same row.</summary>
    [ReactiveCommand]
    private async Task LoadEmployeeTreeAsync(IReadOnlyCollection<Employee>? presetSelection)
    {
        Departments.Clear();

        // Active-only, same pair ReportScopeViewModel/PayrollWizardViewModel's own tree
        // loads use -- matters more here than it does for either of those: a blacklisted
        // employee's node would still be *hidden* from the tree by EmployeeTreeSearchFilter
        // (below), but that only sets IsVisible, and the "whole company" default just below
        // (presetSelection is null -- the common case, since neither "Print Payslips…" nor
        // "Export Payroll Report…" pass one unless a batch group is already active) checks
        // every node in Departments regardless of IsVisible. Querying Active-only means a
        // blacklisted employee's node never exists here to be checked in the first place,
        // rather than relying on the tree's own visual filtering to keep them out of
        // GetSelectedEmployees() -- the same "don't fetch them at all" approach
        // ReportScopeViewModel/PayrollWizardViewModel already take, for the same reason.
        var (departments, unassigned) = await _rosterProvider.GetAsync();

        var tree = EmployeeTreeBuilder.Build(departments, unassigned);
        foreach (var group in tree)
            Departments.Add(group);
        LoadedTree = tree;

        if (presetSelection is null)
        {
            // Every employee (and therefore every department) starts checked -- see this
            // class's own doc comment for why.
            foreach (var node in Departments.SelectMany(d => d.Employees))
                node.IsSelected = true;
        }
        else
        {
            var presetIds = presetSelection.Select(e => e.Id).ToHashSet();
            foreach (var node in Departments.SelectMany(d => d.Employees))
                node.IsSelected = presetIds.Contains(node.Employee.Id);
        }

        EmployeeTreeSearchFilter.Apply(Departments, SearchText, VisibleDepartments);
    }

    [ObservableAsProperty]
    public partial int SelectedEmployeeCount { get; }

    [ObservableAsProperty]
    public partial int TotalEmployeeCount { get; }

    /// <summary>Same wording/logic as ReportScopeViewModel.SelectionScopeText -- see
    /// that property's own doc comment.</summary>
    [ObservableAsProperty(InitialValue = "No employees loaded yet")]
    public partial string SelectionScopeText { get; }

    private int CountSelected() => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    private string DescribeSelection()
    {
        int total = CountSelected();
        int all = Departments.Sum(d => d.Employees.Count);

        if (all == 0) return "No employees loaded yet";
        if (total == all) return "Whole company (all employees checked)";
        if (total == 0) return "Nothing selected -- check at least one employee to print";

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

    /// <summary>The dialog's confirm button (Print/Export): checks there's a whole, forward
    /// period and at least one employee checked, and if so settles AcceptedScope and reports
    /// true, which closes the dialog. Otherwise it says what's missing (Notify) and reports
    /// false, leaving the dialog open.</summary>
    [ReactiveCommand]
    private async Task<bool> AcceptAsync()
    {
        if (PeriodStart?.Date is not DateTime start || PeriodEnd?.Date is not DateTime end)
            return await RefuseAsync("Choose a period start and end date.");

        if (end < start)
            return await RefuseAsync("Period end can't be before period start.");

        var selected = GetSelectedEmployees(_requirePin);
        if (selected.Count == 0)
            return await RefuseAsync("Check at least one employee to continue.");

        AcceptedScope = new PayslipScope(selected, start, end);
        return true;
    }

    private async Task<bool> RefuseAsync(string message)
    {
        await NotifyAsync(message, "Check your selection", NoticeKind.Warning);
        return false;
    }

    /// <summary>Every currently-checked employee -- unlike ReportScopeViewModel.
    /// GetSelectedPins, always the explicit resolved list (never null-meaning-
    /// "everyone"): the two original callers (PayrollCalculator.Calculate)
    /// need the actual Employee (for PayrollResult.EmployeeName/EmployeeId), not
    /// just a pin, so there's no lighter-weight "everyone" shortcut to return
    /// here the way a pins-only attendance query has -- PayslipScopeDialog's
    /// caller always gets a concrete list to loop over.
    /// requirePin no longer filters anything -- every employee has a Pin now (see
    /// Employee.Pin's own doc comment), so there's no "checked but un-Pinned" employee
    /// left to drop. Kept as a parameter rather than removed outright so
    /// PayslipScopeDialog's own _requirePin field doesn't need touching for what's now a
    /// no-op distinction.</summary>
    public List<Employee> GetSelectedEmployees(bool requirePin = true) =>
        [.. Departments
            .SelectMany(d => d.Employees)
            .Where(n => n.IsSelected)
            .Select(n => n.Employee)];
}

/// <summary>What PayslipScopeDialog hands back once accepted: who, and for which period.</summary>
public sealed record PayslipScope(List<Employee> Employees, DateTime PeriodStart, DateTime PeriodEnd);
