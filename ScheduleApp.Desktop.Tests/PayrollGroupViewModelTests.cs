using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Payroll;
using ScheduleApp.Payroll;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class PayrollGroupViewModelTests : IDisposable
{
    private const decimal NetPay = TestPayroll.Gross - TestPayroll.Deduction;
    private static readonly int[] KitchenPins = [3, 4, 5];
    private static readonly int[] AlcantaraAndFloresPins = [1, 6];

    private readonly TestRoster _roster = new();
    private readonly TestSelection _selection = new();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();
    private readonly IPayrollRunRepository _runs = Substitute.For<IPayrollRunRepository>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope = new(new DateTime(2026, 9, 16), new DateTime(2026, 9, 30));
    private readonly AttendanceDataVersion _dataVersion = new();
    private readonly PayrollSummaryViewModel _summary;
    private readonly PayrollGroupViewModel _vm;

    public PayrollGroupViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
        _summary = new PayrollSummaryViewModel(
            _selection, _payroll, Substitute.For<IPayrollAdjustmentRepository>(), Substitute.For<IPayrollUndertimeWaiverRepository>(),
            _statusBar, _busy, _scope, _dataVersion, "Panaderia");
        _vm = new PayrollGroupViewModel(
            _selection, _roster.Provider, _payroll, _runs, _statusBar, _busy, _scope, _summary, _dataVersion);
    }

    public void Dispose() => _busy.Dispose();

    private static bool CanExecute(ICommand command, object? parameter = null) => command.CanExecute(parameter);

    /// <summary>Lands on run 7 with the Kitchen (Pins 3-5) as its group, the way a new or loaded
    /// run does, and waits for the group and roster to load.</summary>
    private async Task LandOnKitchenAsync()
    {
        _scope.ActivePayrollRunId = 7;
        _scope.BatchScopeEmployees = [.. _roster.Kitchen.Employees];
        await Until.TrueAsync(() => _vm.PayrollGroupRows.Count == 3 && _vm.AvailableEmployeeRows.Count == 3 && !_busy.IsRunning,
            "the group and roster");
    }

    [Fact]
    public void Starts_with_no_group()
    {
        Assert.False(_vm.HasBatchScope);
        Assert.Empty(_vm.PayrollGroupRows);
        Assert.Equal(0m, _vm.TotalNetPay);
        Assert.False(CanExecute(_vm.AddEmployeesToGroupCommand));
    }

    [Fact]
    public async Task A_new_group_computes_every_member_and_lists_everyone_else()
    {
        await LandOnKitchenAsync();

        Assert.True(_vm.HasBatchScope);
        Assert.Equal(["Cruz", "Dizon", "Espino"], _vm.PayrollGroupRowsView.Select(r => r.Employee.LastName));
        Assert.All(_vm.PayrollGroupRows, row => Assert.Equal(NetPay, row.NetPay));
        Assert.Equal(3 * NetPay, _vm.TotalNetPay);
        Assert.Equal(["Alcantara", "Bautista", "Flores"], _vm.AvailableEmployeeRowsView.Select(r => r.Employee.LastName));
        await _payroll.Received(1).PrepareBatchAsync(
            Arg.Is<IReadOnlyCollection<int>>(pins => pins.Order().SequenceEqual(KitchenPins)),
            new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 30), Arg.Any<CancellationToken>());
        _statusBar.Received().ShowProgress("Computing payroll group…", Arg.Any<double>());
        _statusBar.Received().ClearProgress();
    }

    [Fact]
    public async Task Search_narrows_both_grids_but_not_the_total()
    {
        await LandOnKitchenAsync();

        _vm.PayrollGroupSearchText = "dizon, 6";

        Assert.Equal("Dizon", Assert.Single(_vm.PayrollGroupRowsView).Employee.LastName);
        Assert.Equal("Flores", Assert.Single(_vm.AvailableEmployeeRowsView).Employee.LastName);
        Assert.Equal(3 * NetPay, _vm.TotalNetPay);
    }

    [Fact]
    public async Task A_recomputed_payslip_patches_its_row_and_the_total()
    {
        await LandOnKitchenAsync();
        var dizon = _roster.Kitchen.Employees.Single(e => e.Pin == 4);

        _selection.SelectedEmployee = dizon;
        await Until.TrueAsync(() => _summary.Result is not null && !_busy.IsRunning, "Dizon's payslip");
        _vm.PayrollGroupRows.Single(r => r.Employee == dizon).NetPay = 5m;

        Assert.Equal(2 * NetPay + 5m, _vm.TotalNetPay);
    }

    [Fact]
    public async Task Removing_the_selected_member_moves_the_selection()
    {
        await LandOnKitchenAsync();
        var cruz = _roster.Kitchen.Employees.Single(e => e.Pin == 3);
        _selection.SelectedEmployee = cruz;
        await Until.TrueAsync(() => !_busy.IsRunning);

        await _vm.RemoveEmployeeFromGroupCommand.Execute(cruz);

        await _runs.Received(1).RemoveEmployeeAsync(7, 3, Arg.Any<CancellationToken>());
        Assert.Equal([4, 5], _scope.BatchScopeEmployees.Select(e => e.Pin));
        Assert.Equal(["Dizon", "Espino"], _vm.PayrollGroupRowsView.Select(r => r.Employee.LastName));
        Assert.Equal(2 * NetPay, _vm.TotalNetPay);
        Assert.Contains(_vm.AvailableEmployeeRows, r => r.Employee == cruz);
        Assert.Equal(4, _selection.SelectedEmployee?.Pin);
        // Patched in place -- the remaining members aren't recomputed.
        await _payroll.Received(1).PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_one_computes_just_them()
    {
        await LandOnKitchenAsync();
        var flores = _roster.Unassigned[0];

        await _vm.AddEmployeeToGroupCommand.Execute(flores);

        await _runs.Received(1).AddEmployeeAsync(7, 6, Arg.Any<CancellationToken>());
        Assert.Equal(NetPay, _vm.PayrollGroupRows.Single(r => r.Employee == flores).NetPay);
        Assert.Equal(4 * NetPay, _vm.TotalNetPay);
        Assert.DoesNotContain(_vm.AvailableEmployeeRows, r => r.Employee == flores);
        _statusBar.Received().Show("Success", $"Added {flores.DisplayName} to the group.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Adding_several_writes_only_the_newly_checked()
    {
        await LandOnKitchenAsync();
        AddToPayrollGroupViewModel? picker = null;
        _vm.ShowDialog.RegisterHandler(async ctx =>
        {
            picker = Assert.IsType<AddToPayrollGroupViewModel>(ctx.Input);
            TestRoster.Node(picker.VisibleDepartments, 1).IsSelected = true;     // Alcantara
            TestRoster.Node(picker.VisibleDepartments, 6).IsSelected = true;     // Flores
            await picker.AcceptCommand.Execute();
            ctx.SetOutput(true);
        });

        await _vm.AddEmployeesToGroupCommand.Execute();

        Assert.Equal("5 employees checked.", picker!.ScopeText);
        await _runs.Received(1).AddEmployeesAsync(7,
            Arg.Is<IReadOnlyCollection<int>>(pins => pins.Order().SequenceEqual(AlcantaraAndFloresPins)), Arg.Any<CancellationToken>());
        Assert.Equal([3, 4, 5, 1, 6], _scope.BatchScopeEmployees.Select(e => e.Pin));
        Assert.Equal(5, _vm.PayrollGroupRows.Count);
        Assert.Equal(5 * NetPay, _vm.TotalNetPay);
        Assert.Equal(["Bautista"], _vm.AvailableEmployeeRowsView.Select(r => r.Employee.LastName));
        _statusBar.Received().Show("Success", "Added 2 employees to the group.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Membership_edits_wait_for_a_run_and_for_quiet()
    {
        _scope.BatchScopeEmployees = [.. _roster.Kitchen.Employees];
        await Until.TrueAsync(() => !_busy.IsRunning);
        Assert.False(CanExecute(_vm.AddEmployeesToGroupCommand));

        _scope.ActivePayrollRunId = 7;
        Assert.True(CanExecute(_vm.AddEmployeesToGroupCommand));

        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: false, _ => release.Task);
        Assert.False(CanExecute(_vm.AddEmployeesToGroupCommand));
        release.SetResult();
        await running;
        Assert.True(CanExecute(_vm.AddEmployeesToGroupCommand));
    }

    [Fact]
    public async Task A_period_change_while_busy_recomputes_once_quiet()
    {
        await LandOnKitchenAsync();
        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: false, _ => release.Task);

        _scope.PeriodEnd = new DateTime(2026, 9, 25);
        await _payroll.Received(1).PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        release.SetResult();
        await running;
        await Until.TrueAsync(() => _payroll.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPayrollComputationService.PrepareBatchAsync)) == 2,
            "the deferred recompute");
        await _payroll.Received(1).PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), new DateOnly(2026, 9, 25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_page_revisit_recomputes_only_for_a_change_to_a_member()
    {
        await LandOnKitchenAsync();

        _dataVersion.BumpScheduleForEmployees([1]);       // Alcantara isn't in the group
        await _vm.RecheckOnPageRevisitAsync();
        await _payroll.Received(1).PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        _dataVersion.BumpScheduleForEmployees([4]);
        await _vm.RecheckOnPageRevisitAsync();
        await _payroll.Received(2).PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
