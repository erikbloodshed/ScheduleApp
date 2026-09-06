namespace ScheduleApp.Core.Attendance;

/// <summary>
/// One validated row from a manual-entries import workbook -- the format
/// ManualEntryImporter.Import (ScheduleApp.Excel) reads, matching -- for
/// round-trip convenience -- the column layout
/// AttendanceExcelExporter.ExportManualLogsToExcel writes: a single sheet
/// with a header row naming each column, so columns can appear in any order.
/// Recognized headers (matched case-insensitively, leading/trailing
/// whitespace trimmed):
///
///   Required: Id, Date, Time, Punch Type, Reason
///   Optional: Entered By, Employee Name, Department
///
/// "Id" here is the same column ExportManualLogsToExcel writes under that
/// header -- the employee's own Employee ID (Employee.Pin), not a
/// ManualAttendanceLog's own database Id (there isn't one yet; every row
/// this produces is a new entry). "Employee Name"/"Department" are accepted
/// so a person can literally re-export a Manual Entries grid and reuse the
/// same file as an import template, but both are display-only on export and
/// ignored here -- Id is what actually resolves the employee (see
/// ManualEntryImporter.ReadRequiredEmployeeId), the same way the
/// single-entry ManualLogEntryDialog only ever trusts the numeric id half of
/// its own EmployeeBox text, never the name half (see that dialog's
/// ResolveEmployeeId).
///
/// EnteredBy defaults to Environment.UserName when the column is absent or
/// blank on a row, mirroring ManualLogEntryDialog's own EnteredByBox
/// default/Save-time fallback.
/// </summary>
public class ManualEntryImportRow
{
    /// <summary>The employee this entry is for -- Employee.Pin, validated against
    /// the roster at import time (see ManualEntryImporter). Named to match
    /// ManualAttendanceLog.EmployeeId, not the sheet's own "Id" header -- see this
    /// class's own doc comment for why those are the same value under different
    /// names.</summary>
    public int EmployeeId { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>0 = Clock In, 1 = Clock Out -- same convention as
    /// ManualAttendanceLog.PunchType. A manual entry can only ever carry one of
    /// these two (see PunchTypeLabel's own doc comment), so ManualEntryImporter
    /// rejects any other Punch Type cell text rather than accepting the wider
    /// device-only status set (Break In/Out, Overtime In/Out).</summary>
    public int PunchType { get; set; }

    public string Reason { get; set; } = string.Empty;
    public string EnteredBy { get; set; } = string.Empty;
}
