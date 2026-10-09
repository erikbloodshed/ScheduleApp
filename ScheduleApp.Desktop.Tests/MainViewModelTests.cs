using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Desktop.ViewModels;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly TestSchedule _schedule = new();
    private MainViewModel Vm => _schedule.ViewModel;

    public void Dispose() => _schedule.Dispose();

    private async Task OpenAsync()
    {
        await Vm.OpenCommand.Execute();
        await _schedule.SettledAsync();
    }

    [Fact]
    public Task Opening_loads_the_tree_and_holidays_once() => UiThread.RunAsync(async () =>
    {
        await OpenAsync();
        await OpenAsync();

        await _schedule.Roster.Repository.Received(1).GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>());
        await _schedule.Holidays.Received(1).ListAsync(Arg.Any<CancellationToken>());
        Assert.Equal(3, Vm.Tree.Departments.Count);
        Assert.True(_schedule.Day(new DateOnly(2026, 9, 21)).IsHoliday);
    });

    [Fact]
    public Task Opening_restores_last_times_employee_onto_the_calendar() => UiThread.RunAsync(async () =>
    {
        _schedule.ViewState.Schedule.SelectedEmployeeId = 30;

        await OpenAsync();
        await Until.TrueAsync(() => _schedule.Day(TestSchedule.Sep1).Entry is not null && !_schedule.Busy.IsRunning, "Cruz's schedule");

        Assert.Same(_schedule.Pin(3), Vm.SelectedEmployee);
        Assert.Equal("Cruz, Juan", Vm.CalendarHeaderText);
        Assert.Equal(Core.Attendance.PunchStatus.Partial, _schedule.Day(TestSchedule.Sep1).AttendanceStatus);
    });

    [Fact]
    public Task The_selection_and_month_are_saved_together_once_restored() => UiThread.RunAsync(async () =>
    {
        _schedule.ViewState.Schedule.SelectedEmployeeId = 40;

        // Before the tree has restored anything, the opening month can't clobber the saved state.
        ((ICommand)Vm.Calendar.NextMonthCommand).Execute(null);
        Assert.Equal(40, _schedule.ViewState.Schedule.SelectedEmployeeId);

        await OpenAsync();
        Vm.SelectedEmployee = _schedule.Pin(1);
        ((ICommand)Vm.Calendar.PreviousMonthCommand).Execute(null);

        var state = _schedule.ViewState.Schedule;
        Assert.Equal(10, state.SelectedEmployeeId);
        Assert.Equal(new DateTime(2026, 9, 1), state.DisplayedMonth);
    });

    [Fact]
    public void The_selected_employee_is_the_trees_and_announced()
    {
        var changed = new List<string?>();
        ((INotifyPropertyChanged)Vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Vm.Tree.SelectedEmployee = _schedule.Pin(4);
        Assert.Same(_schedule.Pin(4), Vm.SelectedEmployee);
        Assert.Contains(nameof(MainViewModel.SelectedEmployee), changed);
        Assert.Equal("Dizon, Juan", Vm.CalendarHeaderText);

        Vm.SelectedEmployee = null;
        Assert.Null(Vm.Tree.SelectedEmployee);
        Assert.Equal("Select an employee", Vm.CalendarHeaderText);
    }

    [Fact]
    public Task Multi_select_mode_blanks_the_calendar_and_leaving_it_unchecks_everyone() => UiThread.RunAsync(async () =>
    {
        await OpenAsync();
        Vm.SelectedEmployee = _schedule.Pin(3);
        await Until.TrueAsync(() => _schedule.Day(TestSchedule.Sep1).Entry is not null && !_schedule.Busy.IsRunning, "Cruz's schedule");
        Assert.False(Vm.IsMultiSelectMode);
        Assert.Equal("Assign Schedule to Multiple Employees", Vm.MultiSelectButtonText);

        await Vm.ToggleMultiSelectModeCommand.Execute();
        await Until.TrueAsync(() => _schedule.Day(TestSchedule.Sep1).Entry is null && !_schedule.Busy.IsRunning, "the blank calendar");

        Assert.True(Vm.IsMultiSelectMode);
        Assert.Equal("Cancel Multi-Select", Vm.MultiSelectButtonText);
        Assert.Equal("Multiple employees -- select days, then assign", Vm.CalendarHeaderText);

        TestRoster.Node(Vm.Tree.Departments, 1).IsSelected = true;
        Assert.Equal(1, Vm.Tree.SelectedEmployeeCount);

        await Vm.ToggleMultiSelectModeCommand.Execute();
        await Until.TrueAsync(() => _schedule.Day(TestSchedule.Sep1).Entry is not null && !_schedule.Busy.IsRunning, "Cruz's schedule again");
        Assert.Equal(0, Vm.Tree.SelectedEmployeeCount);
        Assert.Equal("Cruz, Juan", Vm.CalendarHeaderText);
    });

    [Fact]
    public Task Picking_a_tree_row_selects_the_employee_and_their_department() => UiThread.RunAsync(async () =>
    {
        await OpenAsync();
        var tree = Vm.Tree;

        tree.SelectNode(TestRoster.Node(tree.Departments, 4));
        Assert.Same(_schedule.Pin(4), tree.SelectedEmployee);
        Assert.Same(_schedule.Roster.Kitchen, tree.SelectedDepartment);

        tree.SelectNode(tree.Departments[0]);
        Assert.Null(tree.SelectedEmployee);
        Assert.Same(_schedule.Roster.Bakery, tree.SelectedDepartment);

        tree.SelectNode(TestRoster.Node(tree.Departments, 6));
        tree.SelectNode(tree.Departments[2]);   // the "(Unassigned)" bucket
        Assert.Null(tree.SelectedDepartment);

        tree.SelectNode(TestRoster.Node(tree.Departments, 1));
        tree.SelectNode(null);
        Assert.Null(tree.SelectedEmployee);
        Assert.Same(_schedule.Roster.Bakery, tree.SelectedDepartment);
    });
}
