using System.IO;
using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The Manual Entries tab and the manual-entry editor that writes to it, over stubbed
/// repositories and the test roster.</summary>
public sealed class ManualEntriesViewModelTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 16);
    private static readonly DateTime End = new(2026, 9, 30);

    private readonly TestRoster _roster = new();
    private readonly IManualAttendanceLogRepository _manualLogs = Substitute.For<IManualAttendanceLogRepository>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion = new();
    private readonly AttendanceEmployeeDirectory _directory;
    private readonly AttendanceTabActivationGate _gate = new() { IsReady = true };
    private readonly ManualEntriesViewModel _entries;
    private readonly ManualEntryEditorViewModel _editor;
    private readonly List<ManualAttendanceLog> _stored = [];
    private readonly string _workbook = Path.Combine(Path.GetTempPath(), $"manual-{Guid.NewGuid():N}.xlsx");
    private int _savedViewState;
    private string? _savePath;
    private string? _opened;
    private bool _confirmAnswer = true;
    private Func<ReactiveViewModel, Task<bool>> _dialog = _ => Task.FromResult(false);

    public ManualEntriesViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
        _directory = new AttendanceEmployeeDirectory(_roster.Repository);

        _stored.Add(new ManualAttendanceLog { Id = 1, EmployeeId = 3, Timestamp = Start.AddHours(8), PunchType = 0, Reason = "Clock In", EnteredBy = "maria" });
        _stored.Add(new ManualAttendanceLog { Id = 2, EmployeeId = 4, Timestamp = Start.AddDays(1).AddHours(17), PunchType = 1, Reason = "Clock Out", EnteredBy = "maria" });
        _manualLogs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_stored.ToList()));
        _manualLogs.AddAsync(Arg.Any<ManualAttendanceLog>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var log = call.Arg<ManualAttendanceLog>();
                log.Id = 10 + _stored.Count;
                _stored.Add(log);
                return Task.FromResult(log);
            });

        _entries = new ManualEntriesViewModel(_manualLogs, _statusBar, _busy, _dataVersion, _directory,
            Start, End, initialIsManualEntriesTabSelected: false, () => _savedViewState++, _gate);
        _editor = new ManualEntryEditorViewModel(_manualLogs, Substitute.For<IAttendanceLogRepository>(), _statusBar,
            _busy, _dataVersion, _directory, _entries);

        _entries.PickFileToSave.RegisterHandler(ctx => ctx.SetOutput(_savePath));
        _entries.PickFileToOpen.RegisterHandler(ctx => ctx.SetOutput(_savePath));
        _entries.OpenFile.RegisterHandler(ctx =>
        {
            _opened = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        _entries.Notify.RegisterHandler(ctx => ctx.SetOutput(RxVoid.Default));
        _editor.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        _editor.ShowDialog.RegisterHandler(async ctx => ctx.SetOutput(await _dialog(ctx.Input)));
    }

    public void Dispose()
    {
        _busy.Dispose();
        _directory.Dispose();
        File.Delete(_workbook);
    }

    private static bool CanExecute(ICommand command, object? parameter = null) => command.CanExecute(parameter);

    [Fact]
    public async Task Loading_lists_the_range_with_employee_names()
    {
        Assert.False(_entries.HasLoadedManualEntries);

        await _entries.LoadManualEntriesCommand.Execute();

        Assert.True(_entries.HasLoadedManualEntries);
        Assert.Equal(2, _entries.ManualEntriesCount);
        Assert.Equal([3, 4], _entries.ManualEntries.Select(r => r.EmployeeId));
        Assert.All(_entries.ManualEntries, row => Assert.True(row.IsManual));
        Assert.Contains("Cruz", _entries.ManualEntries[0].EmployeeName);
        await _manualLogs.Received(1).GetLogsAsync(Start, End.AddDays(1).AddTicks(-1), cancellationToken: Arg.Any<CancellationToken>());
        _statusBar.Received().Show("Success", "Found 2 manual entry(ies) in range.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task A_backwards_range_is_turned_away_without_a_query()
    {
        _entries.ManualEntriesEnd = Start.AddDays(-1);

        await _entries.LoadManualEntriesCommand.Execute();

        Assert.False(_entries.HasLoadedManualEntries);
        await _manualLogs.DidNotReceiveWithAnyArgs().GetLogsAsync(default, default, cancellationToken: default);
        _statusBar.Received().Show(Arg.Any<string>(), "⚠ Start date must not be after end date.", StatusKind.Caution, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Stepping_the_period_moves_the_range_and_reloads()
    {
        var saved = _savedViewState;

        await _entries.NextPeriodCommand.Execute();

        Assert.Equal(new DateTime(2026, 10, 1), _entries.ManualEntriesStart);
        Assert.Equal(new DateTime(2026, 10, 15), _entries.ManualEntriesEnd);
        Assert.True(_entries.HasLoadedManualEntries);
        Assert.True(_savedViewState > saved);
    }

    [Fact]
    public async Task Switching_to_the_tab_reloads_only_when_something_changed()
    {
        _entries.IsManualEntriesTabSelected = true;
        await Until.TrueAsync(() => _entries.HasLoadedManualEntries && !_busy.IsRunning);
        _entries.IsManualEntriesTabSelected = false;

        _entries.IsManualEntriesTabSelected = true;
        await Until.TrueAsync(() => !_busy.IsRunning);
        await _manualLogs.Received(1).GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>());

        _entries.IsManualEntriesTabSelected = false;
        _dataVersion.BumpManualLogs();
        _entries.IsManualEntriesTabSelected = true;
        await Until.TrueAsync(() => _manualLogs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IManualAttendanceLogRepository.GetLogsAsync)) == 2,
            "the reload");
    }

    [Fact]
    public async Task Refresh_or_cancel_toggles_with_the_busy_state()
    {
        Assert.Equal("", _entries.RefreshOrCancelGlyph);
        Assert.Equal("Reload manual entries for this period.", _entries.RefreshOrCancelToolTip);

        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: true, _ => release.Task);
        Assert.Equal("", _entries.RefreshOrCancelGlyph);
        Assert.True(CanExecute(_entries.RefreshOrCancelManualEntriesCommand));     // as Cancel
        Assert.False(CanExecute(_entries.LoadManualEntriesCommand));

        release.SetResult();
        await running;
        Assert.Equal("", _entries.RefreshOrCancelGlyph);
        Assert.True(CanExecute(_entries.LoadManualEntriesCommand));
    }

    [Fact]
    public async Task Export_saves_and_opens_the_workbook()
    {
        _savePath = _workbook;

        await _entries.ExportManualEntriesCommand.Execute();

        Assert.True(new FileInfo(_workbook).Length > 0);
        Assert.Equal(_workbook, _opened);
        _statusBar.Received().Show("Success", $"Saved 2 manual entry(ies) to {_workbook}.", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Import_problems_are_shown_rather_than_half_applied()
    {
        _savePath = _workbook;
        await _entries.ExportManualEntriesCommand.Execute();
        _manualLogs.AddRangeAsync(Arg.Any<IReadOnlyList<ManualAttendanceLog>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManualEntryImportException(["Row 2: unknown Employee ID 99."]));
        Notice? shown = null;
        _entries.Notify.RegisterHandler(ctx =>
        {
            shown = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });

        await _entries.ImportManualEntriesCommand.Execute();

        Assert.Equal("Import problems found", shown?.Title);
        Assert.Contains("unknown Employee ID 99", shown?.Message);
    }

    [Fact]
    public async Task Adding_an_entry_saves_it_and_refreshes_a_loaded_grid()
    {
        await _entries.LoadManualEntriesCommand.Execute();
        var manualLogsVersion = _dataVersion.ManualLogsVersion;
        _dialog = async dialog =>
        {
            var entry = Assert.IsType<ManualLogEntryViewModel>(dialog);
            entry.EmployeeText = "5";
            entry.Date = Start.AddDays(2);
            entry.Time = new TimeOnly(7, 45);
            return await entry.AcceptCommand.Execute();
        };

        await _editor.AddManualEntryCommand.Execute();

        await _manualLogs.Received(1).AddAsync(
            Arg.Is<ManualAttendanceLog>(l => l.EmployeeId == 5 && l.Timestamp == Start.AddDays(2).AddHours(7).AddMinutes(45) && l.Reason == "Clock In"),
            Arg.Any<CancellationToken>());
        Assert.Equal(manualLogsVersion + 1, _dataVersion.ManualLogsVersion);
        Assert.Equal(3, _entries.ManualEntriesCount);
        _statusBar.Received().Show("Success", Arg.Is<string>(m => m.StartsWith("Added manual Clock In for ", StringComparison.Ordinal)),
            StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Editing_reopens_the_entry_and_updates_it()
    {
        await _entries.LoadManualEntriesCommand.Execute();
        ManualLogEntryViewModel? shown = null;
        _dialog = async dialog =>
        {
            shown = (ManualLogEntryViewModel)dialog;
            shown.Time = new TimeOnly(8, 30);
            return await shown.AcceptCommand.Execute();
        };

        await _editor.EditManualEntryCommand.Execute(_entries.ManualEntries[0]);

        Assert.Equal("Edit Manual Entry", shown?.Title);
        await _manualLogs.Received(1).UpdateAsync(
            Arg.Is<ManualAttendanceLog>(l => l.Id == 1 && l.Timestamp == Start.AddHours(8).AddMinutes(30) && l.EnteredBy == "maria"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_asks_first_and_drops_the_row()
    {
        await _entries.LoadManualEntriesCommand.Execute();
        var row = _entries.ManualEntries[1];

        _confirmAnswer = false;
        await _editor.DeleteManualEntryCommand.Execute(row);
        await _manualLogs.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);

        _confirmAnswer = true;
        await _editor.DeleteManualEntryCommand.Execute(row);
        await _manualLogs.Received(1).DeleteAsync(2, Arg.Any<CancellationToken>());
        Assert.Equal([1], _entries.ManualEntries.Select(r => r.Id));
        Assert.Equal(1, _entries.ManualEntriesCount);
    }

    [Fact]
    public async Task Editor_commands_wait_while_busy()
    {
        Assert.True(CanExecute(_editor.AddManualEntryCommand));
        Assert.False(_editor.IsAttendanceBusy);

        var release = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: false, _ => release.Task);
        Assert.True(_editor.IsAttendanceBusy);
        Assert.False(CanExecute(_editor.AddManualEntryCommand));

        release.SetResult();
        await running;
        Assert.True(CanExecute(_editor.AddManualEntryCommand));
    }
}
