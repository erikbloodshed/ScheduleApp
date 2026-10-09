using System.IO;
using System.Reactive.Linq;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Payroll;
using ScheduleApp.Payroll;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class PayrollPrintExportViewModelTests : IDisposable
{
    private readonly TestRoster _roster = new();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly PayrollScopeState _scope = new(new DateTime(2026, 9, 16), new DateTime(2026, 9, 30));
    private readonly PayrollPrintExportViewModel _vm;
    private readonly string _workbook = Path.Combine(Path.GetTempPath(), $"salary-{Guid.NewGuid():N}.xlsx");
    private readonly List<ReactiveViewModel> _shown = [];
    private FileRequest? _saveAsked;
    private string? _savePath;

    /// <summary>Whether the scope picker is accepted -- with whatever it starts checked to.</summary>
    private bool _acceptScope = true;

    public PayrollPrintExportViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);


        _vm = new PayrollPrintExportViewModel(_roster.Provider, _payroll, _statusBar, _busy, _scope, "Panaderia");
        _vm.ShowDialog.RegisterHandler(async ctx =>
        {
            _shown.Add(ctx.Input);
            if (ctx.Input is PayslipScopeViewModel scope)
            {
                await scope.LoadEmployeeTreeCommand.Execute(scope.PresetSelection);
                ctx.SetOutput(_acceptScope && await scope.AcceptCommand.Execute());
                return;
            }

            ctx.SetOutput(true);
        });
        _vm.PickFileToSave.RegisterHandler(ctx =>
        {
            _saveAsked = ctx.Input;
            ctx.SetOutput(_savePath);
        });
    }

    public void Dispose()
    {
        _busy.Dispose();
        File.Delete(_workbook);
    }

    [Fact]
    public async Task Print_previews_everyone_the_picker_comes_back_with()
    {
        await _vm.PrintPayslipsCommand.Execute();

        var scope = Assert.IsType<PayslipScopeViewModel>(_shown[0]);
        Assert.Equal("Print Payslips", scope.Title);
        Assert.Equal(new DateTime(2026, 9, 16), scope.PeriodStart);
        Assert.Null(scope.PresetSelection);
        Assert.IsType<PayslipPreviewViewModel>(_shown[1]);
        await _payroll.Received(1).PrepareBatchAsync(
            Arg.Is<IReadOnlyCollection<int>>(pins => pins.Count == 6),
            new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 30), Arg.Any<CancellationToken>());
        await _payroll.Received(6).ComputeOneFromBatchAsync(
            Arg.Any<Employee>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<PayrollBatchContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_active_group_presets_the_picker()
    {
        _scope.BatchScopeEmployees = [.. _roster.Kitchen.Employees];

        await _vm.PrintPayslipsCommand.Execute();

        var scope = Assert.IsType<PayslipScopeViewModel>(_shown[0]);
        Assert.Same(_scope.BatchScopeEmployees, scope.PresetSelection);
        Assert.Equal([3, 4, 5], scope.AcceptedScope?.Employees.Select(e => e.Pin));
    }

    [Fact]
    public async Task Cancelling_the_picker_computes_nothing()
    {
        _acceptScope = false;

        await _vm.PrintPayslipsCommand.Execute();

        Assert.Single(_shown);
        await _payroll.DidNotReceiveWithAnyArgs().PrepareBatchAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_failed_computation_is_reported_and_previews_nothing()
    {
        _payroll.PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database offline"));

        await _vm.PrintPayslipsCommand.Execute();

        Assert.Single(_shown);
        _statusBar.Received().Show(
            "Could not compute payroll for printing", Arg.Is<string>(m => m.Contains("database offline")), StatusKind.Error, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Export_saves_a_workbook_where_asked()
    {
        _savePath = _workbook;

        await _vm.ExportPayrollReportCommand.Execute();

        Assert.Empty(_statusBar.ReceivedCalls()
            .Where(call => call.GetArguments() is [_, _, StatusKind.Error, _])
            .Select(call => call.GetArguments()[1]));
        Assert.Equal("Export Payroll Report", Assert.IsType<PayslipScopeViewModel>(Assert.Single(_shown)).Title);
        Assert.Equal("Salary_091626-093026.xlsx", _saveAsked?.FileName);
        Assert.True(new FileInfo(_workbook).Length > 0);
        _statusBar.Received().Show("Success", $"Saved payroll report to {_workbook}.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Cancelling_the_save_writes_nothing()
    {
        await _vm.ExportPayrollReportCommand.Execute();

        Assert.NotNull(_saveAsked);
        Assert.False(File.Exists(_workbook));
        _statusBar.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<string>(), StatusKind.Success, Arg.Any<TimeSpan>());
    }
}
