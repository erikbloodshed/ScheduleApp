using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using Xunit;
using static ScheduleApp.Attendance.Tests.RestDayTestFixtures;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Implementation Phases doc, 2.5 -- the windowed mode (schedule.TimeIn/
/// WorkTimeHours both set): buffer/window matching against the scheduled
/// window, reusing SingleWindowShiftCalculationStrategy's own buffer logic,
/// but only its Complete outcome (both sides found) counts as a duty -- a
/// Partial outcome (one side found) earns nothing here, unlike
/// SingleWindowShiftCalculationStrategy itself.
///
/// Both scenarios below share one schedule: TimeIn 8:00 AM, WorkTimeHours 8h
/// (scheduled TimeOut 4:00 PM) -- the same "Scheduled 8:00 AM-4:00 PM"
/// shape the design doc's §11 worked example (and
/// PayrollCalculatorRestDayWorkedExampleTests.
/// RestDayWithScheduledWindow_PremiumCoversFullHoursPastTheSchedule, which
/// assumes this exact 11-hour figure comes out of the Attendance layer) both
/// use, so the two projects' test suites agree on what the Attendance layer
/// actually produces for that scenario rather than each assuming a different
/// number independently.
/// </summary>
public class RestDayWindowedModeTests
{
    private static readonly TimeOnly ScheduledTimeIn = new(8, 0);
    private static readonly decimal ScheduledWorkTimeHours = 8m; // scheduled TimeOut = 4:00 PM

    [Fact]
    public void BothClockInAndClockOutMatched_CreditsTheFullSpanPastTheScheduledEnd()
    {
        var employee = Employee();
        var schedule = RestDaySchedule(employee, ScheduledTimeIn, ScheduledWorkTimeHours);

        // Clock-in on time; clock-out at 7 PM -- 3 hours past the scheduled
        // 4 PM end, but still inside the default 6h clock-out buffer
        // (10:00 PM), so it's found and counts as this duty's clock-out.
        var punches = new List<AttendanceLog>
        {
            Punch(employee.Pin, new TimeOnly(8, 0)),
            Punch(employee.Pin, new TimeOnly(19, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(ScheduleType.RestDay, summary.ScheduleType);
        Assert.Equal(PunchStatus.RestDay, summary.Status);
        Assert.True(summary.HasScheduledWindow);
        Assert.Equal(new TimeOnly(8, 0), summary.CheckIn);
        Assert.Equal(new TimeOnly(16, 0), summary.CheckOut);
        Assert.Equal(new TimeOnly(8, 0), summary.ClockIn);
        Assert.Equal(new TimeOnly(19, 0), summary.ClockOut);

        // The whole 8 AM-7 PM span, not capped at the scheduled 8 hours --
        // hours past the scheduled end are Rest Day Pay, not a separate
        // Overtime figure (§5).
        Assert.Equal(11.0, summary.Worked_H);
        Assert.Equal(0.0, summary.Overtime_H);
        Assert.Equal(0.0, summary.Remain_H);
    }

    [Fact]
    public void OnlyOneSideMatched_EarnsNothingEvenThoughSingleWindowStrategyWouldReportPartial()
    {
        var employee = Employee();
        var schedule = RestDaySchedule(employee, ScheduledTimeIn, ScheduledWorkTimeHours);

        // Only a clock-in punch -- nothing anywhere near the clock-out window
        // (10:00 AM-10:00 PM). SingleWindowShiftCalculationStrategy would
        // still report this as Partial with the clock-in visible;
        // RestDayShiftCalculationStrategy's windowed mode treats "only one
        // side found" as no duty at all.
        var punches = new List<AttendanceLog>
        {
            Punch(employee.Pin, new TimeOnly(8, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        // The scheduled window still shows (it's not a "no schedule" day),
        // but nothing about the punch that was found surfaces on the summary.
        Assert.Equal(PunchStatus.RestDay, summary.Status);
        Assert.True(summary.HasScheduledWindow);
        Assert.Equal(new TimeOnly(8, 0), summary.CheckIn);
        Assert.Equal(new TimeOnly(16, 0), summary.CheckOut);
        Assert.Null(summary.ClockIn);
        Assert.Null(summary.ClockOut);
        Assert.Equal(0.0, summary.Worked_H);
        Assert.Equal(0.0, summary.NightDiff_H);
        Assert.Equal(0.0, summary.Overtime_H);
        Assert.Equal(0.0, summary.Remain_H);
    }
}
