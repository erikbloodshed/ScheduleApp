using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.ZkTeco;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs "Fetch from Device…" -- a second way to get punches into
/// AttendanceLogs, alongside AttendanceImportViewModel's Import…, this one connects out
/// to the terminal over the network instead of reading a file someone exported to USB
/// first. Both end up calling the same IAttendanceLogRepository.AddLogsAsync afterward, so
/// re-running either one after the other is safe -- a punch already on file from one path
/// is just a duplicate of the same punch from the other. Connects using whatever's
/// configured in Settings (see AttendanceSettings/SettingsDialog) -- there's no per-fetch
/// connection dialog, since a deployment only ever talks to one device and a separate
/// dialog asking for the same IP/port/comm key/transport Settings already has was just a
/// redundant place to edit the same four fields.</summary>
public partial class DeviceFetchViewModel : ObservableObject
{
    private readonly IAttendanceLogRepository _attendanceLogRepository;
    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>The device this app talks to -- set once at startup from
    /// AttendanceSettings (local appsettings.json, overridable per-machine by the shared
    /// config file Settings writes to) and never changed at runtime. Changing the device
    /// means changing it in Settings and restarting, same as the connection string --
    /// see SettingsDialog's "offers a restart" behavior.</summary>
    private readonly string? _deviceIp;
    private readonly int _devicePort;
    private readonly uint _deviceCommKey;
    private readonly bool _deviceUseUdp;

    public DeviceFetchViewModel(
        IAttendanceLogRepository attendanceLogRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        string? initialDeviceIp,
        int initialDevicePort,
        uint initialDeviceCommKey,
        string? initialDeviceTransport)
    {
        _attendanceLogRepository = attendanceLogRepository;
        _statusBarService = statusBarService;
        _busy = busy;
        _dataVersion = dataVersion;

        _deviceIp = initialDeviceIp;
        _devicePort = initialDevicePort;
        _deviceCommKey = initialDeviceCommKey;
        _deviceUseUdp = string.Equals(initialDeviceTransport, "Udp", StringComparison.OrdinalIgnoreCase);

        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsRunning))
                FetchFromDeviceCommand.NotifyCanExecuteChanged();
        };
    }

    [RelayCommand(CanExecute = nameof(CanFetchFromDevice))]
    private async Task FetchFromDeviceAsync()
    {
        // No connection dialog -- see the class doc comment above. A blank IP means
        // Settings was never configured for this machine; port/comm key/transport
        // always have a usable default even then.
        var validationError = ValidateDeviceConnection();
        if (validationError is not null)
        {
            _statusBarService.ShowCaution(validationError);
            return;
        }

        // visibly: true -- always an explicit click, never a silent auto-load.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ip = _deviceIp!;
            var port = _devicePort;
            var commKey = _deviceCommKey;
            var transportKind = _deviceUseUdp ? ZkTransportKind.Udp : ZkTransportKind.Tcp;

            // ZkTecoAttendanceLogReader.FetchAttendanceLogs is a synchronous, blocking
            // socket call (see ZkDevice) -- Task.Run keeps it off the UI thread the same
            // way an async EF Core call would be, without needing the ZkTeco project
            // itself to know about Task/async at all. cancellationToken IS observed
            // inside it now, but only between chunks of the bulk attendance-log transfer
            // (see ZkDevice.ReadBufferedData) -- that's the one part of a fetch that can
            // actually run long, since a full log has no server-side date filter and can
            // be thousands of records. The initial connect and the two GetDeviceParam
            // calls before it aren't individually interruptible mid-call, but each is a
            // single quick round trip already bounded by the device timeout, so Cancel
            // clicked during those just takes effect a beat later, at the next chunk
            // boundary, rather than not at all.
            var fetchResult = await Task.Run(() =>
                ZkTecoAttendanceLogReader.FetchAttendanceLogs(ip, port, commKey, transportKind,
                    cancellationToken: cancellationToken));

            var importResult = await _attendanceLogRepository.AddLogsAsync(fetchResult.Logs, cancellationToken);

            // See AttendanceDataVersion's doc comment / AttendanceImportViewModel's own
            // identical check for why this is conditional on NewRecords.
            if (importResult.NewRecords > 0)
                _dataVersion.BumpDeviceLogs();

            var message =
                $"Fetched {fetchResult.Logs.Count} punch(es) from {fetchResult.DeviceName} " +
                $"(serial {fetchResult.DeviceSerialNumber}) at {ip}: " +
                $"{importResult.NewRecords} new, {importResult.DuplicateRecords} already on file.";

            if (fetchResult.SkippedNonNumericUserIds > 0)
            {
                var skippedMessage =
                    $"Note: {fetchResult.SkippedNonNumericUserIds} record(s) had a non-numeric device user id " +
                    "and couldn't be matched to an Employee ID, so they were left out.";

                // One status bar message, not two -- Show() replaces whatever's
                // currently displayed, so a second call here would just hide the first.
                _statusBarService.ShowCaution($"{message} {skippedMessage}", "Partial import");
            }
            else
            {
                _statusBarService.ShowSuccess(message);
            }
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    private bool CanFetchFromDevice() => !_busy.IsRunning;

    /// <summary>Cheap, synchronous checks only -- deliberately doesn't touch the
    /// network, so this can run before setting IsRunning.</summary>
    private string? ValidateDeviceConnection()
    {
        if (string.IsNullOrWhiteSpace(_deviceIp))
            return "⚠ No device IP is configured. Set one in Settings (gear icon) -- Menu → Comm → Ethernet on the terminal itself.";

        if (_devicePort is <= 0 or > 65535)
            return "⚠ The device port configured in Settings is invalid -- it must be between 1 and 65535.";

        return null;
    }
}
