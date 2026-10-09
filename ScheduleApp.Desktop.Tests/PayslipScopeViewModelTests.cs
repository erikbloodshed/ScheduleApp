using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Input;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class PayslipScopeViewModelTests
{
    private readonly TestRoster _roster = new();
    private readonly PayslipScopeViewModel _vm;

    public PayslipScopeViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _vm = new PayslipScopeViewModel(_roster.Provider);
    }

    private async Task LoadAsync(IReadOnlyCollection<Employee>? preset = null) => await _vm.LoadEmployeeTreeCommand.Execute(preset);

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    [Fact]
    public void Before_loading_nothing_is_loaded()
    {
        Assert.Equal(0, _vm.SelectedEmployeeCount);
        Assert.Equal(0, _vm.TotalEmployeeCount);
        Assert.Equal("No employees loaded yet", _vm.SelectionScopeText);
        Assert.False(CanExecute(_vm.SelectAllTreeCommand));
        Assert.False(CanExecute(_vm.ClearTreeSelectionCommand));
    }

    [Fact]
    public async Task Loading_checks_everyone_by_default()
    {
        await LoadAsync();

        Assert.Equal(["Bakery", "Kitchen", "(Unassigned)"], _vm.Departments.Select(d => d.Name));
        Assert.Equal(6, _vm.SelectedEmployeeCount);
        Assert.Equal(6, _vm.TotalEmployeeCount);
        Assert.Equal("Whole company (all employees checked)", _vm.SelectionScopeText);
        Assert.False(CanExecute(_vm.SelectAllTreeCommand));
        Assert.True(CanExecute(_vm.ClearTreeSelectionCommand));
        Assert.Equal(3, _vm.VisibleDepartments.Count);
    }

    [Fact]
    public async Task A_preset_checks_only_those_employees_matched_by_id()
    {
        // Different instances from the tree's own, as the batch scope's are.
        await LoadAsync([TestRoster.Employee(3, "Cruz"), TestRoster.Employee(4, "Dizon")]);

        Assert.Equal(2, _vm.SelectedEmployeeCount);
        Assert.Equal([3, 4], _vm.GetSelectedEmployees().Select(e => e.Pin));
        Assert.Equal("2 employees selected", _vm.SelectionScopeText);
    }

    [Fact]
    public async Task Scope_text_names_fully_checked_departments()
    {
        await LoadAsync();
        await _vm.ClearTreeSelectionCommand.Execute();
        Assert.Equal("Nothing selected -- check at least one employee to print", _vm.SelectionScopeText);

        _vm.Departments[1].IsSelected = true;
        Assert.Equal("Department: Kitchen", _vm.SelectionScopeText);

        _vm.Departments[0].IsSelected = true;
        Assert.Equal("2 departments: Bakery, Kitchen", _vm.SelectionScopeText);

        TestRoster.Node(_vm.Departments, 1).IsSelected = false;
        Assert.Equal("4 employees selected", _vm.SelectionScopeText);
    }

    [Fact]
    public async Task Scope_text_follows_a_swap_that_keeps_the_count()
    {
        await LoadAsync();
        await _vm.ClearTreeSelectionCommand.Execute();
        _vm.Departments[0].IsSelected = true;          // Bakery: 2 employees
        Assert.Equal("Department: Bakery", _vm.SelectionScopeText);

        TestRoster.Node(_vm.Departments, 1).IsSelected = false;
        TestRoster.Node(_vm.Departments, 6).IsSelected = true;   // still 2 checked

        Assert.Equal(2, _vm.SelectedEmployeeCount);
        Assert.Equal("2 employees selected", _vm.SelectionScopeText);
    }

    [Fact]
    public async Task Select_all_and_clear_follow_the_count()
    {
        await LoadAsync();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)_vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        TestRoster.Node(_vm.Departments, 2).IsSelected = false;
        Assert.Contains(nameof(PayslipScopeViewModel.SelectedEmployeeCount), raised);
        Assert.Contains(nameof(PayslipScopeViewModel.SelectionScopeText), raised);
        Assert.True(CanExecute(_vm.SelectAllTreeCommand));

        await _vm.SelectAllTreeCommand.Execute();
        Assert.Equal(6, _vm.SelectedEmployeeCount);
        Assert.False(CanExecute(_vm.SelectAllTreeCommand));

        await _vm.ClearTreeSelectionCommand.Execute();
        Assert.Equal(0, _vm.SelectedEmployeeCount);
        Assert.False(CanExecute(_vm.ClearTreeSelectionCommand));
        Assert.Empty(_vm.GetSelectedEmployees());
    }

    [Fact]
    public async Task Search_text_filters_the_visible_tree()
    {
        await LoadAsync();

        _vm.SearchText = "Dizon";

        var visible = Assert.Single(_vm.VisibleDepartments);
        Assert.Equal("Kitchen", visible.Name);
        Assert.Equal([4], visible.VisibleEmployees.Select(e => e.Employee.Pin));

        _vm.SearchText = "";
        Assert.Equal(3, _vm.VisibleDepartments.Count);
    }

    private List<string> CollectNotices()
    {
        var notices = new List<string>();
        _vm.Notify.RegisterHandler(ctx =>
        {
            notices.Add(ctx.Input.Message);
            ctx.SetOutput(default);
        });
        return notices;
    }

    [Fact]
    public async Task Accept_needs_a_whole_forward_period()
    {
        await LoadAsync();
        var notices = CollectNotices();

        _vm.PeriodStart = null;
        _vm.PeriodEnd = new DateTime(2026, 9, 30);
        Assert.False(await _vm.AcceptCommand.Execute());

        _vm.PeriodStart = new DateTime(2026, 10, 1);
        Assert.False(await _vm.AcceptCommand.Execute());

        Assert.Equal(["Choose a period start and end date.", "Period end can't be before period start."], notices);
        Assert.Null(_vm.AcceptedScope);
    }

    [Fact]
    public async Task Accept_needs_someone_checked()
    {
        await LoadAsync();
        var notices = CollectNotices();
        _vm.PeriodStart = new DateTime(2026, 9, 16);
        _vm.PeriodEnd = new DateTime(2026, 9, 30);
        await _vm.ClearTreeSelectionCommand.Execute();

        Assert.False(await _vm.AcceptCommand.Execute());
        Assert.Equal(["Check at least one employee to continue."], notices);
    }

    [Fact]
    public async Task Accept_settles_the_scope()
    {
        await LoadAsync([TestRoster.Employee(5, "Espino")]);
        _vm.PeriodStart = new DateTime(2026, 9, 16, 14, 30, 0);
        _vm.PeriodEnd = new DateTime(2026, 9, 30);

        Assert.True(await _vm.AcceptCommand.Execute());

        var scope = _vm.AcceptedScope!;
        Assert.Equal([5], scope.Employees.Select(e => e.Pin));
        Assert.Equal(new DateTime(2026, 9, 16), scope.PeriodStart);
        Assert.Equal(new DateTime(2026, 9, 30), scope.PeriodEnd);
    }

    [Fact]
    public async Task Reloading_tracks_the_new_tree_only()
    {
        await LoadAsync();
        var oldNode = TestRoster.Node(_vm.Departments, 1);

        await LoadAsync([TestRoster.Employee(1, "Alcantara")]);
        Assert.Equal(1, _vm.SelectedEmployeeCount);

        oldNode.IsSelected = false;     // a node from the discarded tree
        Assert.Equal(1, _vm.SelectedEmployeeCount);
        Assert.Equal("1 employee selected", _vm.SelectionScopeText);
    }
}
