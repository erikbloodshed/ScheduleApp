using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Models;

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
public partial class PayslipScopeViewModel : ObservableObject
{
    /// <summary>Replaces the raw IScheduleRepository this class used to read
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync through
    /// directly -- see ActiveRosterProvider's own doc comment for why this tree's load now
    /// goes through the shared, RosterVersion-gated cache instead, the same swap
    /// ReportScopeViewModel/PayrollWizardViewModel make for their own, identically-shaped
    /// tree loads.</summary>
    private readonly ActiveRosterProvider _rosterProvider;

    public PayslipScopeViewModel(ActiveRosterProvider rosterProvider)
    {
        _rosterProvider = rosterProvider;
    }

    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = new();

    /// <summary>Same comma-separated Employee ID/first name/last name/department
    /// matching as ReportScopeViewModel.SearchText -- see
    /// <see cref="EmployeeTreeSearchFilter"/>, shared between both.</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => EmployeeTreeSearchFilter.Apply(Departments, value);

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
    [RelayCommand]
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

        foreach (var group in EmployeeTreeBuilder.Build(departments, unassigned, OnEmployeeNodeSelectionChanged))
            Departments.Add(group);

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

        OnPropertyChanged(nameof(SelectedEmployeeCount));
        OnPropertyChanged(nameof(SelectionScopeText));
        SelectAllTreeCommand.NotifyCanExecuteChanged();
        ClearTreeSelectionCommand.NotifyCanExecuteChanged();

        EmployeeTreeSearchFilter.Apply(Departments, SearchText);
    }

    private void OnEmployeeNodeSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EmployeeNodeViewModel.IsSelected)) return;

        OnPropertyChanged(nameof(SelectedEmployeeCount));
        OnPropertyChanged(nameof(SelectionScopeText));
        SelectAllTreeCommand.NotifyCanExecuteChanged();
        ClearTreeSelectionCommand.NotifyCanExecuteChanged();
    }

    public int SelectedEmployeeCount => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    public int TotalEmployeeCount => Departments.Sum(d => d.Employees.Count);

    /// <summary>Same wording/logic as ReportScopeViewModel.SelectionScopeText -- see
    /// that property's own doc comment.</summary>
    public string SelectionScopeText
    {
        get
        {
            int total = SelectedEmployeeCount;
            int all = TotalEmployeeCount;

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
        Departments
            .SelectMany(d => d.Employees)
            .Where(n => n.IsSelected)
            .Select(n => n.Employee)
            .ToList();
}