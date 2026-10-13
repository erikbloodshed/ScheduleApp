using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class DeviceFetchViewModelTests : IDisposable
{
    private readonly IAttendanceLogRepository _logs = Substitute.For<IAttendanceLogRepository>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;

    public DeviceFetchViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
    }

    public void Dispose() => _busy.Dispose();

    private DeviceFetchViewModel NewViewModel(string? ip, int port = 4370, DeviceFetchViewModel.DeviceFetch? device = null) =>
        new(_logs, _statusBar, _busy, new AttendanceDataVersion(), ip, port, 0, "Tcp", device);

    [Fact]
    public async Task A_device_that_doesnt_answer_is_reported_as_the_device_not_the_database()
    {
        const string deviceSays = "Could not open a TCP connection to 10.0.0.5:4370 within 4000 ms (attempt 2 of 2).";
        var vm = NewViewModel("10.0.0.5", device: (_, _, _, _, _) => throw new TimeoutException(deviceSays));

        await vm.FetchFromDeviceCommand.Execute();

        _statusBar.Received(1).Show("Couldn't fetch from the device", deviceSays, StatusKind.Error, Arg.Any<TimeSpan>());
        _statusBar.DidNotReceive().Show(Arg.Any<string>(), Failure.TimedOutText, Arg.Any<StatusKind>(), Arg.Any<TimeSpan>());
        await _logs.DidNotReceive().AddLogsAsync(Arg.Any<IReadOnlyList<AttendanceLog>>(), Arg.Any<CancellationToken>());
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task What_the_device_returns_is_saved_and_counted()
    {
        var punches = new List<AttendanceLog> { new() { EmployeeId = 3, Timestamp = new DateTime(2026, 9, 1, 8, 0, 0) } };
        _logs.AddLogsAsync(Arg.Any<IReadOnlyList<AttendanceLog>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PunchRecordImportResult { NewRecords = 1, DuplicateRecords = 0 }));
        var vm = NewViewModel("10.0.0.5", device: (_, _, _, _, _) => new Data.Attendance.ZkTecoAttendanceLogReader.FetchResult
        {
            Logs = punches,
            SkippedNonNumericUserIds = 0,
            DeviceName = "MB560",
            DeviceSerialNumber = "SN1",
        });

        await vm.FetchFromDeviceCommand.Execute();

        await _logs.Received(1).AddLogsAsync(punches, Arg.Any<CancellationToken>());
        _statusBar.Received(1).Show(Arg.Any<string>(), Arg.Is<string>(m => m.Contains("Fetched 1 punch(es) from MB560") && m.Contains("1 new")),
            StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Theory]
    [InlineData(null, 4370, "No device IP is configured")]
    [InlineData("10.0.0.5", 0, "device port configured in Settings is invalid")]
    public async Task A_missing_or_bad_setting_is_explained_and_nothing_is_fetched(string? ip, int port, string explanation)
    {
        var vm = NewViewModel(ip, port);

        await vm.FetchFromDeviceCommand.Execute();

        _statusBar.Received(1).Show(Arg.Any<string>(), Arg.Is<string>(m => m.Contains(explanation)), StatusKind.Caution, Arg.Any<TimeSpan>());
        await _logs.DidNotReceive().AddLogsAsync(Arg.Any<IReadOnlyList<AttendanceLog>>(), Arg.Any<CancellationToken>());
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task Fetch_is_disabled_while_anything_else_runs()
    {
        ICommand fetch = NewViewModel("10.0.0.5").FetchFromDeviceCommand;
        bool? enabledWhileBusy = null;

        await _busy.RunAsync(visibly: false, _ =>
        {
            enabledWhileBusy = fetch.CanExecute(null);
            return Task.CompletedTask;
        });

        Assert.False(enabledWhileBusy);
        Assert.True(fetch.CanExecute(null));
    }
}
