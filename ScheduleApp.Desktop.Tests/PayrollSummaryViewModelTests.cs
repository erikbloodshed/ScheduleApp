using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Enums;
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

public sealed class PayrollSummaryViewModelTests : IDisposable
{
    private readonly TestSelection _selection = new();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();
    private readonly IPayrollAdjustmentRepository _adjustments = Substitute.For<IPayrollAdjustmentRepository>();
    private readonly IPayrollUndertimeWaiverRepository _waivers = Substitute.For<IPayrollUndertimeWaiverRepository>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope = new(new DateTime(2026, 9, 16), new DateTime(2026, 9, 30));
    private readonly AttendanceDataVersion _dataVersion = new();
    private readonly Employee _cruz = TestRoster.Employee(3, "Cruz");
    private readonly Employee _dizon = TestRoster.Employee(4, "Dizon");
    private readonly List<(int EmployeeId, decimal NetPay)> _netPays = [];
    private bool _confirmAnswer = true;

    public PayrollSummaryViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
    }

    public void Dispose() => _busy.Dispose();

    private PayrollSummaryViewModel NewViewModel()
    {
        var vm = new PayrollSummaryViewModel(
            _selection, _payroll, _adjustments, _waivers, _statusBar, _busy, _scope, _dataVersion, "Panaderia");
        vm.EmployeeNetPayComputed += (id, netPay) => _netPays.Add((id, netPay));
        vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        return vm;
    }

    /// <summary>A view model showing <see cref="_cruz"/>'s payslip, loaded.</summary>
    private async Task<PayrollSummaryViewModel> LoadedAsync()
    {
        _scope.ActivePayrollRunId = 7;
        _selection.SelectedEmployee = _cruz;
        var vm = NewViewModel();
        await Until.TrueAsync(() => vm.Result is not null && !_busy.IsRunning, "the payslip");
        return vm;
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    [Fact]
    public void Says_what_to_pick_before_anything_is_selected()
    {
        var vm = NewViewModel();

        Assert.Null(vm.Result);
        Assert.Equal("Select an employee", vm.HeaderText);
        Assert.Equal("Select an employee to see their payroll breakdown.", vm.EmptyStateMessage);
        Assert.Equal(vm.EmptyStateMessage, vm.AttendanceEmptyStateMessage);
        Assert.False(vm.CanEditAdjustmentsNow);
        Assert.False(CanExecute(vm.PrintCurrentPayslipCommand));
    }

    [Fact]
    public void Shows_no_payslip_before_a_payroll_group_is_loaded()
    {
        _selection.SelectedEmployee = _cruz;
        var vm = NewViewModel();

        Assert.Equal(_cruz.DisplayName, vm.HeaderText);
        Assert.Null(vm.Result);
        Assert.StartsWith("Start a \"New Payroll Run…\"", vm.EmptyStateMessage);
        _payroll.DidNotReceiveWithAnyArgs().ComputeOneAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Loads_the_selected_employees_payslip_for_the_active_group()
    {
        var vm = await LoadedAsync();

        Assert.Equal(_cruz.Pin, vm.Result!.EmployeeId);
        Assert.Null(vm.EmptyStateMessage);
        Assert.Equal("No attendance records for the selected period.", vm.AttendanceEmptyStateMessage);
        Assert.True(vm.CanEditAdjustmentsNow);
        Assert.True(CanExecute(vm.PrintCurrentPayslipCommand));
        Assert.True(CanExecute(vm.RecalculatePayslipCommand));
        await _payroll.Received(1).ComputeOneAsync(_cruz, new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Selecting_someone_else_loads_theirs()
    {
        var vm = await LoadedAsync();

        _selection.SelectedEmployee = _dizon;
        await Until.TrueAsync(() => vm.Result?.EmployeeId == _dizon.Pin && !_busy.IsRunning, "Dizon's payslip");

        Assert.Equal(_dizon.DisplayName, vm.HeaderText);
        Assert.Equal([(_dizon.Id, TestPayroll.Gross - TestPayroll.Deduction)], _netPays);
    }

    [Fact]
    public async Task A_backwards_period_clears_the_payslip_and_says_why()
    {
        var vm = await LoadedAsync();

        _scope.PeriodEnd = new DateTime(2026, 9, 1);

        Assert.Null(vm.Result);
        Assert.Equal("Period end can't be before period start.", vm.EmptyStateMessage);
        Assert.False(CanExecute(vm.PrintCurrentPayslipCommand));
    }

    [Fact]
    public async Task A_change_while_busy_waits_its_turn()
    {
        var vm = await LoadedAsync();
        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: true, _ => release.Task);

        Assert.True(vm.IsBusy);
        Assert.Equal("", vm.RefreshOrCancelGlyph);
        Assert.False(vm.CanEditAdjustmentsNow);
        _selection.SelectedEmployee = _dizon;
        Assert.Equal(_cruz.Pin, vm.Result?.EmployeeId);      // not loaded mid-run

        release.SetResult();
        await running;
        await Until.TrueAsync(() => vm.Result?.EmployeeId == _dizon.Pin && !_busy.IsRunning, "the deferred load");
        Assert.Equal("", vm.RefreshOrCancelGlyph);
    }

    [Fact]
    public async Task A_single_value_amount_creates_its_row_once()
    {
        var vm = await LoadedAsync();

        await vm.SetSingleValueAsync(PayrollAdjustmentType.Allowance, "250");

        await _adjustments.Received(1).AddAsync(
            Arg.Is<PayrollAdjustment>(a => a.EmployeeId == _cruz.Pin && a.Type == PayrollAdjustmentType.Allowance
                && a.Amount == 250m && a.PeriodStart == new DateOnly(2026, 9, 16)),
            Arg.Any<CancellationToken>());
        await _payroll.Received(1).ComputeOneAdjustmentsOnlyAsync(_cruz, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blank_means_zero_only_where_the_type_says_so()
    {
        var vm = await LoadedAsync();

        await vm.SetSingleValueAsync(PayrollAdjustmentType.Allowance, " ");
        await vm.SetSingleValueAsync(PayrollAdjustmentType.SSS, " ");
        await vm.SetSingleValueAsync(PayrollAdjustmentType.SSS, "-5");

        await _adjustments.Received(1).AddAsync(Arg.Any<PayrollAdjustment>(), Arg.Any<CancellationToken>());
        await _adjustments.Received(1).AddAsync(
            Arg.Is<PayrollAdjustment>(a => a.Type == PayrollAdjustmentType.Allowance && a.Amount == 0m), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_inline_edit_saves_a_copy_and_leaves_the_shown_row_alone()
    {
        var vm = await LoadedAsync();
        var row = new PayrollAdjustment { Id = 9, EmployeeId = _cruz.Pin, Type = PayrollAdjustmentType.Incentive, Amount = 50m, Description = "Bonus" };

        await vm.UpdateInlineAmountAsync(row, "75");
        await vm.UpdateInlineDescriptionAsync(row, "  Bonus ");     // unchanged once trimmed

        await _adjustments.Received(1).UpdateAsync(
            Arg.Is<PayrollAdjustment>(a => a.Id == 9 && a.Amount == 75m && a.Description == "Bonus" && !ReferenceEquals(a, row)),
            Arg.Any<CancellationToken>());
        Assert.Equal(50m, row.Amount);
    }

    [Fact]
    public async Task Delete_asks_first()
    {
        var vm = await LoadedAsync();
        var row = new PayrollAdjustment { Id = 9, Type = PayrollAdjustmentType.Incentive, Amount = 50m, Description = "Bonus" };

        _confirmAnswer = false;
        await vm.DeleteAdjustmentCommand.Execute(row);
        await _adjustments.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);

        _confirmAnswer = true;
        await vm.DeleteAdjustmentCommand.Execute(row);
        await _adjustments.Received(1).DeleteAsync(9, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Print_previews_the_loaded_payslip()
    {
        var vm = await LoadedAsync();
        ReactiveViewModel? shown = null;
        vm.ShowDialog.RegisterHandler(ctx =>
        {
            shown = ctx.Input;
            ctx.SetOutput(false);
        });

        await vm.PrintCurrentPayslipCommand.Execute();

        Assert.IsType<PayslipPreviewViewModel>(shown);
    }

    [Fact]
    public async Task Recalculate_reports_success()
    {
        var vm = await LoadedAsync();

        await vm.RecalculatePayslipCommand.Execute();

        _statusBar.Received().Show("Success", "Payslip recalculated.", StatusKind.Success, Arg.Any<TimeSpan>());
        await _payroll.Received(2).ComputeOneAsync(_cruz, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_page_revisit_reloads_only_when_something_moved()
    {
        var vm = await LoadedAsync();

        await vm.RecheckOnPageRevisitAsync();
        await _payroll.Received(1).ComputeOneAsync(_cruz, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        _dataVersion.BumpScheduleForEmployees([_cruz.Pin]);
        await vm.RecheckOnPageRevisitAsync();
        await _payroll.Received(2).ComputeOneAsync(_cruz, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
