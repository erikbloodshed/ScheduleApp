using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Covers AttendancePolicy.LateInEarlyOutGraceMinutes: a punch within the grace
/// period of the scheduled start/end reports zero LateInDuration/EarlyOutDuration, and once
/// past it the *entire* difference counts, not just the excess over the grace
/// period -- see that property's own doc comment. Exercises both strategies
/// that populate LateInDuration/EarlyOutDuration at all (SingleWindowShiftCalculationStrategy
/// for Normal, SplitShiftCalculationStrategy for each segment), through the
/// public AttendanceCalculator.CalculateShift entry point, same convention as
/// the other test classes in this project.
/// </summary>
public class LateInEarlyOutGraceTests
{
    private static readonly DateOnly Date = RestDayTestFixtures.Date;

    [Fact]
    public void DefaultPolicy_GraceMinutesIsFive()
    {
        Assert.Equal(5.0, new AttendancePolicy().LateInEarlyOutGraceMinutes);
    }

    private static ScheduleEntry NormalSchedule(Employee employee) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.Normal,
        Date = Date,
        TimeIn = new TimeOnly(8, 0),
        WorkTimeHours = 8m, // scheduled TimeOut = 4:00 PM
    };

    [Theory]
    [InlineData(0)] // exactly on time
    [InlineData(3)] // within the default 5-minute grace
    [InlineData(5)] // exactly at the grace boundary -- still on time
    public void Normal_LateClockIn_WithinGrace_ReportsNoLateIn(int minutesLate)
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0).AddMinutes(minutesLate)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.Zero, summary.LateInDuration);
        Assert.Equal(0.0, summary.RemainHours);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(20)]
    public void Normal_LateClockIn_PastGrace_ReportsTheFullDifference(int minutesLate)
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0).AddMinutes(minutesLate)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        // The entire difference counts once past the grace period, not just
        // (minutesLate - grace) -- e.g. 6 minutes late reports as 6, not 1.
        Assert.Equal(TimeSpan.FromMinutes(minutesLate), summary.LateInDuration);
        Assert.Equal(minutesLate / 60.0, summary.RemainHours, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void Normal_EarlyClockOut_WithinGrace_ReportsNoEarlyOut(int minutesEarly)
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0).AddMinutes(-minutesEarly)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.Zero, summary.EarlyOutDuration);
        Assert.Equal(0.0, summary.RemainHours);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(20)]
    public void Normal_EarlyClockOut_PastGrace_ReportsTheFullDifference(int minutesEarly)
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0).AddMinutes(-minutesEarly)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.FromMinutes(minutesEarly), summary.EarlyOutDuration);
        Assert.Equal(minutesEarly / 60.0, summary.RemainHours, precision: 6);
    }

    [Fact]
    public void Normal_LateInAndEarlyOut_BothPastGrace_RemainIsTheirSum()
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 10)), // 10 min late
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(15, 45)), // 15 min early
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(TimeSpan.FromMinutes(10), summary.LateInDuration);
        Assert.Equal(TimeSpan.FromMinutes(15), summary.EarlyOutDuration);
        Assert.Equal(TimeSpan.FromMinutes(25), summary.RemainDuration);
        Assert.Equal(25 / 60.0, summary.RemainHours, precision: 6);
    }

    /// <summary>A policy with the grace period turned off (0) restores the
    /// pre-feature behavior: any tardiness at all, however small, counts.</summary>
    [Fact]
    public void Normal_ZeroGracePolicy_AnyLatenessCounts()
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = NormalSchedule(employee);
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 1)), // 1 min late
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var zeroGracePolicy = new AttendancePolicy { LateInEarlyOutGraceMinutes = 0 };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, zeroGracePolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(TimeSpan.FromMinutes(1), summary.LateInDuration);
    }

    private static ScheduleEntry SplitShiftSchedule(Employee employee, TimeOnly segmentIn, TimeOnly segmentOut) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.SplitShift,
        Date = Date,
        FlexibleSegments = new List<FlexibleSegment>
        {
            new() { TimeIn = segmentIn, TimeOut = segmentOut },
        },
    };

    [Fact]
    public void SplitShift_LateClockIn_WithinGrace_ReportsNoLateIn()
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = SplitShiftSchedule(employee, new TimeOnly(8, 0), new TimeOnly(12, 0));
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 4)), // 4 min late
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(12, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.Zero, summary.LateInDuration);
    }

    [Fact]
    public void SplitShift_LateClockIn_PastGrace_ReportsTheFullDifference()
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = SplitShiftSchedule(employee, new TimeOnly(8, 0), new TimeOnly(12, 0));
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 7)), // 7 min late
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(12, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.FromMinutes(7), summary.LateInDuration);
    }

    [Fact]
    public void SplitShift_EarlyClockOut_WithinGrace_ReportsNoEarlyOut()
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = SplitShiftSchedule(employee, new TimeOnly(8, 0), new TimeOnly(12, 0));
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(11, 57)), // 3 min early
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.Zero, summary.EarlyOutDuration);
    }

    [Fact]
    public void SplitShift_EarlyClockOut_PastGrace_ReportsTheFullDifference()
    {
        var employee = RestDayTestFixtures.Employee();
        var schedule = SplitShiftSchedule(employee, new TimeOnly(8, 0), new TimeOnly(12, 0));
        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(11, 53)), // 7 min early
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(TimeSpan.FromMinutes(7), summary.EarlyOutDuration);
    }
}
