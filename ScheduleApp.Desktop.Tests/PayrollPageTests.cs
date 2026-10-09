using System.Windows;
using NSubstitute;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class PayrollPageTests : IDisposable
{
    private readonly TestRoster _roster = new();
    private readonly TestSelection _selection = new();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;

    public PayrollPageTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
    }

    public void Dispose() => _busy.Dispose();

    private PayrollViewModel NewViewModel() => new(
        _selection, TestPayroll.Computation(), Substitute.For<IPayrollAdjustmentRepository>(),
        Substitute.For<IPayrollUndertimeWaiverRepository>(), _roster.Provider, Substitute.For<IPayrollRunRepository>(),
        _statusBar, _busy, _roster.DataVersion, new PayrollSettings());

    /// <summary>The page, shown in a window of its own, with the Kitchen loaded as run 7's
    /// group if <paramref name="withGroup"/>.</summary>
    private Task WithPageAsync(bool withGroup, Func<PayrollPage, Task> test) => UiThread.RunAsync(async () =>
    {
        var page = new PayrollPage(NewViewModel());
        var window = await UiThread.ShowAsync(new Window { Content = page, Width = 1200, Height = 800 });
        try
        {
            if (withGroup)
            {
                page.ViewModel!.Scope.ActivePayrollRunId = 7;
                page.ViewModel.Scope.BatchScopeEmployees = [.. _roster.Kitchen.Employees];
                await Until.TrueAsync(() => page.ViewModel.Group.PayrollGroupRows.Count == 3 && !_busy.IsRunning, "the group");
                await UiThread.IdleAsync();
            }

            await test(page);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Covers_the_page_until_a_group_is_loaded() => WithPageAsync(withGroup: false, page =>
    {
        var vm = page.ViewModel!;
        Assert.Equal(Visibility.Visible, page.EmptyStateOverlay.Visibility);
        Assert.Same(vm.Run.NewPayrollRunCommand, page.OverlayNewPayrollRunButton.Command);
        Assert.Same(vm.Run.LoadPayrollGroupCommand, page.OverlayLoadPayrollGroupButton.Command);
        Assert.Same(vm.Summary, page.SummaryViewControl.ViewModel);
        Assert.Same(vm.Summary, page.AttendancePanelControl.ViewModel);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Toolbar_buttons_carry_their_commands() => WithPageAsync(withGroup: false, page =>
    {
        var vm = page.ViewModel!;
        Assert.Same(vm.Summary.PrintCurrentPayslipCommand, page.PrintCurrentPayslipButton.Command);
        Assert.Same(vm.PrintExport.PrintPayslipsCommand, page.PrintPayslipsButton.Command);
        Assert.Same(vm.PrintExport.ExportPayrollReportCommand, page.ExportPayrollReportButton.Command);
        Assert.Same(vm.Run.NewPayrollRunCommand, page.NewPayrollRunButton.Command);
        Assert.Same(vm.Run.LoadPayrollGroupCommand, page.LoadPayrollGroupButton.Command);
        Assert.Same(vm.Group.AddEmployeesToGroupCommand, page.AddToGroupButton.Command);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Period_pickers_bind_both_ways_and_ignore_a_cleared_picker() => WithPageAsync(withGroup: false, async page =>
    {
        var scope = page.ViewModel!.Scope;
        Assert.Equal(scope.PeriodStart, page.PeriodStartPicker.DateTime);
        Assert.Equal(scope.PeriodEnd, page.PeriodEndPicker.DateTime);

        page.PeriodEndPicker.DateTime = new DateTime(2026, 9, 28);
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 9, 28), scope.PeriodEnd);

        scope.PeriodStart = new DateTime(2026, 9, 2);
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 9, 2), page.PeriodStartPicker.DateTime);

        page.PeriodStartPicker.DateTime = null;
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 9, 2), scope.PeriodStart);
    });

    [Fact]
    public Task Shows_the_group_its_total_and_everyone_else() => WithPageAsync(withGroup: true, async page =>
    {
        var group = page.ViewModel!.Group;
        Assert.Equal(Visibility.Collapsed, page.EmptyStateOverlay.Visibility);
        Assert.Same(group.PayrollGroupRowsView, page.PayrollGroupGrid.ItemsSource);
        Assert.Same(group.AvailableEmployeeRowsView, page.AvailableEmployeeGrid.ItemsSource);
        Assert.Equal(Converters.NumberConverter.Format(3 * (TestPayroll.Gross - TestPayroll.Deduction)), page.TotalNetPayText.Text);

        page.GroupSearchBox.Text = "Cruz";
        await UiThread.IdleAsync();
        Assert.Equal("Cruz", group.PayrollGroupSearchText);
        Assert.Single(group.PayrollGroupRowsView);
    });

    [Fact]
    public Task Exclude_and_include_act_on_the_selected_rows() => WithPageAsync(withGroup: true, async page =>
    {
        var group = page.ViewModel!.Group;
        Assert.Same(group.RemoveEmployeeFromGroupCommand, page.ExcludeButton.Command);
        Assert.Same(group.AddEmployeeToGroupCommand, page.IncludeButton.Command);

        page.PayrollGroupGrid.SelectedItem = group.PayrollGroupRowsView[1];               // Dizon
        page.AvailableEmployeeGrid.SelectedItem = group.AvailableEmployeeRowsView[2];     // Flores
        await UiThread.IdleAsync();

        Assert.Equal(4, (page.ExcludeButton.CommandParameter as ScheduleApp.Core.Models.Employee)?.Pin);
        Assert.Equal(6, (page.IncludeButton.CommandParameter as ScheduleApp.Core.Models.Employee)?.Pin);
    });

    [Fact]
    public Task Highlights_the_selected_employee_on_a_visit() => WithPageAsync(withGroup: true, async page =>
    {
        _selection.SelectedEmployee = _roster.Kitchen.Employees.Single(e => e.Pin == 5);
        await Until.TrueAsync(() => !_busy.IsRunning);

        await page.OnNavigatedToAsync();
        await UiThread.IdleAsync();

        Assert.Equal(5, (page.PayrollGroupGrid.SelectedItem as PayrollGroupRow)?.Employee.Pin);
    });
}
