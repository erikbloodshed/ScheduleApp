using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Everything a caller (the WPF ViewModel, a future console harness, etc.)
/// needs after a run: the calculated summaries plus the raw inputs, so both
/// the summary export and the logs export in AttendanceExcelExporter can be
/// driven from one result.
/// </summary>
public class AttendanceRunResult
{
    public List<AttendanceSummary> Summaries { get; init; } = [];
    public List<AttendanceLog> RawLogs { get; init; } = [];

    /// <summary>Employees whose Pin matched at least one punch, used to label
    /// the raw punch-log export with a name/department.</summary>
    public List<Employee> Employees { get; init; } = [];

    /// <summary>Punches that fell within some schedule entry's buffer window
    /// (see ShiftCalculationResult) but weren't picked as its clock-in/out --
    /// e.g. a duplicate device tap a minute after the real clock-in, or a
    /// manual entry dropped because a device punch took priority for the same
    /// slot. "Near a schedule, just not the one that got used" -- as opposed
    /// to UnscheduledPunches below, which never came near any schedule at
    /// all.</summary>
    public List<AttendanceLog> OrphanedPunches { get; init; } = [];

    /// <summary>Punches that no schedule entry this run even considered -- a
    /// punch on a day with no schedule at all for that employee, or under a
    /// pin that doesn't match any employee/schedule entry in the system
    /// whatsoever (e.g. a stale device enrollment). See
    /// AttendanceWorkflowService.RunAsync for how this is told apart from
    /// OrphanedPunches above.</summary>
    public List<AttendanceLog> UnscheduledPunches { get; init; } = [];
}
