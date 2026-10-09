using System.Reactive.Linq;
using ReactiveUI.Binding;
using ScheduleApp.Core.Models;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Builds the Department/Employee checkbox tree shared by the Schedule tab's
/// multi-select tree and the Attendance tab's report-scope tree. Both trees
/// wrap the same repository data in the same DepartmentGroupViewModel/
/// EmployeeNodeViewModel shapes and wire up the same parent/child checkbox
/// sync -- only what each tab *does* with a checked employee differs, not how
/// the tree itself is put together, so that part lives here once instead of
/// twice.
/// </summary>
internal static class EmployeeTreeBuilder
{
    public const string UnassignedGroupName = "(Unassigned)";

    /// <summary>One DepartmentGroupViewModel per real department (employees sorted
    /// by LastName) plus a trailing "(Unassigned)" group if there are any employees
    /// with no department yet. A caller reacts to its checkboxes through
    /// <see cref="SelectionChanges"/>.</summary>
    public static List<DepartmentGroupViewModel> Build(
        IEnumerable<Department> departments,
        IEnumerable<Employee> unassignedEmployees)
    {
        var groups = new List<DepartmentGroupViewModel>();

        foreach (var department in departments)
            groups.Add(BuildGroup(department.Name, department, department.Employees.OrderBy(e => e.LastName)));

        var unassignedList = unassignedEmployees.ToList();
        if (unassignedList.Count > 0)
            groups.Add(BuildGroup(UnassignedGroupName, null, unassignedList));

        return groups;
    }

    /// <summary>
    /// One value each time any employee's checkbox in <paramref name="tree"/> changes -- not
    /// for the values they hold when this is subscribed. A department's own checkbox is
    /// already current by then: it follows its employees through a subscription made when
    /// the tree was built, which runs first.
    ///
    /// A caller whose tree is rebuilt on each load switches to the new tree's changes
    /// (Select(SelectionChanges).Switch()), so the discarded tree's nodes stop counting.
    /// </summary>
    public static IObservable<RxVoid> SelectionChanges(IEnumerable<DepartmentGroupViewModel> tree) =>
        tree.SelectMany(g => g.Employees)
            .Select(node => node.WhenAnyValue(n => n.IsSelected).Skip(1).Select(_ => RxVoid.Default))
            .Merge();

    private static DepartmentGroupViewModel BuildGroup(
        string name,
        Department? realDepartment,
        IEnumerable<Employee> employees)
    {
        var group = new DepartmentGroupViewModel
        {
            Name = name,
            RealDepartment = realDepartment,
            Employees = [.. employees.Select(e => new EmployeeNodeViewModel { Employee = e })]
        };

        group.AttachChildNotifications();
        return group;
    }
}
