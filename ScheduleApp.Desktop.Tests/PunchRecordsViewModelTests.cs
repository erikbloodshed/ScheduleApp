using System.IO;
using System.Reactive.Linq;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The Punch Records page's ViewModel, through the whole Attendance tab.</summary>
public sealed class PunchRecordsViewModelTests : IDisposable
{
    private readonly TestAttendance _attendance = new();
    private readonly string _workbook = Path.Combine(Path.GetTempPath(), $"punches-{Guid.NewGuid():N}.xlsx");
    private string? _savePath;
    private string? _opened;

    public PunchRecordsViewModelTests()
    {
        _attendance.Roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Core.Models.Department> { _attendance.Roster.Bakery, _attendance.Roster.Kitchen }));
        _attendance.Roster.Repository.GetUnassignedEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_attendance.Roster.Unassigned));
        _attendance.Logs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<AttendanceLog>
            {
                new() { EmployeeId = 3, Timestamp = DateTime.Today.AddHours(8), PunchType = 0 },
                new() { EmployeeId = 1, Timestamp = DateTime.Today.AddHours(7), PunchType = 0 },
            }));

        Records.PickFileToSave.RegisterHandler(ctx => ctx.SetOutput(_savePath));
        Records.OpenFile.RegisterHandler(ctx =>
        {
            _opened = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
    }

    public void Dispose()
    {
        _attendance.Dispose();
        File.Delete(_workbook);
    }

    private PunchRecordsViewModel Records => _attendance.ViewModel.PunchRecords;

    [Fact]
    public async Task Opening_the_page_loads_the_device_punches_quietly()
    {
        await _attendance.ViewModel.EnsureInitializedAsync();
        _attendance.ViewModel.ActivatePunchRecordsTab();
        await Until.TrueAsync(() => Records.HasLoadedStoredLogs && !_attendance.Busy.IsRunning, "the punches");

        Assert.Equal(2, Records.StoredLogsCount);
        Assert.Equal(2, Records.StoredLogsView.Count);
        _attendance.StatusBar.DidNotReceive().Show(Arg.Any<string>(), Arg.Is<string>(m => m.StartsWith("Found", StringComparison.Ordinal)),
            Arg.Any<StatusKind>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Typing_suggests_but_only_a_pick_filters_the_grid()
    {
        await Records.LoadStoredLogsCommand.Execute();
        await _attendance.ViewModel.EnsureInitializedAsync();     // warms the suggestion cache

        Records.LogViewSearchText = "Kit";
        await Until.TrueAsync(() => Records.IsLogViewSuggestionsOpen, "the suggestions");
        Assert.Contains(Records.LogViewSuggestions, s => s.Display == "Kitchen (Department)");
        Assert.Equal(2, Records.StoredLogsView.Count);             // not filtered yet

        await Records.SelectLogViewSuggestionCommand.Execute(Records.LogViewSuggestions.First(s => s.InsertValue == "Kitchen"));
        Assert.False(Records.IsLogViewSuggestionsOpen);
        Assert.Equal("Kitchen", Records.LogViewSearchText);
        Assert.Equal(3, Assert.Single(Records.StoredLogsView).EmployeeId);

        Records.LogViewSearchText = "";
        Assert.Equal(2, Records.StoredLogsView.Count);
    }

    [Fact]
    public async Task Export_saves_every_punch_in_range_and_opens_it()
    {
        await Records.LoadStoredLogsCommand.Execute();
        await Records.SelectLogViewSuggestionCommand.Execute(new PunchSearchSuggestion("x", "Kitchen"));
        _savePath = _workbook;

        await Records.ExportStoredLogsCommand.Execute();

        Assert.True(new FileInfo(_workbook).Length > 0);
        Assert.Equal(_workbook, _opened);
        _attendance.StatusBar.Received().Show("Success", $"Saved 2 punch(es) to {_workbook}.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Stepping_the_period_reloads_with_feedback()
    {
        await Records.PreviousPeriodCommand.Execute();

        Assert.True(Records.HasLoadedStoredLogs);
        _attendance.StatusBar.Received().Show("Success", "Found 2 punch(es) in range.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Reload_toggles_to_cancel_while_busy()
    {
        Assert.Equal("Reload", Records.RefreshOrCancelContent);

        var release = new TaskCompletionSource();
        var running = _attendance.Busy.RunAsync(visibly: true, _ => release.Task);
        Assert.Equal("Cancel", Records.RefreshOrCancelContent);
        Assert.Equal("", Records.RefreshOrCancelIcon);

        release.SetResult();
        await running;
        Assert.Equal("Reload", Records.RefreshOrCancelContent);
    }
}
