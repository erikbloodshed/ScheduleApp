using System.IO;
using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The Attendance Summary's report, through the whole Attendance tab (TestAttendance).</summary>
public sealed class ReportViewModelTests : IDisposable
{
    private readonly TestAttendance _attendance = new();
    private readonly string _workbook = Path.Combine(Path.GetTempPath(), $"summary-{Guid.NewGuid():N}.xlsx");
    private readonly List<ReactiveViewModel> _shown = [];
    private string? _savePath;
    private string? _opened;

    public ReportViewModelTests()
    {
        _attendance.Runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Result(call.Arg<AttendanceRunRequest>())));

        Report.PickFileToSave.RegisterHandler(ctx => ctx.SetOutput(_savePath));
        Report.OpenFile.RegisterHandler(ctx =>
        {
            _opened = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        Report.ShowDialog.RegisterHandler(ctx =>
        {
            _shown.Add(ctx.Input);
            ctx.SetOutput(false);
        });
    }

    public void Dispose()
    {
        _attendance.Dispose();
        File.Delete(_workbook);
    }

    private AttendanceViewModel Tab => _attendance.ViewModel;
    private ReportViewModel Report => Tab.Report;

    /// <summary>A report with one Complete, one Partial and one Absent day, one orphaned
    /// punch, for whoever the request covers.</summary>
    private static AttendanceRunResult Result(AttendanceRunRequest request) => new()
    {
        Summaries =
        [
            Summary(3, "Cruz", request.PeriodStart, PunchStatus.Complete),
            Summary(3, "Cruz", request.PeriodStart.AddDays(1), PunchStatus.Partial),
            Summary(4, "Dizon", request.PeriodStart, PunchStatus.Absent),
        ],
        RawLogs = [new AttendanceLog { EmployeeId = 3 }, new AttendanceLog { EmployeeId = 3 }],
        Employees = [TestRoster.Employee(3, "Cruz"), TestRoster.Employee(4, "Dizon")],
        OrphanedPunches = [new AttendanceLog { EmployeeId = 3, Timestamp = request.PeriodStart.ToDateTime(new TimeOnly(23, 0)) }],
    };

    private static AttendanceSummary Summary(int pin, string name, DateOnly day, PunchStatus status) => new()
    {
        EmployeeId = pin,
        EmployeeName = name,
        Department = "Kitchen",
        ShiftDate = day,
        Status = status,
    };

    private int Runs => _attendance.Runner.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAttendanceRunner.RunAsync));

    /// <summary>The Summary page opened, its first report loaded.</summary>
    private async Task ShowSummaryAsync()
    {
        await Tab.EnsureInitializedAsync();
        Tab.ActivateSummaryTab();
        await Until.TrueAsync(() => Report.HasResults && !_attendance.Busy.IsRunning, "the first report");
    }

    private static bool CanExecute(ICommand command, object? parameter = null) => command.CanExecute(parameter);

    [Fact]
    public async Task Opening_the_summary_loads_the_report_and_its_counts()
    {
        await ShowSummaryAsync();

        Assert.Equal(1, Runs);
        Assert.Equal(2, Report.TotalLogs);
        Assert.Equal(1, Report.CompleteCount);
        Assert.Equal(1, Report.PartialCount);
        Assert.Equal(1, Report.AbsentCount);
        Assert.Equal(1, Report.OrphanedCount);
        Assert.True(Report.HasSummaryRows);
        Assert.Equal(3, Report.SummaryRowsView.Count);
        Assert.True(CanExecute(Report.ExportSummaryCommand));
    }

    [Fact]
    public async Task A_period_change_reruns_the_report()
    {
        await ShowSummaryAsync();

        await Report.NextPeriodCommand.Execute();
        await Until.TrueAsync(() => Runs == 2 && !_attendance.Busy.IsRunning, "the re-run");

        await _attendance.Runner.Received(1).RunAsync(
            Arg.Is<AttendanceRunRequest>(r => r.PeriodStart == DateOnly.FromDateTime(Report.PeriodStart!.Value)),
            Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>());
        _attendance.StatusBar.Received().Show("Report ready", Arg.Is<string>(m => m.StartsWith("Report generated", StringComparison.Ordinal)),
            StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Changes_made_while_the_summary_is_away_wait_for_the_next_visit()
    {
        await ShowSummaryAsync();
        Tab.ActivatePunchRecordsTab();
        await Until.TrueAsync(() => !_attendance.Busy.IsRunning);
        var runs = Runs;

        Report.PeriodEnd = Report.PeriodEnd!.Value.AddDays(-1);
        _attendance.Roster.DataVersion.BumpManualLogs();
        await Task.Delay(100);
        Assert.Equal(runs, Runs);

        Tab.ActivateSummaryTab();
        await Until.TrueAsync(() => Runs == runs + 1 && !_attendance.Busy.IsRunning, "the revisit's reload");
    }

    [Fact]
    public async Task New_punches_rerun_a_showing_summary()
    {
        await ShowSummaryAsync();

        _attendance.Roster.DataVersion.BumpDeviceLogs();

        await Until.TrueAsync(() => Runs == 2 && !_attendance.Busy.IsRunning, "the re-run");
    }

    [Fact]
    public async Task A_change_during_a_run_is_caught_up_once_after_it()
    {
        await ShowSummaryAsync();
        var release = new TaskCompletionSource();
        var running = _attendance.Busy.RunAsync(visibly: false, _ => release.Task);

        Report.PeriodEnd = Report.PeriodEnd!.Value.AddDays(-1);
        _attendance.Roster.DataVersion.BumpDeviceLogs();
        Assert.Equal(1, Runs);

        release.SetResult();
        await running;
        await Until.TrueAsync(() => Runs == 2 && !_attendance.Busy.IsRunning, "the catch-up run");
        await Task.Delay(100);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public async Task A_failing_run_does_not_retry_itself()
    {
        _attendance.Runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database offline"));
        await Tab.EnsureInitializedAsync();

        Tab.ActivateSummaryTab();
        await Until.TrueAsync(() => Runs >= 1 && !_attendance.Busy.IsRunning, "the first attempt");
        Report.PeriodEnd = Report.PeriodEnd!.Value.AddDays(-1);      // a visible attempt, which says why
        await Task.Delay(300);

        Assert.Equal(2, Runs);
        Assert.False(Report.HasResults);
        _attendance.StatusBar.Received(1).Show(Arg.Any<string>(), Arg.Is<string>(m => m.Contains("database offline")),
            StatusKind.Error, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task A_failed_refresh_keeps_a_report_still_valid_for_the_period()
    {
        await ShowSummaryAsync();
        _attendance.Runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("blip"));

        await Report.RefreshSummaryCommand.Execute();
        Assert.True(Report.HasResults);

        await Report.NextPeriodCommand.Execute();
        await Until.TrueAsync(() => !Report.HasResults && !_attendance.Busy.IsRunning, "the new period's failure");
        Assert.Empty(Report.SummaryRows);
        Assert.False(CanExecute(Report.ExportSummaryCommand));
    }

    [Fact]
    public async Task A_status_tile_narrows_the_grid_and_a_second_click_clears_it()
    {
        await ShowSummaryAsync();

        await Report.ShowStatusDetailCommand.Execute(PunchStatus.Partial);
        Assert.Equal(PunchStatus.Partial, Report.SelectedStatusFilter);
        Assert.Equal(PunchStatus.Partial, Assert.Single(Report.SummaryRowsView).Status);

        await Report.ShowStatusDetailCommand.Execute(PunchStatus.Partial);
        Assert.Null(Report.SelectedStatusFilter);
        Assert.Equal(3, Report.SummaryRowsView.Count);
    }

    [Fact]
    public async Task Unchecking_an_employee_narrows_the_grid_at_once()
    {
        await ShowSummaryAsync();

        TestRoster.Node(Tab.ReportScope.Departments, 4).IsSelected = false;

        Assert.DoesNotContain(Report.SummaryRowsView, row => row.EmployeeId == 4);
        await Until.TrueAsync(() => Runs == 2 && !_attendance.Busy.IsRunning, "the re-run for the new scope");
    }

    [Fact]
    public async Task The_orphaned_tile_lists_its_punches()
    {
        await ShowSummaryAsync();

        await Report.ShowOrphanedDetailCommand.Execute();

        var list = Assert.IsType<PunchListDetailViewModel>(Assert.Single(_shown));
        Assert.Equal("Orphaned -- 1 record", list.Title);
        Assert.Equal(3, Assert.Single(list.Rows).EmployeeId);
    }

    [Fact]
    public async Task Export_saves_and_opens_the_summary()
    {
        await ShowSummaryAsync();
        _savePath = _workbook;

        await Report.ExportSummaryCommand.Execute();

        Assert.True(new FileInfo(_workbook).Length > 0);
        Assert.Equal(_workbook, _opened);
    }

    [Fact]
    public async Task Refresh_or_cancel_follows_the_busy_state()
    {
        await ShowSummaryAsync();
        Assert.Equal("", Report.RefreshOrCancelGlyph);

        var release = new TaskCompletionSource();
        var running = _attendance.Busy.RunAsync(visibly: true, _ => release.Task);
        Assert.Equal("", Report.RefreshOrCancelGlyph);
        Assert.True(CanExecute(Report.RefreshOrCancelSummaryCommand));
        Assert.False(CanExecute(Report.RefreshSummaryCommand));

        release.SetResult();
        await running;
    }
}

public class PunchListDetailViewModelTests
{
    public PunchListDetailViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    [Fact]
    public void An_empty_list_has_nothing_to_export()
    {
        var vm = new PunchListDetailViewModel("Unscheduled", [], []);

        Assert.Equal("Unscheduled -- 0 records", vm.Title);
        Assert.False(((ICommand)vm.ExportCommand).CanExecute(null));
    }

    [Fact]
    public async Task Export_says_where_it_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orphaned-{Guid.NewGuid():N}.xlsx");
        try
        {
            var vm = new PunchListDetailViewModel("Orphaned",
                [new AttendanceLog { EmployeeId = 3, Timestamp = new DateTime(2026, 9, 16, 23, 0, 0) }],
                [TestRoster.Employee(3, "Cruz")]);
            Notice? notice = null;
            vm.PickFileToSave.RegisterHandler(ctx => ctx.SetOutput(path));
            vm.Notify.RegisterHandler(ctx =>
            {
                notice = ctx.Input;
                ctx.SetOutput(RxVoid.Default);
            });

            await vm.ExportCommand.Execute();

            Assert.Equal("Export complete", notice?.Title);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
