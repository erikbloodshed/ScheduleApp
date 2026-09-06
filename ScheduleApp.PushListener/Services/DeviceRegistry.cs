using System.Collections.Concurrent;
using ScheduleApp.PushListener.Models;

namespace ScheduleApp.PushListener.Services;

/// <summary>
/// Shared, in-memory device state -- ported from AdmsServer unchanged. One process = one
/// listener, so a plain ConcurrentDictionary wrapped in a singleton DI service is enough.
/// IClockController reads/writes through this same instance.
///
/// This is intentionally NOT persisted anywhere: restarting the listener forgets sync stamps
/// and queued commands, but not attendance data -- that lives in ScheduleAppDb's
/// AttendanceLogs table via IAttendanceLogRepository, same as every other punch source.
/// LastSeenUtc here is handshake/ping-driven "is the device talking to us at all," which is
/// a different (and currently unexposed) signal from the DB-derived "when was the last punch
/// actually recorded" the Attendance tab's status line reads instead -- see this folder's
/// README for why that distinction was left out of the first version.
/// </summary>
public class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new();

    public IEnumerable<DeviceState> Devices => _devices.Values;

    public DeviceState GetOrAdd(string serialNumber) =>
        _devices.GetOrAdd(serialNumber, sn => new DeviceState(sn));
}
