namespace ScheduleApp.Core.Attendance;

/// <summary>
/// One raw punch (clock in/out event) -- whether freshly parsed from a .dat
/// file and not yet persisted (Id is 0), or read back from the database.
///
/// EmployeeId here is the punch clock's own employee code, not a ScheduleApp
/// database key -- it matches <see cref="Models.Employee.Pin"/>, the same
/// external id used to match employees on Excel roster/schedule re-import. See
/// AttendanceWorkflowService for where that match happens.
///
/// This class does double duty as both the CsvHelper parse target (see
/// AttendanceLogReader's ClassMap, which maps only EmployeeId/Timestamp/
/// PunchType by column index -- the fields below are untouched by parsing)
/// and the EF Core entity persisted to the AttendanceLogs table (see
/// ScheduleDbContext). Whoever is importing (today: AttendanceViewModel's
/// import command; later: a live ADMS listener) sets Source/DeviceSerialNumber
/// after parsing/receiving a punch; IAttendanceLogRepository.AddLogsAsync sets
/// Id and ImportedAt on the way into the database.
/// </summary>
public class AttendanceLog
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateTime Timestamp { get; set; }
    public int PunchType { get; set; }

    /// <summary>Where this punch came from. Defaults to File since that's the
    /// only source that exists today.</summary>
    public AttendanceLogSource Source { get; set; } = AttendanceLogSource.File;

    /// <summary>Which physical device recorded this punch, when known -- useful
    /// once there's more than one clock (e.g. separate entrances/branches).
    /// Null for now; nothing sets it yet.</summary>
    public string? DeviceSerialNumber { get; set; }

    /// <summary>When this row was written into ScheduleApp's own database --
    /// distinct from Timestamp (when the punch itself happened). An audit
    /// trail, not used by attendance calculation.</summary>
    public DateTime ImportedAt { get; set; }

    /// <summary>Why a manual entry was needed (e.g. "forgot to badge in") --
    /// required on <see cref="ManualAttendanceLog"/> itself, but only ever
    /// carried here on the transient, Source=Manual AttendanceLog that
    /// ManualAttendanceLog.ToAttendanceLog() produces when merging manual
    /// entries into the punch pool for matching/display (see
    /// AttendanceWorkflowService and AttendanceViewModel's Punch Records
    /// query). Null for every real File/Network/Adms punch. Ignored by
    /// ScheduleDbContext -- there is no AttendanceLogs.Reason column, since
    /// this never represents a row actually stored in that table.</summary>
    public string? Reason { get; set; }

    /// <summary>Who typed in a manual entry (see ManualAttendanceLog.EnteredBy).
    /// Same carry-through-only-for-manual-rows treatment as Reason above --
    /// null for every real File/Network/Adms punch, and likewise not a real
    /// AttendanceLogs column.</summary>
    public string? EnteredBy { get; set; }
}
