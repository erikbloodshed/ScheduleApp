namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One row in the stored-punch-log viewer on the Attendance tab -- a flattened,
/// display-ready projection of an AttendanceLog plus its resolved employee name.
/// Populated once per load; not edited in place, so it doesn't need to be
/// observable.
/// </summary>
public class StoredPunchLogRow
{
    /// <summary>AttendanceLog.Id for a real device punch, or
    /// ManualAttendanceLog.Id for a manual entry (see IsManual) -- the two id
    /// spaces are unrelated, but only one is ever meaningful for a given row,
    /// and IsManual says which. Used by DeleteManualEntryCommand to know which
    /// row to remove; 0/unused for a device punch, since those can't be
    /// deleted from this grid (see IsManual's remarks).</summary>
    public int Id { get; init; }

    /// <summary>True for a row sourced from ManualAttendanceLog rather than a
    /// real AttendanceLogs row -- i.e. Source == "Manual". Controls whether
    /// the grid's Delete button is enabled for this row: a manual entry is
    /// just this app's own typed data and can be corrected by deleting and
    /// re-adding it, but a real device punch is left alone -- AttendanceLogs
    /// is meant to stay an untouched record of what the clock reported.</summary>
    public bool IsManual { get; init; }

    /// <summary>Why a manual entry was needed (see ManualAttendanceLog.Reason).
    /// Always null for a real device punch.</summary>
    public string? Reason { get; init; }

    /// <summary>Who typed in a manual entry (see ManualAttendanceLog.EnteredBy).
    /// Always null for a real device punch. Not shown as its own grid column
    /// (Reason already answers "why"), but round-tripped so EditManualEntryCommand
    /// can prefill it rather than losing whoever originally entered it.</summary>
    public string? EnteredBy { get; init; }

    public int EmployeeId { get; init; }
    public required string EmployeeName { get; init; }
    public required string DepartmentName { get; init; }
    public DateTime Timestamp { get; init; }
    public required string PunchTypeText { get; init; }
    public required string Source { get; init; }
    public DateTime ImportedAt { get; init; }
}
