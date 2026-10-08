using System.Collections.ObjectModel;
using System.Windows.Threading;
using ScheduleApp.Desktop.Models.PushListener;
using ScheduleApp.Desktop.Services;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

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
/// </summary>
public class PushListenerViewModel : ViewModelBase, IDisposable
{
    private readonly PushListenerApiClient _client;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public ObservableCollection<PushListenerDeviceInfo> Devices { get; } = new();
    public ObservableCollection<PushListenerAttendanceLogInfo> AttendanceRows { get; } = new();
    public ObservableCollection<PushListenerLogEntry> LogEntries { get; } = new();

    /// <summary>ComboBox source for the Logs tab's level filter -- "All" translates to no minimum-level filter server-side, not a literal level value.</summary>
    public IReadOnlyList<string> LogLevelOptions { get; } = ["All", "Information", "Warning", "Error", "Fatal"];

    public string ServerUrl
    {
        get => _serverUrl;
        set => this.RaiseAndSetIfChanged(ref _serverUrl, value);
    }

    private string _serverUrl;

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    private bool _isConnected;

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        set => this.RaiseAndSetIfChanged(ref _connectionStatusText, value);
    }

    private string _connectionStatusText = "Not connected";

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(_autoRefresh, value)) return;
            this.RaisePropertyChanging();
            _autoRefresh = value;
            OnAutoRefreshChanged(value);
            this.RaisePropertyChanged();
        }
    }

    private bool _autoRefresh = true;

    private void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    public PushListenerDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (EqualityComparer<PushListenerDeviceInfo?>.Default.Equals(_selectedDevice, value)) return;
            this.RaisePropertyChanging();
            _selectedDevice = value;
            OnSelectedDeviceChanged(value);
            this.RaisePropertyChanged();
        }
    }

    private PushListenerDeviceInfo? _selectedDevice;

    private void OnSelectedDeviceChanged(PushListenerDeviceInfo? value)
    {
        RequeryCanExecute();
    }

    public string? FilterSn
    {
        get => _filterSn;
        set => this.RaiseAndSetIfChanged(ref _filterSn, value);
    }

    private string? _filterSn;

    public string? FilterPin
    {
        get => _filterPin;
        set => this.RaiseAndSetIfChanged(ref _filterPin, value);
    }

    private string? _filterPin;

    public string? FilterStartTime
    {
        get => _filterStartTime;
        set => this.RaiseAndSetIfChanged(ref _filterStartTime, value);
    }

    private string? _filterStartTime;

    public string? FilterEndTime
    {
        get => _filterEndTime;
        set => this.RaiseAndSetIfChanged(ref _filterEndTime, value);
    }

    private string? _filterEndTime;

    public PushListenerHealthInfo? Health
    {
        get => _health;
        set
        {
            if (EqualityComparer<PushListenerHealthInfo?>.Default.Equals(_health, value)) return;
            this.RaisePropertyChanging();
            _health = value;
            OnHealthChanged(value);
            this.RaisePropertyChanged();
        }
    }

    private PushListenerHealthInfo? _health;

    private void OnHealthChanged(PushListenerHealthInfo? value) => this.RaisePropertyChanged(nameof(UptimeDisplay));

    /// <summary>
    /// Formatted here rather than as a property on PushListenerHealthInfo itself -- that DTO
    /// stays a plain mirror of the wire shape, the same convention every other DTO in
    /// Models/PushListener follows.
    /// </summary>
    public string UptimeDisplay
    {
        get
        {
            if (Health is null)
            {
                return "";
            }

            var span = TimeSpan.FromSeconds(Health.UptimeSeconds);
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
    }

    public string LogLevelFilter
    {
        get => _logLevelFilter;
        set => this.RaiseAndSetIfChanged(ref _logLevelFilter, value);
    }

    private string _logLevelFilter = "All";

    public string? LogSnFilter
    {
        get => _logSnFilter;
        set => this.RaiseAndSetIfChanged(ref _logSnFilter, value);
    }

    private string? _logSnFilter;

    public bool LogAutoRefresh
    {
        get => _logAutoRefresh;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(_logAutoRefresh, value)) return;
            this.RaisePropertyChanging();
            _logAutoRefresh = value;
            OnLogAutoRefreshChanged(value);
            this.RaisePropertyChanged();
        }
    }

    private bool _logAutoRefresh;

    private void OnLogAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _logsRefreshTimer.Start();
        }
        else
        {
            _logsRefreshTimer.Stop();
        }
    }

    private readonly DispatcherTimer _logsRefreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public PushListenerViewModel(IStatusBarService statusBarService, PushListenerSettings settings)
        : base(statusBarService)
    {
        _serverUrl = settings.BaseUrl;
        _client = new PushListenerApiClient(_serverUrl);
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _logsRefreshTimer.Tick += async (_, _) => await RefreshLogsAsync();

        TestConnectionCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(TestConnectionAsync));
        RefreshDevicesCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(RefreshDevicesAsync));
        SearchAttendanceCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(SearchAttendanceAsync));
        RefreshLogsCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(RefreshLogsAsync));
        ResyncSelectedDeviceCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(ResyncSelectedDeviceAsync), CanExecuteFrom(CanActOnSelectedDevice));
        ForceRecheckSelectedDeviceCommand = ReactiveCommand.CreateFromTask(() => RunSafelyAsync(ForceRecheckSelectedDeviceAsync), CanExecuteFrom(CanActOnSelectedDevice));
    }

    public ReactiveCommand<RxVoid, RxVoid> TestConnectionCommand { get; }

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
            _refreshTimer.Stop();
            return;
        }

        IsConnected = reachable;
        ConnectionStatusText = reachable ? "Connected" : "Not reachable";

        if (reachable)
        {
            await RefreshDevicesAsync();
            if (AutoRefresh)
            {
                _refreshTimer.Start();
            }
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    public ReactiveCommand<RxVoid, RxVoid> RefreshDevicesCommand { get; }

    private async Task RefreshDevicesAsync()
    {
        // Health is folded into the same refresh cycle as Devices, on both the 10s timer and
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
            // Not ShowFailure: the auto-refresh timer runs this every few seconds, and a
            // listener that's down would write the same failure to the log on every tick.
            StatusBar.ShowError(ex.Message, "Failed to refresh devices");
        }
    }

    public ReactiveCommand<RxVoid, RxVoid> SearchAttendanceCommand { get; }

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

    public ReactiveCommand<RxVoid, RxVoid> RefreshLogsCommand { get; }

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
            // Not ShowFailure, for the same reason as RefreshDevicesAsync: a timer runs this.
            StatusBar.ShowError(ex.Message, "Failed to refresh logs");
        }
    }

    private bool CanActOnSelectedDevice() => SelectedDevice is not null;

    public ReactiveCommand<RxVoid, RxVoid> ResyncSelectedDeviceCommand { get; }

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

    public ReactiveCommand<RxVoid, RxVoid> ForceRecheckSelectedDeviceCommand { get; }

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
        _refreshTimer.Stop();
        _logsRefreshTimer.Stop();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
