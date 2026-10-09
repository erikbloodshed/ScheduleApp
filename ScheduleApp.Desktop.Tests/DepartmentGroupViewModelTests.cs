using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class DepartmentGroupViewModelTests
{
    private readonly DepartmentGroupViewModel _group;

    public DepartmentGroupViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _group = new DepartmentGroupViewModel
        {
            Name = "Bakery",
            Employees = [Node(1), Node(2), Node(3)],
        };
        _group.AttachChildNotifications();
    }

    private static EmployeeNodeViewModel Node(int pin) =>
        new() { Employee = new Employee { Pin = pin, LastName = $"L{pin}", FirstName = $"F{pin}" } };

    [Fact]
    public void Starts_unselected_with_every_employee_visible()
    {
        Assert.False(_group.IsSelected);
        Assert.Equal(3, _group.VisibleEmployees.Count);
    }

    [Fact]
    public void Checking_the_group_checks_every_employee_and_unchecking_clears_them()
    {
        _group.IsSelected = true;
        Assert.All(_group.Employees, e => Assert.True(e.IsSelected));
        Assert.True(_group.IsSelected);

        _group.IsSelected = false;
        Assert.All(_group.Employees, e => Assert.False(e.IsSelected));
        Assert.False(_group.IsSelected);
    }

    [Fact]
    public void The_group_follows_its_employees()
    {
        _group.Employees[0].IsSelected = true;
        Assert.Null(_group.IsSelected);

        _group.Employees[1].IsSelected = true;
        _group.Employees[2].IsSelected = true;
        Assert.True(_group.IsSelected);

        _group.Employees[1].IsSelected = false;
        Assert.Null(_group.IsSelected);
        Assert.True(_group.Employees[0].IsSelected);
        Assert.True(_group.Employees[2].IsSelected);
    }

    [Fact]
    public void Checking_a_partly_checked_group_checks_the_rest()
    {
        _group.Employees[0].IsSelected = true;

        _group.IsSelected = true;

        Assert.All(_group.Employees, e => Assert.True(e.IsSelected));
        Assert.True(_group.IsSelected);
    }

    [Fact]
    public void Visible_employees_follow_IsVisible_on_refresh()
    {
        _group.Employees[1].IsVisible = false;
        _group.RefreshVisibleEmployees();

        Assert.Equal([1, 3], _group.VisibleEmployees.Select(e => e.Employee.Pin));
    }
}
