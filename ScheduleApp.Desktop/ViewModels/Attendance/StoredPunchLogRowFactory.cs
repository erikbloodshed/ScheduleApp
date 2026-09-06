using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Builds display-ready StoredPunchLogRow grid rows from an AttendanceLog (a real
/// device punch, or a manual entry already projected via ManualAttendanceLog.
/// ToAttendanceLog() -- see StoredPunchLogRow.Source). Shared by PunchRecordsViewModel's
/// Punch Records grid and ManualEntriesViewModel's Manual Entries grid, which both need
/// the exact same employee-name/department lookup and field mapping, just from a
/// different underlying query -- previously each built this inline.</summary>
public static class StoredPunchLogRowFactory
{
    public static StoredPunchLogRow BuildRow(
        AttendanceLog log, IReadOnlyDictionary<int, (string Name, string Department)> employeeInfo)
    {
        employeeInfo.TryGetValue(log.EmployeeId, out var info);
        return new StoredPunchLogRow
        {
            Id = log.Id,
            IsManual = log.Source == AttendanceLogSource.Manual,
            Reason = log.Reason,
            EnteredBy = log.EnteredBy,
            EmployeeId = log.EmployeeId,
            EmployeeName = info.Name ?? "(no matching employee)",
            DepartmentName = info.Name is null ? "" : info.Department,
            Timestamp = log.Timestamp,
            PunchTypeText = PunchTypeLabel.ToText(log.PunchType),
            Source = log.Source.ToString(),

            // Unlike Timestamp (already local -- it's the device's or the manual
            // entry's own wall-clock time), ImportedAt is written as DateTime.UtcNow
            // (see SqlAttendanceLogRepository.AddLogsAsync / ManualAttendanceLog.
            // CreatedAt). SQL Server's datetime2 has no timezone of its own, so it
            // comes back from the database as Kind=Unspecified holding that same UTC
            // clock value -- ToLocalTime() treats Unspecified as UTC and converts it,
            // so this grid shows "just now" rather than a value offset by the
            // machine's UTC offset.
            ImportedAt = log.ImportedAt.ToLocalTime()
        };
    }

    /// <summary>Employee display name + department, keyed by Pin, used to populate
    /// both the Employee Name and Department columns in either grid. Takes the employee
    /// list as a parameter -- both callers already fetched it for their own purposes (via
    /// AttendanceEmployeeDirectory) and shouldn't each query it separately. Department
    /// comes pre-populated on each Employee via IScheduleRepository.
    /// GetDepartmentsWithEmployeesAsync's Include(d => d.Employees) -- EF Core fixes up
    /// the inverse Employee.Department navigation for entities materialized together in
    /// that same query, even under AsNoTracking, so e.Department is already populated
    /// here with no extra query (the same thing AttendanceExcelExporter.ExportLogsToExcel
    /// relies on for its own Department column). Unassigned employees simply have a null
    /// Department, shown as "(Unassigned)" below.</summary>
    public static Dictionary<int, (string Name, string Department)> BuildEmployeeInfoByPin(
        IReadOnlyList<Employee> employees)
    {
        return employees
            // Pin is unique per ScheduleDbContext's index, so this can't collide.
            .ToDictionary(e => e.Pin, e => (e.DisplayName, e.Department?.Name ?? "(Unassigned)"));
    }
}
