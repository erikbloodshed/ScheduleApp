namespace ScheduleApp.Desktop.Models.PushListener;

/// <summary>
/// Mirrors the JSON shape returned by GET /admin/attendance on a running
/// ScheduleApp.PushListener instance (see that project's AdminController.GetAttendance).
///
/// Named distinctly from ScheduleApp.Core.Attendance.AttendanceLog (the real entity type used
/// everywhere else in this app, e.g. StoredPunchLogRow) on purpose: this is a DTO describing
/// what a remote PushListener process's HTTP API returned in response to one query, not a row
/// this app read from ScheduleDbContext itself -- even though, in practice, both ultimately
/// describe the same AttendanceLogs table (this tab and the Attendance tab's Punch Records
/// grid can show the exact same row, reached two different ways).
/// </summary>
public class PushListenerAttendanceLogInfo
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateTime Timestamp { get; set; }
    public int PunchType { get; set; }
    public string PunchTypeText { get; set; } = "";
    public string Source { get; set; } = "";
    public string? DeviceSerialNumber { get; set; }

    /// <summary>
    /// UTC, not Philippine local -- SqlAttendanceLogRepository.AddLogsAsync sets this via
    /// DateTime.UtcNow, unlike Timestamp above, which is Philippine local time.
    /// </summary>
    public DateTime ImportedAt { get; set; }
}
