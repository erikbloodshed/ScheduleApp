using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Desktop.Models.PushListener;
using ScheduleApp.Desktop.Services;

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
public partial class PushListenerViewModel : ObservableObject, IDisposable
{
    private readonly IStatusBarService _statusBarService;
    private readonly PushListenerApiClient _client;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public ObservableCollection<PushListenerDeviceInfo> Devices { get; } = new();
    public ObservableCollection<PushListenerAttendanceLogInfo> AttendanceRows { get; } = new();
    public ObservableCollection<PushListenerLogEntry> LogEntries { get; } = new();

    /// <summary>ComboBox source for the Logs tab's level filter -- "All" translates to no minimum-level filter server-side, not a literal level value.</summary>
    public IReadOnlyList<string> LogLevelOptions { get; } = ["All", "Information", "Warning", "Error", "Fatal"];

    [ObservableProperty]
    private string serverUrl;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string connectionStatusText = "Not connected";

    [ObservableProperty]
    private bool autoRefresh = true;

    partial void OnAutoRefreshChanged(bool value)
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

    [ObservableProperty]
    private PushListenerDeviceInfo? selectedDevice;

    partial void OnSelectedDeviceChanged(PushListenerDeviceInfo? value)
    {
        ResyncSelectedDeviceCommand.NotifyCanExecuteChanged();
        ForceRecheckSelectedDeviceCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string? filterSn;

    [ObservableProperty]
    private string? filterPin;

    [ObservableProperty]
    private string? filterStartTime;

    [ObservableProperty]
    private string? filterEndTime;

    [ObservableProperty]
    private PushListenerHealthInfo? health;

    partial void OnHealthChanged(PushListenerHealthInfo? value) => OnPropertyChanged(nameof(UptimeDisplay));

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

    [ObservableProperty]
    private string logLevelFilter = "All";

    [ObservableProperty]
    private string? logSnFilter;

    [ObservableProperty]
    private bool logAutoRefresh;

    partial void OnLogAutoRefreshChanged(bool value)
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
    {
        _statusBarService = statusBarService;
        serverUrl = settings.BaseUrl;
        _client = new PushListenerApiClient(serverUrl);
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _logsRefreshTimer.Tick += async (_, _) => await RefreshLogsAsync();
    }

    [RelayCommand]
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
            _statusBarService.ShowError(ex.Message);
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

    [RelayCommand]
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
            _statusBarService.ShowError(ex.Message, "Failed to refresh devices");
        }
    }

    [RelayCommand]
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

            _statusBarService.ShowSuccess($"Found {AttendanceRows.Count} punch(es) via the push listener.");
        }
        catch (Exception ex)
        {
            // Most likely a malformed startTime/endTime, or a non-numeric PIN -- the server
            // validates both and returns 400 rather than an empty result.
            _statusBarService.ShowError(ex.Message, "Search failed");
        }
    }

    [RelayCommand]
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
            _statusBarService.ShowError(ex.Message, "Failed to refresh logs");
        }
    }

    private bool CanActOnSelectedDevice() => SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanActOnSelectedDevice))]
    private async Task ResyncSelectedDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            await _client.ResyncAttendanceAsync(SelectedDevice.SerialNumber);
            _statusBarService.ShowSuccess(
                $"Resync queued for {SelectedDevice.SerialNumber} -- it'll pick this up on its next poll.");
        }
        catch (Exception ex)
        {
            _statusBarService.ShowError(ex.Message, "Resync failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelectedDevice))]
    private async Task ForceRecheckSelectedDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            await _client.ForceRecheckAsync(SelectedDevice.SerialNumber);
            _statusBarService.ShowSuccess(
                $"Force-recheck queued for {SelectedDevice.SerialNumber} -- stamps reset, it'll re-upload everything.");
        }
        catch (Exception ex)
        {
            _statusBarService.ShowError(ex.Message, "Force recheck failed");
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _logsRefreshTimer.Stop();
        _client.Dispose();
    }
}
