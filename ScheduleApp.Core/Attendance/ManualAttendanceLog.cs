namespace ScheduleApp.Core.Attendance;

/// <summary>
/// One manually-entered punch -- covering the case an employee simply forgot
/// to clock in/out and there's no device record to import, so there's nothing
/// IAttendanceLogRepository.AddLogsAsync could ever pick up. Deliberately its
/// own table (ManualAttendanceLogs, see ScheduleDbContext) rather than a new
/// AttendanceLogSource row mixed into AttendanceLogs itself: AttendanceLogs
/// is meant to be an untouched record of what the punch clock (or a .dat/
/// network import of it) actually reported, and a hand-typed correction
/// living in the same table would blur that. A ManualAttendanceLog also
/// carries two columns a real punch never needs -- Reason and EnteredBy --
/// which wouldn't make sense as nullable clutter on every device row.
///
/// A manual entry never overrides a device punch that already covers the
/// same clock-in/clock-out -- it only fills in when the device side has
/// nothing there at all. See ToAttendanceLog() for how a row here gets
/// merged into the same punch pool a device punch would occupy, and
/// ScheduleApp.Attendance.PunchMatching for where that device-first,
/// manual-fallback preference is actually enforced.
/// </summary>
public class ManualAttendanceLog
{
    public int Id { get; set; }

    /// <summary>The punch clock's own employee code, matching
    /// Employee.Pin -- same convention as AttendanceLog.EmployeeId, so a
    /// manual entry can be matched to a schedule/employee exactly like a real
    /// punch.</summary>
    public int EmployeeId { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>0 = Clock In, 1 = Clock Out -- same convention as
    /// AttendanceLog.PunchType (see StoredPunchLogRow's PunchTypeText
    /// mapping). Not actually consulted by shift matching (neither is the
    /// device equivalent -- see AttendanceLog.PunchType's own remarks), but
    /// kept for display/export and so a person entering one knows which slot
    /// they're filling in.</summary>
    public int PunchType { get; set; }

    /// <summary>Required -- why this punch couldn't come from the device
    /// (e.g. "Forgot to badge in", "Device offline"). Shown alongside the
    /// entry everywhere a merged punch list is displayed, so a manually
    /// entered time is never mistaken for something the clock actually
    /// recorded.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Free-text name of whoever entered this -- there's no user/auth
    /// system in ScheduleApp (single-operator desktop app), so this is just a
    /// typed name rather than a real user reference. Defaults to the current
    /// Windows username in the entry dialog, but editable.</summary>
    public string EnteredBy { get; set; } = string.Empty;

    /// <summary>When this row was written into ScheduleApp's own database --
    /// distinct from Timestamp (when the punch is being recorded as having
    /// happened). Mirrors AttendanceLog.ImportedAt's role.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Projects this into the same shape AttendanceLog matching code
    /// already consumes, so neither shift-calculation strategy nor the Punch
    /// Records grid/export needs a second code path for manual entries -- they
    /// just see one more AttendanceLog in the list, tagged
    /// AttendanceLogSource.Manual. This is always an in-memory projection; it's
    /// never itself written to the AttendanceLogs table (see AttendanceLog.Reason's
    /// remarks). The returned Id is *this* row's ManualAttendanceLog.Id, not an
    /// AttendanceLogs.Id -- the two id spaces are unrelated tables and never
    /// compared against each other, but a caller that needs to act back on a
    /// manual entry (e.g. AttendanceViewModel's Delete button, via
    /// StoredPunchLogRow.Id) needs it preserved somewhere, and Source == Manual
    /// is exactly the tag that says which table an Id here belongs to.</summary>
    public AttendanceLog ToAttendanceLog() => new()
    {
        Id = Id,
        EmployeeId = EmployeeId,
        Timestamp = Timestamp,
        PunchType = PunchType,
        Source = AttendanceLogSource.Manual,
        DeviceSerialNumber = null,
        ImportedAt = CreatedAt,
        Reason = Reason,
        EnteredBy = EnteredBy,
    };
}
