using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// A leave day skips punch matching entirely -- there's nothing to clock in
/// or out of, so the summary is just an identity + status marker.
/// </summary>
internal sealed class LeaveShiftCalculationStrategy : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy) =>
        new()
        {
            Summaries =
            [
                new()
                {
                    EmployeeId = schedule.Employee?.Pin ?? schedule.EmployeeId,
                    EmployeeName = schedule.Employee?.DisplayName ?? "Unknown",
                    Department = schedule.Employee?.Department?.Name ?? "(Unassigned)",
                    ShiftDate = schedule.Date,
                    ScheduleType = ScheduleType.Leave,
                    Status = PunchStatus.Leave,
                    // Copied straight through so PayrollCalculator can read Basic Pay's
                    // Leave case (Employee.DailyRate when paid, 0.00 when unpaid) off the
                    // attendance result without a second schedule lookup -- see
                    // AttendanceSummary.IsPaidLeave.
                    IsPaidLeave = schedule.IsPaidLeave,
                }
            ],
            ClaimedPunches = [],
            UnclaimedPunches = [],
        };
}