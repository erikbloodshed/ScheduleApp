using System.IO;
using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class AttendanceImportViewModelTests : IDisposable
{
    private readonly IAttendanceLogRepository _logs = Substitute.For<IAttendanceLogRepository>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _version = new();
    private readonly AttendanceImportViewModel _vm;
    private readonly string _datFile = Path.Combine(Path.GetTempPath(), $"punches-{Guid.NewGuid():N}.dat");
    private string? _picked;
    private FileRequest? _asked;

    public AttendanceImportViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
        _vm = new AttendanceImportViewModel(_logs, _statusBar, _busy, _version, initialLogDatFile: "C:\\last.dat");
        _vm.PickFileToOpen.RegisterHandler(ctx =>
        {
            _asked = ctx.Input;
            ctx.SetOutput(_picked);
        });

        File.WriteAllLines(_datFile,
        [
            "101\t2026-09-16 07:58:00\t1\t0\t0\t0",
            "101\t2026-09-16 17:02:00\t1\t1\t0\t0",
        ]);
        _logs.AddLogsAsync(Arg.Any<IReadOnlyList<AttendanceLog>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new PunchRecordImportResult
            {
                TotalInFile = call.Arg<IReadOnlyList<AttendanceLog>>().Count,
                NewRecords = 2,
                DuplicateRecords = 0,
            }));
    }

    public void Dispose()
    {
        _busy.Dispose();
        File.Delete(_datFile);
    }

    [Fact]
    public async Task Cancelling_the_picker_imports_nothing()
    {
        await _vm.ImportPunchLogCommand.Execute();

        Assert.Equal("C:\\last.dat", _asked?.FileName);
        Assert.Contains("*.dat", _asked?.Filter);
        await _logs.DidNotReceive().AddLogsAsync(Arg.Any<IReadOnlyList<AttendanceLog>>(), Arg.Any<CancellationToken>());
        Assert.Equal("C:\\last.dat", _vm.LogDatFilePath);
    }

    [Fact]
    public async Task Importing_reads_the_file_records_it_and_reports()
    {
        _picked = _datFile;

        await _vm.ImportPunchLogCommand.Execute();

        await _logs.Received(1).AddLogsAsync(
            Arg.Is<IReadOnlyList<AttendanceLog>>(l => l.Count == 2 && l.All(p => p.EmployeeId == 101)),
            Arg.Any<CancellationToken>());
        Assert.Equal(_datFile, _vm.LogDatFilePath);
        Assert.Equal(1, _version.DeviceLogsVersion);
        _statusBar.Received(1).Show(
            "Success", Arg.Is<string>(m => m.StartsWith("Imported 2 new punch(es)", StringComparison.Ordinal)),
            StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task A_failed_import_is_shown_on_the_status_bar()
    {
        _picked = _datFile;
        _logs.AddLogsAsync(Arg.Any<IReadOnlyList<AttendanceLog>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database offline"));

        await _vm.ImportPunchLogCommand.Execute();

        _statusBar.Received(1).Show("Error", Arg.Is<string>(m => m.Contains("database offline")), StatusKind.Error, Arg.Any<TimeSpan>());
        Assert.Equal(0, _version.DeviceLogsVersion);
    }

    [Fact]
    public async Task Import_is_disabled_while_anything_else_runs()
    {
        ICommand import = _vm.ImportPunchLogCommand;
        bool? enabledWhileBusy = null;

        Assert.True(import.CanExecute(null));
        await _busy.RunAsync(visibly: false, _ =>
        {
            enabledWhileBusy = import.CanExecute(null);
            return Task.CompletedTask;
        });

        Assert.False(enabledWhileBusy);
        Assert.True(import.CanExecute(null));
    }
}
