using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Desktop;

/// <summary>
/// Bound from the "Attendance" section of appsettings.json. Everything here
/// is just a starting default the user can still override in the Attendance
/// tab -- a missing or incomplete section is fine, not fatal.
/// </summary>
public class AttendanceSettings
{
    public string? LogDatFile { get; set; }
    public AttendancePolicy Policy { get; set; } = new();

    /// <summary>ZKTeco terminal IP address (e.g. "192.168.1.201"), for Fetch
    /// from Device -- see Menu -> Comm -> Ethernet on the terminal itself.
    /// This is the only place it's set (no per-fetch dialog -- see
    /// DeviceFetchViewModel); leave it blank and Fetch from Device will show
    /// a reminder to set it here instead of running.</summary>
    public string? DeviceIp { get; set; }

    /// <summary>Defaults to 4370, the standard ZKTeco port -- almost never
    /// needs to change.</summary>
    public int DevicePort { get; set; } = 4370;

    /// <summary>The device's communication password (Menu -> Comm ->
    /// Ethernet/Comm Key). 0 (no password) is the out-of-the-box default on
    /// most units.</summary>
    public uint DeviceCommKey { get; set; } = 0;

    /// <summary>"Tcp" or "Udp" -- most deployments use Tcp; a handful of
    /// firmware/installs only accept Udp. Stored as text (not the ZkTeco
    /// project's own enum) so this settings class doesn't need a reference to
    /// ScheduleApp.ZkTeco just to bind a config value.</summary>
    public string DeviceTransport { get; set; } = "Tcp";

    /// <summary>Pre-filled into ApplyScheduleDialog's work-time-hours field when
    /// setting a brand-new (non-Leave) schedule entry with no existing entry to
    /// prefill from -- purely a starting point the person can still change per
    /// entry; has no effect on any already-saved ScheduleEntry. Defaults to 10,
    /// matching the app's long-standing hardcoded value.</summary>
    public double DefaultWorkTimeHours { get; set; } = 10.0;
}
