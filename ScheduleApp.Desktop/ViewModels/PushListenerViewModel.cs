using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using ScheduleApp.Desktop.Models.PushListener;
using ScheduleApp.Desktop.Services;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs the Push Listener tab -- moved here from the now-redundant standalone
/// ScheduleApp.PushListener.ControlPanel WPF app, per the same "port it in, retire the
/// standalone original" pattern already used for AdmsServer.
///
/// This tab is a pure HttpClient wrapper (see PushListenerApiClient) over a running
/// ScheduleApp.PushListener instance's /admin/* endpoints -- it has no direct database
/// access, and deliberately doesn't get any: PushListener is meant to run headless,
/// independent of whether this Desktop app is even open, and this tab is only ever a window
/// into that separate, already-running process, not a second way to reach the same data.
/// It can even point at a PushListener running on a different machine (the normal case in a
/// real deployment) just by changing ServerUrl.
///
/// Two polls refresh it while their switches are on, every ten seconds: the device list
/// (with the listener's health) while IsPollingDevices is, and the log list while
/// LogAutoRefresh is. Each refresh waits for the one before it to finish.
/// </summary>
public partial class PushListenerViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often a poll that's switched on refreshes.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IPushListenerApiClient _client;
    private readonly CompositeDisposable _polls = [];
    private readonly IObservable<bool> _canActOnSelectedDevice;

    public PushListenerViewModel(IStatusBarService statusBarService, PushListenerSettings settings)
        : this(statusBarService, settings, new PushListenerApiClient(settings.BaseUrl), RxSchedulers.MainThreadScheduler)
    {
    }

    /// <summary>The client, and the clock the polls tick on, supplied -- for tests.</summary>
    internal PushListenerViewModel(
        IStatusBarService statusBarService, PushListenerSettings settings,
        IPushListenerApiClient client, ISequencer pollScheduler)
        : base(statusBarService)
    {
        ServerUrl = settings.BaseUrl;
        _client = client;

        _uptimeDisplayHelper = this.WhenAnyValue(x => x.Health)
            .Select(FormatUptime)
            .ToProperty(this, x => x.UptimeDisplay);

        // Auto-refresh switches the device poll on or off straight away, connected or not, as
        // the Auto-refresh box always has; a connection test also turns it on or off.
        this.WhenAnyValue(x => x.AutoRefresh).Skip(1).Subscribe(on => IsPollingDevices = on);

        Poll(this.WhenAnyValue(x => x.IsPollingDevices), RefreshDevicesAsync, pollScheduler).DisposeWith(_polls);
        Poll(this.WhenAnyValue(x => x.LogAutoRefresh), RefreshLogsAsync, pollScheduler).DisposeWith(_polls);

        _canActOnSelectedDevice = this.WhenAnyValue(x => x.SelectedDevice).Select(device => device is not null);

        ReportFailuresOf(
            TestConnectionCommand, RefreshDevicesCommand, SearchAttendanceCommand, RefreshLogsCommand,
            ResyncSelectedDeviceCommand, ForceRecheckSelectedDeviceCommand);
    }

    /// <summary>Runs <paramref name="refresh"/> every <see cref="PollInterval"/> while
    /// <paramref name="switchedOn"/> is true -- the next refresh waiting for the last.</summary>
    private static IDisposable Poll(IObservable<bool> switchedOn, Func<Task> refresh, ISequencer scheduler) =>
        Observable.Switch(switchedOn.Select(on => on ? Signal.Interval(PollInterval, scheduler) : Observable.Never<long>()))
            .Select(_ => Observable.FromAsync(refresh))
            .Concat()
            .Subscribe();

    public ObservableCollection<PushListenerDeviceInfo> Devices { get; } = [];
    public ObservableCollection<PushListenerAttendanceLogInfo> AttendanceRows { get; } = [];
    public ObservableCollection<PushListenerLogEntry> LogEntries { get; } = [];

    /// <summary>ComboBox source for the Logs tab's level filter -- "All" translates to no minimum-level filter server-side, not a literal level value.</summary>
    public IReadOnlyList<string> LogLevelOptions { get; } = ["All", "Information", "Warning", "Error", "Fatal"];

    [Reactive]
    public partial string ServerUrl { get; set; } = string.Empty;

    [Reactive]
    public partial bool IsConnected { get; set; }

    [Reactive]
    public partial string ConnectionStatusText { get; set; } = "Not connected";

    [Reactive]
    public partial bool AutoRefresh { get; set; } = true;

    /// <summary>Whether the device list is being refreshed every <see cref="PollInterval"/> --
    /// on after a successful connection test with AutoRefresh on, or whenever AutoRefresh is
    /// switched on; off after a failed test, or whenever AutoRefresh is switched off.</summary>
    [Reactive]
    internal partial bool IsPollingDevices { get; private set; }

    [Reactive]
    public partial PushListenerDeviceInfo? SelectedDevice { get; set; }

    [Reactive]
    public partial string? FilterSn { get; set; }

    [Reactive]
    public partial string? FilterPin { get; set; }

    [Reactive]
    public partial string? FilterStartTime { get; set; }

    [Reactive]
    public partial string? FilterEndTime { get; set; }

    [Reactive]
    public partial PushListenerHealthInfo? Health { get; set; }

    /// <summary>
    /// Formatted here rather than as a property on PushListenerHealthInfo itself -- that DTO
    /// stays a plain mirror of the wire shape, the same convention every other DTO in
    /// Models/PushListener follows.
    /// </summary>
    [ObservableAsProperty]
    public partial string UptimeDisplay { get; }

    private static string FormatUptime(PushListenerHealthInfo? health)
    {
        if (health is null)
        {
            return "";
        }

        var span = TimeSpan.FromSeconds(health.UptimeSeconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        return $"{span.Minutes}m {span.Seconds}s";
    }

    [Reactive]
    public partial string LogLevelFilter { get; set; } = "All";

    [Reactive]
    public partial string? LogSnFilter { get; set; }

    /// <summary>Refreshes the log list every <see cref="PollInterval"/> while on.</summary>
    [Reactive]
    public partial bool LogAutoRefresh { get; set; }

    [ReactiveCommand]
    private async Task TestConnectionAsync()
    {
        _client.UpdateBaseUrl(ServerUrl);
        ConnectionStatusText = "Checking…";

        bool reachable;
        try
        {
            reachable = await _client.TestConnectionAsync();
        }
        catch (Exception ex)
        {
            // Most likely an invalid URL (e.g. missing "http://") -- constructing the
            // underlying System.Uri is what throws in that case.
            IsConnected = false;
            ConnectionStatusText = "Invalid server URL";
            ShowFailure(ex);
            IsPollingDevices = false;
            return;
        }

        IsConnected = reachable;
        ConnectionStatusText = reachable ? "Connected" : "Not reachable";

        if (reachable)
        {
            await RefreshDevicesAsync();
            if (AutoRefresh)
            {
                IsPollingDevices = true;
            }
        }
        else
        {
            IsPollingDevices = false;
        }
    }

    [ReactiveCommand]
    private async Task RefreshDevicesAsync()
    {
        // Health is folded into the same refresh cycle as Devices, on both the 10s poll and
        // this command's own manual "Refresh Now" button, rather than polled separately --
        // both calls are cheap and the connection bar's health summary should stay in step
        // with whatever this tab last actually checked, not drift on its own cadence.
        // GetHealthAsync swallows its own failures (returns null), so no try/catch needed here.
        Health = await _client.GetHealthAsync();

        try
        {
            var devices = await _client.GetDevicesAsync();

            // Preserve the selection across a refresh (by serial number, since the DTO
            // itself is a new instance each call) rather than always clearing it -- the
            // Resync/Force Recheck buttons would otherwise disable themselves every 10
            // seconds during auto-refresh even mid-use.
            var selectedSerial = SelectedDevice?.SerialNumber;

            Devices.Clear();
            foreach (var device in devices.OrderBy(d => d.SerialNumber))
            {
                Devices.Add(device);
            }

            SelectedDevice = selectedSerial is null
                ? null
                : Devices.FirstOrDefault(d => d.SerialNumber == selectedSerial);
        }
        catch (Exception ex)
        {
            // Not ShowFailure: the poll runs this every few seconds, and a listener that's
            // down would write the same failure to the log on every tick.
            StatusBar.ShowError(ex.Message, "Failed to refresh devices");
        }
    }

    [ReactiveCommand]
    private async Task SearchAttendanceAsync()
    {
        try
        {
            var results = await _client.GetAttendanceAsync(
                FilterSn, FilterPin, take: 500, startTime: FilterStartTime, endTime: FilterEndTime);

            AttendanceRows.Clear();
            foreach (var row in results)
            {
                AttendanceRows.Add(row);
            }

            StatusBar.ShowSuccess($"Found {AttendanceRows.Count} punch(es) via the push listener.");
        }
        catch (Exception ex)
        {
            // Most likely a malformed startTime/endTime, or a non-numeric PIN -- the server
            // validates both and returns 400 rather than an empty result.
            ShowFailure(ex, "Search failed");
        }
    }

    [ReactiveCommand]
    private async Task RefreshLogsAsync()
    {
        try
        {
            var levelParam = LogLevelFilter is "All" or null ? null : LogLevelFilter;
            var results = await _client.GetLogsAsync(levelParam, LogSnFilter, take: 300);

            LogEntries.Clear();
            foreach (var entry in results)
            {
                LogEntries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            // Not ShowFailure, for the same reason as RefreshDevicesAsync: a poll runs this.
            StatusBar.ShowError(ex.Message, "Failed to refresh logs");
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canActOnSelectedDevice))]
    private async Task ResyncSelectedDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            await _client.ResyncAttendanceAsync(SelectedDevice.SerialNumber);
            StatusBar.ShowSuccess(
                $"Resync queued for {SelectedDevice.SerialNumber} -- it'll pick this up on its next poll.");
        }
        catch (Exception ex)
        {
            ShowFailure(ex, "Resync failed");
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canActOnSelectedDevice))]
    private async Task ForceRecheckSelectedDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            await _client.ForceRecheckAsync(SelectedDevice.SerialNumber);
            StatusBar.ShowSuccess(
                $"Force-recheck queued for {SelectedDevice.SerialNumber} -- stamps reset, it'll re-upload everything.");
        }
        catch (Exception ex)
        {
            ShowFailure(ex, "Force recheck failed");
        }
    }

    public void Dispose()
    {
        _polls.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
