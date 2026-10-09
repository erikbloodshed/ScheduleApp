using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Primitives.Concurrency;
using ScheduleApp.Desktop.Models.PushListener;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class PushListenerViewModelTests : IDisposable
{
    private readonly IPushListenerApiClient _client = Substitute.For<IPushListenerApiClient>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly VirtualTimeSequencer<DateTimeOffset, TimeSpan> _clock = new(
        DateTimeOffset.UnixEpoch, Comparer<DateTimeOffset>.Default, (at, by) => at + by, at => at, span => span);
    private readonly PushListenerViewModel _vm;

    public PushListenerViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _client.TestConnectionAsync().Returns(true);
        _client.GetDevicesAsync().Returns(_ => Task.FromResult(new List<PushListenerDeviceInfo>
        {
            new() { SerialNumber = "B2" },
            new() { SerialNumber = "A1" },
        }));
        _client.GetLogsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(_ => Task.FromResult(new List<PushListenerLogEntry>()));
        _vm = new PushListenerViewModel(_statusBar, new PushListenerSettings { BaseUrl = "http://listener:8080" }, _client, _clock);
    }

    public void Dispose() => _vm.Dispose();

    private void TenSecondsPass() => _clock.AdvanceBy(TimeSpan.FromSeconds(10));

    [Fact]
    public void Nothing_polls_until_connected()
    {
        TenSecondsPass();
        TenSecondsPass();

        _client.DidNotReceive().GetDevicesAsync();
        Assert.False(_vm.IsPollingDevices);
        Assert.Equal("http://listener:8080", _vm.ServerUrl);
    }

    [Fact]
    public async Task A_good_connection_lists_devices_then_polls_every_ten_seconds()
    {
        await _vm.TestConnectionCommand.Execute();

        Assert.True(_vm.IsConnected);
        Assert.Equal("Connected", _vm.ConnectionStatusText);
        Assert.Equal(["A1", "B2"], _vm.Devices.Select(d => d.SerialNumber));
        await _client.Received(1).GetDevicesAsync();

        TenSecondsPass();
        TenSecondsPass();
        await _client.Received(3).GetDevicesAsync();

        _vm.AutoRefresh = false;
        TenSecondsPass();
        await _client.Received(3).GetDevicesAsync();
    }

    [Fact]
    public async Task An_unreachable_listener_stops_polling()
    {
        _vm.AutoRefresh = false;
        _vm.AutoRefresh = true;
        Assert.True(_vm.IsPollingDevices);

        _client.TestConnectionAsync().Returns(false);
        await _vm.TestConnectionCommand.Execute();

        Assert.Equal("Not reachable", _vm.ConnectionStatusText);
        Assert.False(_vm.IsPollingDevices);
    }

    [Fact]
    public async Task A_bad_url_says_so_and_reports_the_failure()
    {
        _client.TestConnectionAsync().ThrowsAsync(new UriFormatException("bad"));

        await _vm.TestConnectionCommand.Execute();

        Assert.Equal("Invalid server URL", _vm.ConnectionStatusText);
        Assert.False(_vm.IsConnected);
        _statusBar.Received(1).Show("Error", Arg.Any<string>(), StatusKind.Error, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Log_auto_refresh_polls_the_log_list_with_the_chosen_level()
    {
        _vm.LogLevelFilter = "Warning";
        _vm.LogAutoRefresh = true;

        TenSecondsPass();
        await _client.Received(1).GetLogsAsync("Warning", null, 300);

        _vm.LogAutoRefresh = false;
        TenSecondsPass();
        await _client.Received(1).GetLogsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Device_actions_need_a_selection_that_survives_a_refresh()
    {
        await _vm.TestConnectionCommand.Execute();
        ICommand resync = _vm.ResyncSelectedDeviceCommand;
        Assert.False(resync.CanExecute(null));

        _vm.SelectedDevice = _vm.Devices.Single(d => d.SerialNumber == "B2");
        Assert.True(resync.CanExecute(null));

        TenSecondsPass();
        Assert.Equal("B2", _vm.SelectedDevice?.SerialNumber);
        Assert.True(resync.CanExecute(null));

        await _vm.ResyncSelectedDeviceCommand.Execute();
        await _client.Received(1).ResyncAttendanceAsync("B2");
    }

    [Fact]
    public void Uptime_follows_health()
    {
        Assert.Equal("", _vm.UptimeDisplay);

        _vm.Health = new PushListenerHealthInfo { UptimeSeconds = 93784 };
        Assert.Equal("1d 2h 3m", _vm.UptimeDisplay);

        _vm.Health = new PushListenerHealthInfo { UptimeSeconds = 125 };
        Assert.Equal("2m 5s", _vm.UptimeDisplay);
    }
}
