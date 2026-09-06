namespace ScheduleApp.Desktop.Models.PushListener;

/// <summary>
/// Mirrors the JSON shape returned by GET /admin/devices on a running
/// ScheduleApp.PushListener instance (see that project's AdminController.GetDevices).
/// Deliberately a separate, hand-written DTO rather than a shared type -- this tab talks to
/// PushListener purely over HTTP (see PushListenerApiClient), the same as if it were running
/// on a different machine, which in a real deployment it usually will be.
/// </summary>
public class PushListenerDeviceInfo
{
    public string SerialNumber { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
    public long AttLogStamp { get; set; }
    public long OperLogStamp { get; set; }
    public int PendingCommandCount { get; set; }
}
