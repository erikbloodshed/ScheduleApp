using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// An official-business day skips punch matching entirely -- no punches are
/// looked at, so ClaimedPunches/UnclaimedPunches always come back empty --
/// but unlike LeaveShiftCalculationStrategy it's expected to carry a real
/// TimeIn/WorkTimeHours (the Set Schedule dialog collects both for
/// OfficialBusiness the same way it does for Normal), because the employee
/// is still credited for the day. Whenever both are present, ClockIn/ClockOut
/// are set equal to that scheduled TimeIn/TimeOut and WorkedDuration/WorkedHours equal
/// to WorkTimeHours -- as if the whole scheduled window was worked -- so the
/// Excel summary report and the in-app grid show real, non-blank times and
/// totals for the day (and so those hours count toward the totals row) the
/// same way a Complete Normal day would, rather than singling official
/// business out as a blank exception. A legacy/malformed entry missing either
/// field (e.g. one saved before this field pair was required for
/// OfficialBusiness) falls back to the old blank-row shape.
///
/// Deliberately never sets NightDiffDuration/NightDiffHours, even when the scheduled
/// TimeIn/TimeOut window above overlaps AttendancePolicy.NightDiffStart/
/// NightDiffEnd -- an official-business day is credited for the hours, not
/// for actually being on the clock during them, and night diff only ever
/// pays for real worked time (see NightDifferentialCalculator). Both fields
/// default to zero and this strategy leaves them alone. AttendanceExcelExporter
/// mirrors this by skipping the night-diff formula/value entirely for an
/// OfficialBusiness row rather than deriving one from ClockIn/ClockOut, since
/// those two are set equal to CheckIn/CheckOut here and would otherwise look
/// like a real (and possibly night-diff-eligible) punch pair.
/// </summary>
internal sealed class OfficialBusinessShiftCalculationStrategy : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var summary = new AttendanceSummary
        {
            EmployeeId = schedule.Employee?.Pin ?? schedule.EmployeeId,
            EmployeeName = schedule.Employee?.DisplayName ?? "Unknown",
            Department = schedule.Employee?.Department?.Name ?? "(Unassigned)",
            ShiftDate = schedule.Date,
            ScheduleType = ScheduleType.OfficialBusiness,
            Status = PunchStatus.OfficialBusiness,
        };

        if (schedule.TimeIn is { } scheduledTimeIn && schedule.WorkTimeHours is { } workTimeHours)
        {
            var scheduledTimeOut = schedule.TimeOut!.Value; // derived from TimeIn + WorkTimeHours, so non-null here

            summary.HasScheduledWindow = true;
            summary.CheckIn = scheduledTimeIn;
            summary.CheckOut = scheduledTimeOut;
            summary.Span = workTimeHours;

            // Credited as fully worked -- no device/manual punch is expected or
            // required for an official-business day, so ClockIn/ClockOut simply
            // mirror the scheduled window rather than staying blank.
            summary.ClockIn = scheduledTimeIn;
            summary.ClockOut = scheduledTimeOut;

            summary.WorkedHours = (double)workTimeHours;
            summary.WorkedDuration = TimeSpan.FromHours(summary.WorkedHours);
        }
        else
        {
            // No scheduled window on this entry -- keep the old blank shape
            // rather than writing a misleading Span of 0.
            summary.Span = null;
        }

        return new ShiftCalculationResult
        {
            Summaries = [summary],
            ClaimedPunches = [],
            UnclaimedPunches = [],
        };
    }
}
