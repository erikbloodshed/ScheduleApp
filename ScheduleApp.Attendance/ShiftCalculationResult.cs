using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Attendance;

/// <summary>
/// What one ScheduleEntry's shift calculation produced: the AttendanceSummary
/// row(s) as before, plus which of the employee's punches were actually used
/// as a clock-in/clock-out (ClaimedPunches) versus fell within a window this
/// schedule entry searched but weren't picked (UnclaimedPunches) -- e.g. a
/// duplicate device tap inside an already-satisfied window, or a manual entry
/// dropped because a device punch took priority for the same slot (see
/// PunchMatching). Neither list has any relation to ScheduleType-specific
/// AttendanceSummary fields, so both are plain punches, not summary rows.
///
/// AttendanceWorkflowService aggregates these across every schedule entry an
/// employee has in the run to tell apart two different exceptions: a punch
/// that's in UnclaimedPunches for some entry is Orphaned (it's near a
/// schedule, just not picked); a punch that never shows up in either list for
/// *any* of the employee's entries is Unscheduled (nothing about it relates
/// to any schedule at all). See AttendanceWorkflowService.RunAsync.
/// </summary>
public sealed class ShiftCalculationResult
{
    public required List<AttendanceSummary> Summaries { get; init; }
    public required List<AttendanceLog> ClaimedPunches { get; init; }
    public required List<AttendanceLog> UnclaimedPunches { get; init; }
}
