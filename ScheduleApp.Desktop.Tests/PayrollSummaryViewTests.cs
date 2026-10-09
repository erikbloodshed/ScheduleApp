using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Payroll;
using ScheduleApp.Desktop.Views;
using ScheduleApp.Payroll;
using Syncfusion.Windows.Tools.Controls;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class PayrollSummaryViewTests : IDisposable
{
    private readonly TestSelection _selection = new();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope = new(new DateTime(2026, 9, 16), new DateTime(2026, 9, 30)) { ActivePayrollRunId = 7 };

    public PayrollSummaryViewTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);

        // Cruz's payslip, with an Allowance of 250 and one Incentive line.
        _payroll.ComputeOneAsync(Arg.Any<Employee>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<(PayrollResult, IReadOnlyList<AttendanceSummary>)>((
                TestPayroll.Result(call.Arg<Employee>(), 1000m, 100m) with
                {
                    GrossPayAdjustmentGroups =
                    [
                        new() { Type = PayrollAdjustmentType.Allowance, Adjustments = [new() { Id = 1, Type = PayrollAdjustmentType.Allowance, Amount = 250m }] },
                        new() { Type = PayrollAdjustmentType.Incentive, Adjustments = [new() { Id = 2, Type = PayrollAdjustmentType.Incentive, Amount = 40m, Description = "Bonus" }] },
                    ],
                },
                [])));
    }

    public void Dispose() => _busy.Dispose();

    private PayrollSummaryViewModel NewViewModel() => new(
        _selection, _payroll, Substitute.For<IPayrollAdjustmentRepository>(), Substitute.For<IPayrollUndertimeWaiverRepository>(),
        _statusBar, _busy, _scope, new AttendanceDataVersion(), "Panaderia");

    private Task WithViewAsync(Func<PayrollSummaryView, Task> test) => UiThread.RunAsync(async () =>
    {
        var view = new PayrollSummaryView { ViewModel = NewViewModel() };
        var window = await UiThread.ShowAsync(new Window { Content = view, Width = 900, Height = 700 });
        try
        {
            await test(view);
        }
        finally
        {
            window.Close();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    [Fact]
    public Task Says_what_to_pick_with_nobody_selected() => WithViewAsync(view =>
    {
        Assert.Equal("Select an employee", view.HeaderText.Text);
        Assert.Equal(Visibility.Visible, view.EmptyStatePanel.Visibility);
        Assert.Equal("Select an employee to see their payroll breakdown.", view.EmptyStateText.Text);
        Assert.Equal(Visibility.Collapsed, view.BreakdownPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, view.PayTypeBadge.Visibility);
        Assert.Equal(Visibility.Collapsed, view.RefreshOrCancelButton.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Shows_the_selected_employees_breakdown() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        _selection.SelectedEmployee = TestRoster.Employee(3, "Cruz");
        await Until.TrueAsync(() => vm.Result is not null && !_busy.IsRunning, "the payslip");
        await UiThread.IdleAsync();

        Assert.StartsWith("Cruz", view.HeaderText.Text);
        Assert.Equal("Daily", view.PayTypeText.Text);
        Assert.Equal(Visibility.Collapsed, view.EmptyStatePanel.Visibility);
        Assert.Equal(Visibility.Visible, view.BreakdownPanel.Visibility);
        Assert.Equal(Converters.NumberConverter.Format(vm.Result!.TotalGrossPay), view.TotalGrossPayText.Text);
        Assert.Equal(Converters.NumberConverter.Format(vm.Result.NetPay), view.NetPayText.Text);
        Assert.Same(vm.Result.ComputedGrossPay, view.ComputedGrossPayList.ItemsSource);
        Assert.Same(vm.GrossPayAdjustmentGroupRows, view.GrossPayGroupList.ItemsSource);
        Assert.Same(vm.RefreshOrCancelPayslipCommand, view.RefreshOrCancelButton.Command);
        Assert.Equal("", view.RefreshOrCancelButton.Tag);
    });

    [Fact]
    public Task Category_cards_reach_the_view_model_and_lock_while_busy() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        _selection.SelectedEmployee = TestRoster.Employee(3, "Cruz");
        await Until.TrueAsync(() => vm.Result is not null && !_busy.IsRunning, "the payslip");
        await UiThread.IdleAsync();

        var boxes = Descendants<NumericTextBox>(view.GrossPayGroupList).ToList();
        Assert.NotEmpty(boxes);
        Assert.All(boxes, box => Assert.True(box.IsEnabled));
        Assert.Contains(Descendants<ButtonAdv>(view.GrossPayGroupList), b => ReferenceEquals(b.Command, vm.DeleteAdjustmentCommand));
        Assert.Contains(Descendants<Button>(view.GrossPayGroupList), b => ReferenceEquals(b.Command, vm.AddInlineRowCommand));

        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: true, _ => release.Task);
        await UiThread.IdleAsync();
        Assert.All(boxes, box => Assert.False(box.IsEnabled));
        Assert.Equal("", view.RefreshOrCancelButton.Tag);

        release.SetResult();
        await running;
        await UiThread.IdleAsync();
        Assert.All(boxes, box => Assert.True(box.IsEnabled));
    });
}
