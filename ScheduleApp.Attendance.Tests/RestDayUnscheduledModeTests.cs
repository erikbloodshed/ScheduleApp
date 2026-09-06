using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using Xunit;
using static ScheduleApp.Attendance.Tests.RestDayTestFixtures;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// The no-schedule mode (schedule.TimeIn/WorkTimeHours both null,
/// RestDayDutyCheckBox left unchecked in the desktop dialog): a plain day
/// off. Punch matching is skipped entirely, the same shape
/// LeaveShiftCalculationStrategy uses -- so nothing punched that day is ever
/// honored, no matter how clean the punch data looks.
/// </summary>
public class RestDayUnscheduledModeTests
{
    [Fact]
    public void NoPunches_ReportsBlankRestDay()
    {
        var employee = Employee();
        var schedule = RestDaySchedule(employee);

        var result = AttendanceCalculator.CalculateShift(schedule, [], DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(ScheduleType.RestDay, summary.ScheduleType);
        Assert.Equal(PunchStatus.RestDay, summary.Status);
        Assert.Null(summary.ClockIn);
        Assert.Null(summary.ClockOut);
        Assert.Equal(0.0, summary.Worked_H);
        Assert.Equal(0.0, summary.NightDiff_H);
        Assert.Equal(0.0, summary.Overtime_H);
        Assert.Equal(0.0, summary.Remain_H);

        Assert.Empty(result.ClaimedPunches);
        Assert.Empty(result.UnclaimedPunches);
    }

    [Fact]
    public void CleanPunchPairs_StillEarnsNothing()
    {
        var employee = Employee();
        var schedule = RestDaySchedule(employee);

        // A textbook 8-5 clock-in/clock-out pair -- exactly what would have
        // earned a full day's Rest Day Duty premium under the old
        // "no schedule = ad-hoc common case" behavior. Left unchecked, none
        // of that applies anymore.
        var punches = new List<AttendanceLog>
        {
            Punch(employee.Pin, new TimeOnly(8, 0)),  // clock in
            Punch(employee.Pin, new TimeOnly(17, 0)), // clock out
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.RestDay, summary.Status);
        Assert.Null(summary.ClockIn);
        Assert.Null(summary.ClockOut);
        Assert.Equal(0.0, summary.Worked_H);
        Assert.Equal(0.0, summary.NightDiff_H);

        // Not claimed by this schedule entry -- it never looked at punches
        // at all, so these fall through to AttendanceWorkflowService's
        // Unscheduled bucket, same as any other punch nothing expects.
        Assert.Empty(result.ClaimedPunches);
        Assert.Empty(result.UnclaimedPunches);
    }

    [Fact]
    public void UnpairedOrMessyPunches_AlsoEarnNothing()
    {
        var employee = Employee();
        var schedule = RestDaySchedule(employee);

        // An odd, dangling punch count -- would have zeroed the day under
        // the old AllPunchesPairCleanly rule too, but for a different
        // reason. Here it's irrelevant: the day never looks at punches
        // regardless of shape.
        var punches = new List<AttendanceLog>
        {
            Punch(employee.Pin, new TimeOnly(8, 0)),
            Punch(employee.Pin, new TimeOnly(12, 0)),
            Punch(employee.Pin, new TimeOnly(13, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(0.0, summary.Worked_H);
        Assert.Null(summary.ClockIn);
        Assert.Null(summary.ClockOut);
        Assert.Equal(PunchStatus.RestDay, summary.Status);
        Assert.Empty(result.ClaimedPunches);
        Assert.Empty(result.UnclaimedPunches);
    }
}
