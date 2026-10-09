using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ScheduleApp.Core.Models;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// The picker for adding employees to an already-saved payroll group: the same
/// DepartmentGroupViewModel/EmployeeNodeViewModel checkbox tree as the wizard's Step 2 and
/// PayslipScopeViewModel, built by the caller from the roster it has already loaded -- no live
/// repository read in here.
///
/// Employees already in the group start checked so the person can see who's already a member;
/// <see cref="Accept"/> hands back everyone checked, existing members included, and
/// PayrollGroupViewModel.AddEmployeesToGroupAsync filters those out -- so this picker's job stays
/// simple: show the tree, return whoever's checked.
/// </summary>
public partial class AddToPayrollGroupViewModel : ReactiveViewModel
{
    private readonly IReadOnlyList<DepartmentGroupViewModel> _departments;

    /// <param name="departments">The roster tree, from EmployeeTreeBuilder.Build.</param>
    /// <param name="currentMembers">Who's already in the group -- matched by Employee.Id, since
    /// they come from a different roster read than the tree's.</param>
    public AddToPayrollGroupViewModel(IReadOnlyList<DepartmentGroupViewModel> departments, IReadOnlyCollection<Employee> currentMembers)
    {
        _departments = departments;

        var currentIds = currentMembers.Select(e => e.Id).ToHashSet();
        foreach (var department in departments)
        {
            // Start expanded, so the whole roster shows without clicking each department open.
            department.IsExpanded = true;
            foreach (var node in department.Employees)
                node.IsSelected = currentIds.Contains(node.Employee.Id);
        }

        EmployeeTreeSearchFilter.Apply(_departments, string.Empty, VisibleDepartments);
        this.WhenAnyValue(x => x.SearchText)
            .Skip(1)
            .Subscribe(searchText => EmployeeTreeSearchFilter.Apply(_departments, searchText, VisibleDepartments));

        _scopeTextHelper = EmployeeTreeBuilder.SelectionChanges(departments)
            .StartWith(RxVoid.Default)
            .Select(_ => DescribeChecked(_departments.SelectMany(d => d.Employees).Count(n => n.IsSelected)))
            .ToProperty(this, x => x.ScopeText);
    }

    /// <summary>The departments the search hasn't hidden -- the tree's items, since SfTreeView
    /// can't hide a row (see EmployeeTreeSearchFilter.Apply).</summary>
    public ObservableCollection<DepartmentGroupViewModel> VisibleDepartments { get; } = [];

    /// <summary>Same comma-separated ID/name/department filter as the wizard's Step 2.</summary>
    [Reactive]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableAsProperty]
    public partial string ScopeText { get; }

    /// <summary>Everyone checked once Add Selected was clicked; null until then.</summary>
    public IReadOnlyList<Employee>? CheckedEmployees { get; private set; }

    /// <summary>Add Selected: settles <see cref="CheckedEmployees"/>.</summary>
    [ReactiveCommand]
    private void Accept() =>
        CheckedEmployees = [.. _departments.SelectMany(d => d.Employees).Where(n => n.IsSelected).Select(n => n.Employee)];

    private static string DescribeChecked(int count) => count switch
    {
        0 => "No employees checked.",
        1 => "1 employee checked.",
        _ => $"{count} employees checked.",
    };
}
