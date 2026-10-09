using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Payroll;
using ScheduleApp.Payroll;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class PayrollRunViewModelTests : IDisposable
{
    private readonly TestRoster _roster = new();
    private readonly IPayrollRunRepository _runs = Substitute.For<IPayrollRunRepository>();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope = new(new DateTime(2026, 9, 16), new DateTime(2026, 9, 30));
    private readonly TestSelection _selection = new();
    private readonly PayrollRunViewModel _vm;

    /// <summary>What the opened dialog does before it's closed -- and whether it's accepted.</summary>
    private Func<ReactiveViewModel, Task<bool>> _dialog = _ => Task.FromResult(false);

    public PayrollRunViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);

        _runs.CreateAsync(Arg.Any<PayrollRun>(), Arg.Any<CancellationToken>())
            .Returns(call => { var run = call.Arg<PayrollRun>(); run.Id = 42; return Task.FromResult(run); });

        _vm = new PayrollRunViewModel(
            _selection, _roster.Provider, _runs, _payroll, _statusBar, _busy, _scope);
        _vm.ShowDialog.RegisterHandler(async ctx => ctx.SetOutput(await _dialog(ctx.Input)));
    }

    public void Dispose() => _busy.Dispose();

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private static PayrollRun SavedRun(int id, string label, params int[] pins) => new()
    {
        Id = id,
        Label = label,
        PeriodStart = new DateOnly(2026, 10, 1),
        PeriodEnd = new DateOnly(2026, 10, 15),
        Employees = [.. pins.Select(pin => new PayrollRunEmployee { EmployeeId = pin })],
    };

    [Fact]
    public async Task A_saved_wizard_run_becomes_the_active_batch()
    {
        PayrollWizardViewModel? wizard = null;
        _dialog = async dialog =>
        {
            wizard = Assert.IsType<PayrollWizardViewModel>(dialog);
            await wizard.LoadEmployeeTreeCommand.Execute();
            await wizard.NextCommand.Execute();
            await wizard.ClearTreeSelectionCommand.Execute();
            wizard.Departments[1].IsSelected = true;       // Kitchen
            await wizard.NextCommand.Execute();
            await wizard.SavePayrollGroupCommand.Execute();
            return await wizard.NextCommand.Execute();     // Finish
        };

        await _vm.NewPayrollRunCommand.Execute();

        Assert.Equal(new DateTime(2026, 9, 16), wizard!.PeriodStart);     // started from the tab's period
        Assert.Equal([3, 4, 5], _scope.BatchScopeEmployees.Select(e => e.Pin));
        Assert.Equal(42, _scope.ActivePayrollRunId);
        Assert.Equal([null, 3], _selection.Assignments.Select(e => e?.Pin));        // cleared, then the first of the batch
        _statusBar.Received().Show("Success", "Saved payroll run: September 16-30, 2026", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Closing_the_wizard_without_saving_changes_nothing()
    {
        _dialog = async dialog =>
        {
            var wizard = (PayrollWizardViewModel)dialog;
            await wizard.LoadEmployeeTreeCommand.Execute();
            return true;
        };

        await _vm.NewPayrollRunCommand.Execute();

        Assert.Empty(_scope.BatchScopeEmployees);
        Assert.Null(_scope.ActivePayrollRunId);
        Assert.Empty(_selection.Assignments);
    }

    [Fact]
    public async Task A_loaded_run_resolves_its_pins_against_the_current_roster()
    {
        _runs.ListAsync(Arg.Any<CancellationToken>()).Returns([SavedRun(7, "Oct 1-15", 2, 99, 6)]);
        _dialog = async dialog =>
        {
            var picker = Assert.IsType<LoadPayrollGroupViewModel>(dialog);
            await picker.LoadRunsCommand.Execute();
            picker.SelectedRun = picker.Runs[0];
            return await picker.ChooseCommand.Execute();
        };

        await _vm.LoadPayrollGroupCommand.Execute();

        // Pin 99 no longer matches anyone active, so it's dropped.
        Assert.Equal([2, 6], _scope.BatchScopeEmployees.Select(e => e.Pin));
        Assert.Equal(7, _scope.ActivePayrollRunId);
        Assert.Equal(new DateTime(2026, 10, 1), _scope.PeriodStart);
        Assert.Equal(new DateTime(2026, 10, 15), _scope.PeriodEnd);
        Assert.Equal([null, 2], _selection.Assignments.Select(e => e?.Pin));
        _statusBar.Received().Show("Success", "Loaded payroll run: Oct 1-15", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task A_run_whose_employees_are_all_gone_is_an_error_not_an_empty_batch()
    {
        _runs.ListAsync(Arg.Any<CancellationToken>()).Returns([SavedRun(7, "Oct 1-15", 98, 99)]);
        _dialog = async dialog =>
        {
            var picker = (LoadPayrollGroupViewModel)dialog;
            await picker.LoadRunsCommand.Execute();
            picker.SelectedRun = picker.Runs[0];
            return await picker.ChooseCommand.Execute();
        };

        await _vm.LoadPayrollGroupCommand.Execute();

        Assert.Null(_scope.ActivePayrollRunId);
        Assert.Empty(_selection.Assignments);
        _statusBar.Received().Show(
            "Could not load payroll group",
            "None of \"Oct 1-15\"'s employees could be found -- they may have been deleted.",
            StatusKind.Error, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Both_wait_while_a_refresh_is_running()
    {
        Assert.True(CanExecute(_vm.NewPayrollRunCommand));
        Assert.True(CanExecute(_vm.LoadPayrollGroupCommand));

        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: false, _ => release.Task);

        Assert.False(CanExecute(_vm.NewPayrollRunCommand));
        Assert.False(CanExecute(_vm.LoadPayrollGroupCommand));

        release.SetResult();
        await running;
        Assert.True(CanExecute(_vm.NewPayrollRunCommand));
    }
}
