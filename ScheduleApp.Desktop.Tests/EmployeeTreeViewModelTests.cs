using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Schedule;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class EmployeeTreeViewModelTests : IDisposable
{
    private readonly TestRoster _roster = new();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly ViewStateStore _viewState = new();
    private readonly AttendanceBusyState _busy;
    private readonly EmployeeTreeViewModel _vm;
    private int _saves;
    private bool _confirmAnswer = true;
    private Func<ReactiveViewModel, bool> _dialog = _ => false;

    public EmployeeTreeViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Department> { _roster.Bakery, _roster.Kitchen }));
        _roster.Repository.GetUnassignedEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_roster.Unassigned));

        _busy = new AttendanceBusyState(_statusBar);
        _vm = new EmployeeTreeViewModel(_roster.Repository, _statusBar, _viewState, _roster.DataVersion, () => _saves++, _busy,
            new PayrollPolicy(), new AttendanceSettings());
        _vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        _vm.ShowDialog.RegisterHandler(ctx => ctx.SetOutput(_dialog(ctx.Input)));
    }

    public void Dispose() => _busy.Dispose();

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private async Task LoadAsync() => await _vm.LoadCommand.Execute();

    private Employee Pin(int pin) => _roster.Everyone.Single(e => e.Pin == pin);

    [Fact]
    public async Task Loading_builds_the_tree_with_nobody_checked()
    {
        await LoadAsync();

        Assert.Equal(["Bakery", "Kitchen", "(Unassigned)"], _vm.Departments.Select(d => d.Name));
        Assert.Equal(3, _vm.VisibleDepartments.Count);
        Assert.Equal(0, _vm.SelectedEmployeeCount);
        Assert.Equal([_roster.Bakery, _roster.Kitchen], _vm.RealDepartments);
        Assert.True(_vm.HasRestoredSelection);
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task The_first_load_restores_the_saved_employee()
    {
        _viewState.Schedule.SelectedEmployeeId = 40;
        await LoadAsync();

        Assert.Same(Pin(4), _vm.SelectedEmployee);
        Assert.Same(_roster.Kitchen, _vm.SelectedDepartment);

        // A later reload keeps whatever has been selected since.
        _vm.SelectedEmployee = Pin(1);
        await LoadAsync();
        Assert.Same(Pin(1), _vm.SelectedEmployee);
    }

    [Fact]
    public async Task The_first_load_falls_back_to_the_saved_department()
    {
        _viewState.Schedule.SelectedEmployeeId = 999;
        _viewState.Schedule.SelectedDepartmentId = 1;
        await LoadAsync();

        Assert.Null(_vm.SelectedEmployee);
        Assert.Same(_roster.Bakery, _vm.SelectedDepartment);
    }

    [Fact]
    public async Task Selection_changes_save_the_view_state()
    {
        await LoadAsync();
        var before = _saves;

        _vm.SelectedEmployee = Pin(3);
        _vm.SelectedDepartment = _roster.Kitchen;

        Assert.Equal(before + 2, _saves);
    }

    [Fact]
    public async Task Checking_employees_counts_them_and_enables_clearing()
    {
        await LoadAsync();
        Assert.False(CanExecute(_vm.ClearEmployeeSelectionCommand));

        TestRoster.Node(_vm.Departments, 1).IsSelected = true;
        TestRoster.Node(_vm.Departments, 6).IsSelected = true;
        Assert.Equal(2, _vm.SelectedEmployeeCount);
        Assert.True(CanExecute(_vm.ClearEmployeeSelectionCommand));
        Assert.Equal([1, 6], _vm.GetCheckedEmployees().Select(e => e.Pin).Order());

        ((ICommand)_vm.ClearEmployeeSelectionCommand).Execute(null);
        Assert.Equal(0, _vm.SelectedEmployeeCount);
        Assert.False(CanExecute(_vm.ClearEmployeeSelectionCommand));
    }

    [Fact]
    public async Task A_reloaded_tree_is_counted_afresh()
    {
        await LoadAsync();
        TestRoster.Node(_vm.Departments, 2).IsSelected = true;
        Assert.Equal(1, _vm.SelectedEmployeeCount);

        await LoadAsync();
        Assert.Equal(0, _vm.SelectedEmployeeCount);
        TestRoster.Node(_vm.Departments, 3).IsSelected = true;
        Assert.Equal(1, _vm.SelectedEmployeeCount);
    }

    [Fact]
    public async Task Searching_narrows_the_visible_tree()
    {
        await LoadAsync();

        _vm.SearchText = "Cruz";
        Assert.Equal(["Kitchen"], _vm.VisibleDepartments.Select(d => d.Name));

        _vm.SearchText = string.Empty;
        Assert.Equal(3, _vm.VisibleDepartments.Count);
    }

    [Fact]
    public async Task Commands_follow_the_selection()
    {
        await LoadAsync();
        Assert.False(CanExecute(_vm.DeleteDepartmentCommand));
        Assert.False(CanExecute(_vm.EditEmployeeCommand));
        Assert.False(CanExecute(_vm.DeleteEmployeeCommand));
        Assert.False(CanExecute(_vm.BlacklistEmployeeCommand));
        Assert.False(CanExecute(_vm.UnblacklistEmployeeCommand));

        _vm.SelectedDepartment = _roster.Bakery;
        Assert.True(CanExecute(_vm.DeleteDepartmentCommand));

        _vm.SelectedEmployee = Pin(1);
        Assert.True(CanExecute(_vm.EditEmployeeCommand));
        Assert.True(CanExecute(_vm.DeleteEmployeeCommand));
        Assert.True(CanExecute(_vm.BlacklistEmployeeCommand));
        Assert.False(CanExecute(_vm.UnblacklistEmployeeCommand));

        _vm.SelectedEmployee = new Employee { Id = 70, Pin = 7, LastName = "Gomez", FirstName = "Ana", IsBlacklisted = true };
        Assert.False(CanExecute(_vm.BlacklistEmployeeCommand));
        Assert.True(CanExecute(_vm.UnblacklistEmployeeCommand));
    }

    [Fact]
    public async Task Adding_a_department_asks_for_its_name()
    {
        await LoadAsync();
        var roster = _roster.DataVersion.RosterVersion;
        _dialog = vm =>
        {
            var prompt = Assert.IsType<TextPromptViewModel>(vm);
            Assert.Equal("New Department", prompt.Title);
            prompt.Text = " Pastry ";
            return true;
        };

        await _vm.AddDepartmentCommand.Execute();

        await _roster.Repository.Received(1).AddDepartmentAsync("Pastry", Arg.Any<CancellationToken>());
        Assert.Equal(roster + 1, _roster.DataVersion.RosterVersion);
    }

    [Fact]
    public async Task A_cancelled_prompt_adds_nothing()
    {
        await LoadAsync();
        await _vm.AddDepartmentCommand.Execute();

        await _roster.Repository.DidNotReceive().AddDepartmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_department_is_confirmed_first()
    {
        await LoadAsync();
        _vm.SelectedDepartment = _roster.Bakery;
        _vm.SelectedEmployee = Pin(1);

        _confirmAnswer = false;
        await _vm.DeleteDepartmentCommand.Execute();
        await _roster.Repository.DidNotReceive().DeleteDepartmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        _confirmAnswer = true;
        await _vm.DeleteDepartmentCommand.Execute();
        await _roster.Repository.Received(1).DeleteDepartmentAsync(1, Arg.Any<CancellationToken>());
        Assert.Null(_vm.SelectedDepartment);
        Assert.Null(_vm.SelectedEmployee);
    }

    [Fact]
    public async Task Adding_an_employee_saves_what_the_dialog_settles_on()
    {
        await LoadAsync();
        _vm.SelectedDepartment = _roster.Kitchen;
        EmployeeEditorViewModel? editor = null;
        _dialog = vm =>
        {
            editor = Assert.IsType<EmployeeEditorViewModel>(vm);
            editor.Pin = 7;
            editor.LastName = "Garcia";
            editor.FirstName = "Ana";
            editor.DailyRate = 610m;
            return editor.AcceptCommand.Execute().Wait();
        };

        await _vm.AddEmployeeCommand.Execute();

        Assert.Equal("New Employee", editor!.Title);
        Assert.Same(_roster.Kitchen, editor.Department);
        await _roster.Repository.Received(1).AddEmployeeAsync("Garcia", "Ana", 2, 7,
            true, true, 610m, false, true, 0m, 0m, 0m, 0m, 0m, 0m,
            Core.Enums.EmployeeType.Daily, 0m, null, false, false, null, null, null, null, null, null, false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_dialog_turns_away_an_id_another_employee_has()
    {
        await LoadAsync();
        var notices = new List<string>();
        _dialog = vm =>
        {
            var editor = (EmployeeEditorViewModel)vm;
            editor.Notify.RegisterHandler(ctx =>
            {
                notices.Add(ctx.Input.Message);
                ctx.SetOutput(default);
            });
            editor.Pin = 3;
            editor.LastName = "Garcia";
            editor.FirstName = "Ana";
            return editor.AcceptCommand.Execute().Wait();
        };

        await _vm.AddEmployeeCommand.Execute();

        Assert.Contains("Employee ID 3 is already assigned", notices.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_an_employee_may_keep_their_own_id()
    {
        await LoadAsync();
        _vm.SelectedEmployee = Pin(3);
        _dialog = vm =>
        {
            var editor = (EmployeeEditorViewModel)vm;
            Assert.Equal("Edit Employee", editor.Title);
            editor.FirstName = "Pedro";
            return editor.AcceptCommand.Execute().Wait();
        };

        await _vm.EditEmployeeCommand.Execute();

        await _roster.Repository.Received(1).UpdateEmployeeAsync(30, "Cruz", "Pedro", 2, 3,
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<decimal>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<Core.Enums.EmployeeType>(), Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_race_for_the_same_id_is_reported_not_thrown()
    {
        await LoadAsync();
        _roster.Repository.AddEmployeeAsync(default!, default!, default, default,
                default, default, default, default, default, default, default, default, default, default, default,
                default, default, default, default, default, default, default, default, default, default, default, default,
                default)
            .ReturnsForAnyArgs(Task.FromException<Employee>(new DuplicateEmployeeIdException(7)));
        _dialog = vm =>
        {
            var editor = (EmployeeEditorViewModel)vm;
            editor.Pin = 7;
            editor.LastName = "Garcia";
            editor.FirstName = "Ana";
            return editor.AcceptCommand.Execute().Wait();
        };
        var roster = _roster.DataVersion.RosterVersion;

        await _vm.AddEmployeeCommand.Execute();

        _statusBar.Received(1).Show("Duplicate Employee ID", Arg.Any<string>(), StatusKind.Caution, Arg.Any<TimeSpan>());
        Assert.Equal(roster, _roster.DataVersion.RosterVersion);
    }

    [Fact]
    public async Task Deleting_an_employee_bumps_their_schedule()
    {
        await LoadAsync();
        _vm.SelectedEmployee = Pin(4);
        var schedule = _roster.DataVersion.ScheduleVersion;

        await _vm.DeleteEmployeeCommand.Execute();

        await _roster.Repository.Received(1).DeleteEmployeeAsync(40, Arg.Any<CancellationToken>());
        Assert.Null(_vm.SelectedEmployee);
        Assert.True(_roster.DataVersion.ScheduleVersion > schedule);
    }

    [Fact]
    public async Task Blacklisting_reselects_the_reloaded_employee()
    {
        await LoadAsync();
        _vm.SelectedEmployee = Pin(2);
        var reloaded = new Employee { Id = 20, Pin = 2, LastName = "Bautista", FirstName = "Juan", IsBlacklisted = true };
        _roster.Repository.SetEmployeeBlacklistAsync(20, true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var bakery = new Department { Id = 1, Name = "Bakery" };
                bakery.Employees.Add(Pin(1));
                bakery.Employees.Add(reloaded);
                _roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new List<Department> { bakery, _roster.Kitchen }));
                return Task.CompletedTask;
            });

        await _vm.BlacklistEmployeeCommand.Execute();

        Assert.Same(reloaded, _vm.SelectedEmployee);
        Assert.False(CanExecute(_vm.BlacklistEmployeeCommand));
        Assert.True(CanExecute(_vm.UnblacklistEmployeeCommand));
    }

    [Fact]
    public async Task A_failed_load_is_reported()
    {
        _roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<Department>>(new InvalidOperationException("offline")));

        await LoadAsync();

        _statusBar.Received(1).Show("Couldn't load employees", Arg.Any<string>(), StatusKind.Error, Arg.Any<TimeSpan>());
        Assert.Empty(_vm.Departments);
        Assert.False(_busy.IsRunning);
    }
}
