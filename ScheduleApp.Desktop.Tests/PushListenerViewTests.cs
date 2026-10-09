using System.Windows;
using System.Windows.Media;
using NSubstitute;
using ReactiveUI.Primitives.Concurrency;
using ScheduleApp.Desktop.Models.PushListener;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class PushListenerViewTests
{
    private readonly IPushListenerApiClient _client = Substitute.For<IPushListenerApiClient>();

    /// <summary>A clock nobody advances: a poll switched on here never ticks. (An immediate
    /// scheduler would run a ten-second interval by blocking the UI thread for it.)</summary>
    private readonly VirtualTimeSequencer<DateTimeOffset, TimeSpan> _stoppedClock = new(
        DateTimeOffset.UnixEpoch, Comparer<DateTimeOffset>.Default, (at, by) => at + by, at => at, span => span);

    private PushListenerViewModel NewViewModel() =>
        new(Substitute.For<IStatusBarService>(), new PushListenerSettings { BaseUrl = "http://listener:8080" },
            _client, _stoppedClock);

    /// <summary>The view, with a fresh ViewModel, in a window on screen.</summary>
    private Task WithViewAsync(Func<PushListenerView, Task> test) => UiThread.RunAsync(async () =>
    {
        var view = new PushListenerView { ViewModel = NewViewModel() };
        var window = await UiThread.ShowAsync(new Window { Content = view, Width = 1100, Height = 600 });
        try
        {
            await test(view);
        }
        finally
        {
            window.Close();
            view.ViewModel!.Dispose();
        }
    });

    [Fact]
    public Task The_page_hands_its_view_model_to_the_view() => UiThread.RunAsync(() =>
    {
        using var vm = NewViewModel();

        var page = new PushListenerPage(vm);

        Assert.Same(vm, page.ViewModel);
        Assert.Same(vm, page.ListenerView.ViewModel);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Server_url_and_filters_bind_both_ways() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        Assert.Equal("http://listener:8080", view.ServerUrlBox.Text);

        view.ServerUrlBox.Text = "http://other:9000";
        view.FilterPinBox.Text = "101";
        await UiThread.IdleAsync();
        Assert.Equal("http://other:9000", vm.ServerUrl);
        Assert.Equal("101", vm.FilterPin);

        vm.FilterSn = "A1";
        await UiThread.IdleAsync();
        Assert.Equal("A1", view.FilterSnBox.Text);
    });

    [Fact]
    public Task Check_boxes_bind_both_ways() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        Assert.True(view.AutoRefreshBox.IsChecked);
        Assert.False(view.LogAutoRefreshBox.IsChecked);

        view.AutoRefreshBox.IsChecked = false;
        view.LogAutoRefreshBox.IsChecked = true;
        await UiThread.IdleAsync();
        Assert.False(vm.AutoRefresh);
        Assert.True(vm.LogAutoRefresh);

        vm.AutoRefresh = true;
        await UiThread.IdleAsync();
        Assert.True(view.AutoRefreshBox.IsChecked);
    });

    [Fact]
    public Task Log_level_picker_binds_both_ways() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        Assert.Same(vm.LogLevelOptions, view.LogLevelBox.ItemsSource);
        Assert.Equal("All", view.LogLevelBox.SelectedItem);

        view.LogLevelBox.SelectedItem = "Error";
        await UiThread.IdleAsync();
        Assert.Equal("Error", vm.LogLevelFilter);

        vm.LogLevelFilter = "Warning";
        await UiThread.IdleAsync();
        Assert.Equal("Warning", view.LogLevelBox.SelectedItem);
    });

    [Fact]
    public Task Health_summary_appears_and_colours_itself() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        Assert.Equal(Visibility.Collapsed, view.HealthPanel.Visibility);

        vm.Health = new PushListenerHealthInfo { Status = "Healthy", DbReachable = true, UptimeSeconds = 125 };
        await UiThread.IdleAsync();
        Assert.Equal(Visibility.Visible, view.HealthPanel.Visibility);
        Assert.Equal(Color.FromRgb(0x22, 0xC5, 0x5E), ((SolidColorBrush)view.HealthDot.Fill).Color);
        Assert.Equal("Healthy", view.HealthStatusText.Text);
        Assert.Equal("uptime 2m 5s", view.UptimeText.Text);
        Assert.Equal("DB reachable", view.DbStatusText.Text);
        Assert.Equal(FontWeights.Normal, view.DbStatusText.FontWeight);

        vm.Health = new PushListenerHealthInfo { Status = "Degraded", DbReachable = false };
        await UiThread.IdleAsync();
        Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), ((SolidColorBrush)view.HealthDot.Fill).Color);
        Assert.Equal("DB unreachable", view.DbStatusText.Text);
        Assert.Equal(FontWeights.SemiBold, view.DbStatusText.FontWeight);
    });

    [Fact]
    public Task Grids_buttons_and_selection_are_wired() => WithViewAsync(async view =>
    {
        var vm = view.ViewModel!;
        Assert.Same(vm.Devices, view.DevicesGrid.ItemsSource);
        Assert.Same(vm.AttendanceRows, view.AttendanceGrid.ItemsSource);
        Assert.Same(vm.LogEntries, view.LogsGrid.ItemsSource);
        Assert.Same(vm.TestConnectionCommand, view.TestConnectionButton.Command);
        Assert.Same(vm.SearchAttendanceCommand, view.SearchButton.Command);
        Assert.False(view.ResyncButton.IsEnabled);

        vm.Devices.Add(new PushListenerDeviceInfo { SerialNumber = "A1" });
        vm.SelectedDevice = vm.Devices[0];
        await UiThread.IdleAsync();
        Assert.Same(vm.Devices[0], view.DevicesGrid.SelectedItem);
        Assert.True(view.ResyncButton.IsEnabled);
    });
}
