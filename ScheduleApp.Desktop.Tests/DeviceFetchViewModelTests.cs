using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Services;
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

    private DeviceFetchViewModel NewViewModel(string? ip, int port = 4370) =>
        new(_logs, _statusBar, _busy, new AttendanceDataVersion(), ip, port, 0, "Tcp");

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
