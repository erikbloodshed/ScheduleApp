using NSubstitute;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Tests;

/// <summary>
/// A small active roster for the employee-tree tests: Bakery (Pins 1, 2), Kitchen (3, 4, 5),
/// and one unassigned employee (6), served through a stubbed IScheduleRepository and a real
/// ActiveRosterProvider over it.
/// </summary>
internal sealed class TestRoster
{
    public TestRoster()
    {
        Bakery = Department(1, "Bakery", Employee(1, "Alcantara"), Employee(2, "Bautista"));
        Kitchen = Department(2, "Kitchen", Employee(3, "Cruz"), Employee(4, "Dizon"), Employee(5, "Espino"));
        Unassigned = [Employee(6, "Flores")];

        Repository.GetActiveDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Department> { Bakery, Kitchen }));
        Repository.GetActiveUnassignedEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Unassigned));
        Provider = new ActiveRosterProvider(Repository, DataVersion);
    }

    public IScheduleRepository Repository { get; } = Substitute.For<IScheduleRepository>();
    public AttendanceDataVersion DataVersion { get; } = new();
    public ActiveRosterProvider Provider { get; }

    public Department Bakery { get; }
    public Department Kitchen { get; }
    public List<Employee> Unassigned { get; }

    public IEnumerable<Employee> Everyone => [.. Bakery.Employees, .. Kitchen.Employees, .. Unassigned];

    public static Employee Employee(int pin, string lastName) =>
        new() { Id = pin * 10, Pin = pin, LastName = lastName, FirstName = "Juan" };

    private static Department Department(int id, string name, params Employee[] employees)
    {
        var department = new Department { Id = id, Name = name };
        foreach (var employee in employees)
        {
            employee.DepartmentId = id;
            employee.Department = department;
            department.Employees.Add(employee);
        }

        return department;
    }

    /// <summary>The tree node for <paramref name="pin"/> in <paramref name="tree"/>.</summary>
    public static EmployeeNodeViewModel Node(IEnumerable<DepartmentGroupViewModel> tree, int pin) =>
        tree.SelectMany(d => d.Employees).Single(n => n.Employee.Pin == pin);
}
