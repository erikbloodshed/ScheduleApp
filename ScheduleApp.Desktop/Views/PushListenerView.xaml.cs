using System.Windows;
using System.Windows.Media;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Models.PushListener;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>The Push Listener tab's content -- see PushListenerViewModel. Hosted by
/// PushListenerPage, which hands it the ViewModel.</summary>
public partial class PushListenerView
{
    private static readonly Brush HealthyBrush = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush DegradedBrush = Frozen(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush OtherHealthBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public PushListenerView()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            this.Bind(ViewModel, vm => vm.ServerUrl, v => v.ServerUrlBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.TestConnectionCommand, v => v.TestConnectionButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ConnectionStatusText, v => v.ConnectionStatusText.Text).DisposeWith(d);

            // The health summary, once a refresh has returned one: green when Healthy, red
            // when Degraded, amber otherwise, and the DB line in red when it can't be reached.
            this.OneWayBind(ViewModel, vm => vm.Health, v => v.HealthPanel.Visibility,
                health => health is null ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Health, v => v.HealthDot.Fill, HealthBrush).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Health, v => v.HealthStatusText.Text, health => health?.Status ?? "").DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.UptimeDisplay, v => v.UptimeText.Text, uptime => $"uptime {uptime}").DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Health, v => v.DbStatusText.Text,
                health => health is { DbReachable: false } ? "DB unreachable" : "DB reachable").DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Health, v => v.DbStatusText.Foreground,
                health => health is { DbReachable: false } ? DegradedBrush : MutedBrush()).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Health, v => v.DbStatusText.FontWeight,
                health => health is { DbReachable: false } ? FontWeights.SemiBold : FontWeights.Normal).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.RefreshDevicesCommand, v => v.RefreshDevicesButton).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.AutoRefreshBox.IsChecked, on => on, isChecked => isChecked == true).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Devices, v => v.DevicesGrid.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.SelectedDevice, v => v.DevicesGrid.SelectedItem).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ResyncSelectedDeviceCommand, v => v.ResyncButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ForceRecheckSelectedDeviceCommand, v => v.ForceRecheckButton).DisposeWith(d);

            this.Bind(ViewModel, vm => vm.FilterSn, v => v.FilterSnBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FilterPin, v => v.FilterPinBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FilterStartTime, v => v.FilterStartBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FilterEndTime, v => v.FilterEndBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.SearchAttendanceCommand, v => v.SearchButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.AttendanceRows, v => v.AttendanceGrid.ItemsSource).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.LogLevelOptions, v => v.LogLevelBox.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.LogLevelFilter, v => v.LogLevelBox.SelectedItem).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.LogSnFilter, v => v.LogSnBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RefreshLogsCommand, v => v.RefreshLogsButton).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.LogAutoRefresh, v => v.LogAutoRefreshBox.IsChecked, on => on, isChecked => isChecked == true).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.LogEntries, v => v.LogsGrid.ItemsSource).DisposeWith(d);
        });
    }

    private static Brush HealthBrush(PushListenerHealthInfo? health) => health?.Status switch
    {
        "Healthy" => HealthyBrush,
        "Degraded" => DegradedBrush,
        _ => OtherHealthBrush,
    };

    private Brush MutedBrush() => (Brush)FindResource("MutedForegroundBrush");

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
