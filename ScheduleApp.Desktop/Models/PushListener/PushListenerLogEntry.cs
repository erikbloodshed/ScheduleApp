namespace ScheduleApp.Desktop.Models.PushListener;

/// <summary>
/// Mirrors the JSON shape returned by GET /admin/logs on a running ScheduleApp.PushListener
/// instance (see that project's AdminController.GetLogs / TryParseCompactJsonLine). Details is
/// already a flattened "Key=Value, Key=Value" string built server-side from that log line's
/// structured properties (SerialNumber, CommandId, etc.) -- kept flat here rather than as a
/// dictionary since this is a DataGrid row, not a tree.
/// </summary>
public class PushListenerLogEntry
{
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? Details { get; set; }
}
