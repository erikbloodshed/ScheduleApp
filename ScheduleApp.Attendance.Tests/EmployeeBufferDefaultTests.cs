using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Covers the employee-level buffer-default feature: Employee.
/// ClockInBufferBeforeHours/ClockInBufferAfterHours/ClockOutBufferBeforeHours/
/// ClockOutBufferAfterHours, resolved through NormalBufferResolver's three-tier
/// cascade (a specific day's own ScheduleEntry override wins first; failing
/// that, the employee's own default; failing that, the AttendancePolicy
/// default). NormalBufferResolver itself is internal, so -- same convention as
/// every other test class in this project (see RestDayTestFixtures' own doc
/// comment) -- these go through the public AttendanceCalculator.CalculateShift
/// entry point rather than calling the resolver directly, exercising both
/// strategies that actually read it: SingleWindowShiftCalculationStrategy
/// (Normal) and RestDayShiftCalculationStrategy (RestDay's windowed sub-case).
///
/// Every scenario below uses a schedule TimeIn of 8:00 AM with an 8-hour
/// WorkTimeHours (TimeOut 4:00 PM, same shape as LateInEarlyOutGraceTests'
/// own NormalSchedule), and a "tight" employee default of 15 minutes
/// (0.25h) either side -- deliberately much narrower than
/// RestDayTestFixtures.DefaultPolicy's own wide 2h/6h Normal buffers, so a
/// punch placed between the two windows (an hour early, e.g.) unambiguously
/// shows which one the calculator actually searched.
/// </summary>
public class EmployeeBufferDefaultTests
{
    private static readonly DateOnly Date = RestDayTestFixtures.Date;

    private static Employee EmployeeWithTightBufferDefault() => new()
    {
        Pin = 1001,
        LastName = "Cruz",
        FirstName = "Juan",
        ClockInBufferBeforeHours = 0.25,
        ClockInBufferAfterHours = 0.25,
        ClockOutBufferBeforeHours = 0.25,
        ClockOutBufferAfterHours = 0.25,
    };

    private static ScheduleEntry NormalSchedule(Employee employee) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.Normal,
        Date = Date,
        TimeIn = new TimeOnly(8, 0),
        WorkTimeHours = 8m, // scheduled TimeOut = 4:00 PM
    };

    [Fact]
    public void Normal_NoEmployeeDefaultSet_FallsBackToPolicyDefault()
    {
        // Same employee shape as every other test class's fixture (no buffer
        // fields set) -- a punch an hour early is well within
        // DefaultPolicy's own wide ClockInBufferBefore (2h), so it should
        // still match as a clock-in.
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(7, 0)), // 1h early
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
    }

    [Fact]
    public void Normal_EmployeeDefaultSet_NarrowsTheSearchWindow()
    {
        // The employee's own tight (15-minute) default takes priority over
        // the policy's wide one -- an hour-early punch that would have
        // matched under the policy default now falls outside the employee's
        // own window, so it's never picked up as the clock-in at all.
        var employee = EmployeeWithTightBufferDefault();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(7, 0)), // 1h early -- outside the 15-min employee window
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        // Only the 4:00 PM clock-out was found within its own (also tight)
        // window -- the 7:00 AM punch matched nothing, so this is Partial,
        // not Complete.
        Assert.Equal(PunchStatus.Partial, summary.Status);
        Assert.Null(summary.ClockIn);
        Assert.NotNull(summary.ClockOut);
    }

    [Fact]
    public void Normal_EmployeeDefaultSet_StillMatchesAPunchInsideItsOwnWindow()
    {
        // Same tight employee default as above, but the clock-in punch is
        // only 10 minutes early -- inside the employee's own 15-minute
        // window -- so it's still picked up normally.
        var employee = EmployeeWithTightBufferDefault();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(7, 50)), // 10 min early
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(7, 50), summary.ClockIn);
    }

    [Fact]
    public void Normal_PerDayOverride_TakesPriorityOverEmployeeDefault()
    {
        // ScheduleEntry's own per-day override (2h, matching the policy
        // default's own width here just for a round number) sits above the
        // employee's tight 15-minute default in priority -- the same
        // hour-early punch that Normal_EmployeeDefaultSet_NarrowsTheSearchWindow
        // showed getting excluded is picked back up once this day explicitly
        // widens its own window past the employee default.
        var employee = EmployeeWithTightBufferDefault();
        var schedule = NormalSchedule(employee);
        schedule.ClockInBufferBeforeHours = 2.0;
        schedule.ClockInBufferAfterHours = 2.0;

        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(7, 0)), // 1h early
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(7, 0), summary.ClockIn);
    }

    [Fact]
    public void RestDay_WindowedMode_AlsoHonorsTheEmployeeDefault()
    {
        // RestDayShiftCalculationStrategy's windowed sub-case reuses the exact
        // same buffer-window matching Normal does (see NormalBufferResolver)
        // -- same scenario as Normal_EmployeeDefaultSet_NarrowsTheSearchWindow,
        // just for a Rest Day scheduled as duty instead.
        var employee = EmployeeWithTightBufferDefault();
        var schedule = new ScheduleEntry
        {
            EmployeeId = employee.Pin,
            Employee = employee,
            ScheduleType = ScheduleType.RestDay,
            Date = Date,
            TimeIn = new TimeOnly(8, 0),
            WorkTimeHours = 8m,
        };
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(7, 0)), // 1h early -- outside the 15-min employee window
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        // RestDay only recognizes a duty when *both* sides are found -- one
        // side missing (same as this case) reports zero WorkedHours, same as an
        // unscheduled Rest Day, even though ClockOut/ClockIn stay unset on the
        // summary the same way Partial's own two fields do for Normal above.
        Assert.Equal(PunchStatus.RestDay, summary.Status);
        Assert.Equal(0.0, summary.WorkedHours);
    }
}
