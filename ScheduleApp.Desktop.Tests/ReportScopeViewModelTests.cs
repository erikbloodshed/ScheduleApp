using System.Reactive.Linq;
using System.Windows.Input;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class ReportScopeViewModelTests : IDisposable
{
    private readonly TestRoster _roster = new();
    private readonly ViewStateStore _viewState = new();
    private readonly ReportScopeViewModel _vm;

    public ReportScopeViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _vm = new ReportScopeViewModel(_roster.Provider, _viewState);
    }

    public void Dispose() => _viewState.Dispose();

    private async Task LoadAsync() => await _vm.LoadEmployeeTreeCommand.Execute();

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    [Fact]
    public async Task Loads_everyone_checked_meaning_no_pin_filter()
    {
        Assert.Equal("No employees loaded yet", _vm.SelectionScopeText);

        await LoadAsync();

        Assert.Equal(6, _vm.SelectedEmployeeCount);
        Assert.Equal(6, _vm.TotalEmployeeCount);
        Assert.Equal(6, _vm.LoadedEmployees.Count);
        Assert.Null(_vm.GetSelectedPins());
        Assert.True(_vm.IsChecked(3));
        Assert.True(_vm.HasRestoredScope);
        Assert.Equal("Whole company (all employees checked)", _vm.SelectionScopeText);
    }

    [Fact]
    public async Task The_first_load_restores_the_saved_scope_and_a_reload_does_not()
    {
        _viewState.Attendance.SelectedPins = [3, 4, 5];

        await LoadAsync();
        Assert.Equal([3, 4, 5], _vm.GetSelectedPins()!.Order());
        Assert.Equal("Department: Kitchen", _vm.SelectionScopeText);
        Assert.False(_vm.IsChecked(1));

        await LoadAsync();
        Assert.Null(_vm.GetSelectedPins());
        Assert.Equal(6, _vm.SelectedEmployeeCount);
    }

    [Fact]
    public async Task Clearing_means_nothing_and_hides_every_row()
    {
        await LoadAsync();

        await _vm.ClearTreeSelectionCommand.Execute();

        Assert.Equal(0, _vm.SelectedEmployeeCount);
        Assert.Null(_vm.GetSelectedPins());
        Assert.False(_vm.IsChecked(1));
        Assert.Equal("Nothing selected -- check at least one employee to generate a report", _vm.SelectionScopeText);
        Assert.False(CanExecute(_vm.ClearTreeSelectionCommand));
        Assert.True(CanExecute(_vm.SelectAllTreeCommand));
    }

    [Fact]
    public async Task Every_checkbox_change_moves_the_selection_version_even_at_the_same_count()
    {
        await LoadAsync();
        await _vm.ClearTreeSelectionCommand.Execute();
        TestRoster.Node(_vm.Departments, 1).IsSelected = true;
        var before = _vm.SelectionVersion;

        TestRoster.Node(_vm.Departments, 1).IsSelected = false;
        TestRoster.Node(_vm.Departments, 6).IsSelected = true;

        Assert.Equal(before + 2, _vm.SelectionVersion);
        Assert.Equal([6], _vm.GetSelectedPins()!);
        // The one unassigned employee is that whole group.
        Assert.Equal("Department: (Unassigned)", _vm.SelectionScopeText);
    }

    [Fact]
    public async Task SelectionChanges_reports_a_same_count_swap_with_everything_already_current()
    {
        await LoadAsync();
        await _vm.ClearTreeSelectionCommand.Execute();
        TestRoster.Node(_vm.Departments, 1).IsSelected = true;
        var seen = new List<(int Version, int Count, bool SixChecked)>();
        using var _ = _vm.SelectionChanges.Subscribe(_ =>
            seen.Add((_vm.SelectionVersion, _vm.SelectedEmployeeCount, _vm.IsChecked(6))));
        var before = _vm.SelectionVersion;

        TestRoster.Node(_vm.Departments, 1).IsSelected = false;
        TestRoster.Node(_vm.Departments, 6).IsSelected = true;

        Assert.Equal([(before + 1, 0, false), (before + 2, 1, true)], seen);
    }

    [Fact]
    public async Task A_reload_is_a_selection_change_and_the_old_tree_stops_counting()
    {
        await LoadAsync();
        var oldNode = TestRoster.Node(_vm.Departments, 2);
        var changes = 0;
        using var _ = _vm.SelectionChanges.Subscribe(_ => changes++);

        await LoadAsync();
        var afterReload = changes;
        oldNode.IsSelected = false;

        Assert.True(afterReload > 0);
        Assert.Equal(afterReload, changes);
        Assert.Equal(6, _vm.SelectedEmployeeCount);
    }

    [Fact]
    public async Task Search_filters_without_changing_the_scope()
    {
        await LoadAsync();

        _vm.SearchText = "Flores";

        Assert.Equal("(Unassigned)", Assert.Single(_vm.VisibleDepartments).Name);
        Assert.Equal(6, _vm.SelectedEmployeeCount);
    }
}
