using System.ComponentModel;
using ScheduleApp.Core.Models;

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
    /// with no department yet. onEmployeeSelectionChanged is attached to every
    /// employee node's PropertyChanged so the caller can react (recompute a count,
    /// re-evaluate a command's CanExecute, etc.) as checkboxes are toggled.</summary>
    public static List<DepartmentGroupViewModel> Build(
        IEnumerable<Department> departments,
        IEnumerable<Employee> unassignedEmployees,
        PropertyChangedEventHandler onEmployeeSelectionChanged)
    {
        var groups = new List<DepartmentGroupViewModel>();

        foreach (var department in departments)
        {
            groups.Add(BuildGroup(
                department.Name, department,
                department.Employees.OrderBy(e => e.LastName),
                onEmployeeSelectionChanged));
        }

        var unassignedList = unassignedEmployees.ToList();
        if (unassignedList.Count > 0)
        {
            groups.Add(BuildGroup(UnassignedGroupName, null, unassignedList, onEmployeeSelectionChanged));
        }

        return groups;
    }

    private static DepartmentGroupViewModel BuildGroup(
        string name,
        Department? realDepartment,
        IEnumerable<Employee> employees,
        PropertyChangedEventHandler onEmployeeSelectionChanged)
    {
        var group = new DepartmentGroupViewModel
        {
            Name = name,
            RealDepartment = realDepartment,
            Employees = employees.Select(e => new EmployeeNodeViewModel { Employee = e }).ToList()
        };

        foreach (var node in group.Employees)
            node.PropertyChanged += onEmployeeSelectionChanged;

        group.AttachChildNotifications();
        return group;
    }
}
