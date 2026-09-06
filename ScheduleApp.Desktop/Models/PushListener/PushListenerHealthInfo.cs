namespace ScheduleApp.Desktop.Models.PushListener;

/// <summary>
/// Mirrors the JSON shape returned by GET /admin/health on a running
/// ScheduleApp.PushListener instance (see that project's AdminController.GetHealth).
/// Same reasoning as PushListenerDeviceInfo for why this is a separate, hand-written DTO
/// rather than a shared type -- this tab talks to PushListener purely over HTTP.
///
/// Deliberately plain data, no computed display properties (e.g. an "UptimeDisplay" string) --
/// PushListenerViewModel formats those, the same convention ConnectionStatusText already
/// follows for the connection bar this sits next to.
/// </summary>
public class PushListenerHealthInfo
{
    public string Status { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public long UptimeSeconds { get; set; }
    public bool DbReachable { get; set; }
    public int DeviceCount { get; set; }
    public string ListenUrl { get; set; } = "";
}
