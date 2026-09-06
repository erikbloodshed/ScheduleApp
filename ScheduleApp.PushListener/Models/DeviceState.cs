using System.Collections.Concurrent;

namespace ScheduleApp.PushListener.Models;

/// <summary>
/// Tracks what we know about one physical device (keyed by its serial number, "SN"). ADMS is
/// device-initiated: the device calls us, never the other way around. So the only way to
/// "send" it a command (e.g. reboot, re-sync) is to queue text here and wait for the device's
/// next poll of /iclock/getrequest -- ported from AdmsServer unchanged.
/// </summary>
public class DeviceState
{
    public string SerialNumber { get; }
    public DateTime LastSeenUtc { get; set; }

    // Simplified sync counters. Real ADMS firmware manages its own stamp progression; this is
    // just enough bookkeeping to echo something sensible back on handshake.
    public long AttLogStamp { get; set; }
    public long OperLogStamp { get; set; }

    public ConcurrentQueue<(int Id, string Text)> PendingCommands { get; } = new();

    private int _commandCounter;

    public DeviceState(string serialNumber)
    {
        SerialNumber = serialNumber;
    }

    public int NextCommandId() => Interlocked.Increment(ref _commandCounter);
}
