using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Covers FlexibleShiftCalculationStrategy's odd-count normalization: an odd
/// number of effective punches used to leave the last punch dangling
/// (Partial, and Worked_H computed only from whatever Pass 1 happened to
/// pair sequentially -- see the class doc comment's old behavior). Now the
/// smallest adjacent gap in the day is checked first; if it's under
/// AttendancePolicy.FlexibleMinimumBreakGap, the noisier side of that pair is
/// dropped before pairing, restoring an even count.
///
/// Goes through the public AttendanceCalculator.CalculateShift entry point,
/// same convention as FlexibleShiftManualPunchTests --
/// FlexibleShiftCalculationStrategy itself is internal.
/// </summary>
public class FlexibleShiftOddPunchNormalizationTests
{
    private static readonly DateOnly Date = new(2026, 8, 20);
    private static readonly AttendancePolicy DefaultPolicy = new(); // FlexibleMinimumBreakGap = 1.0h

    private static Employee Employee(int pin = 1001) => new()
    {
        Pin = pin,
        LastName = "Cruz",
        FirstName = "Juan",
    };

    private static ScheduleEntry FlexibleSchedule(Employee employee, decimal? workTimeHours) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.Flexible,
        Date = Date,
        WorkTimeHours = workTimeHours,
    };

    private static AttendanceLog DevicePunch(int employeeId, TimeOnly timeOfDay) => new()
    {
        EmployeeId = employeeId,
        Timestamp = Date.ToDateTime(timeOfDay),
        Source = AttendanceLogSource.File,
    };

    /// <summary>(8:30, 8:38, 17:43) -- smallest gap is 8m at the start, so
    /// 8:38 is dropped and the day normalizes to (8:30, 17:43), a single
    /// ~9h13m interval. Before this fix, Pass 1 would have paired
    /// (8:30,8:38) as an 8-minute "interval" and left 17:43 dangling --
    /// Worked_H would have come out to 0.13h instead of ~9.22h.</summary>
    [Fact]
    public void ShortGapAtStart_DropsSecondPunch_NormalizesToFirstAndLast()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);
        var punches = new List<AttendanceLog>
        {
            DevicePunch(employee.Pin, new TimeOnly(8, 30)),
            DevicePunch(employee.Pin, new TimeOnly(8, 38)),
            DevicePunch(employee.Pin, new TimeOnly(17, 43)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(8, 30), summary.ClockIn);
        Assert.Equal(new TimeOnly(17, 43), summary.ClockOut);
        Assert.Equal(9.0 + 13.0 / 60.0, summary.Worked_H, precision: 3);

        Assert.Equal(2, result.ClaimedPunches.Count);
        var unclaimed = Assert.Single(result.UnclaimedPunches);
        Assert.Equal(new TimeOnly(8, 38), TimeOnly.FromDateTime(unclaimed.Timestamp));
    }

    /// <summary>(8:30, 17:33, 17:45) -- smallest gap is 12m at the end, so
    /// 17:33 is dropped and the day normalizes to (8:30, 17:45).</summary>
    [Fact]
    public void ShortGapAtEnd_DropsSecondToLastPunch_NormalizesToFirstAndLast()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);
        var punches = new List<AttendanceLog>
        {
            DevicePunch(employee.Pin, new TimeOnly(8, 30)),
            DevicePunch(employee.Pin, new TimeOnly(17, 33)),
            DevicePunch(employee.Pin, new TimeOnly(17, 45)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(8, 30), summary.ClockIn);
        Assert.Equal(new TimeOnly(17, 45), summary.ClockOut);
        Assert.Equal(9.0 + 15.0 / 60.0, summary.Worked_H, precision: 3);

        Assert.Equal(2, result.ClaimedPunches.Count);
        var unclaimed = Assert.Single(result.UnclaimedPunches);
        Assert.Equal(new TimeOnly(17, 33), TimeOnly.FromDateTime(unclaimed.Timestamp));
    }

    /// <summary>(8:00, 12:00, 12:05, 13:00, 17:00) -- the smallest gap (5m,
    /// between 12:00 and 12:05) isn't at either edge. 12:00's other neighbor
    /// (8:00) is 4h away; 12:05's other neighbor (13:00) is only 55m away --
    /// so 12:05 is the one sitting in a pocket of noise and gets dropped,
    /// normalizing to (8:00, 12:00, 13:00, 17:00): two clean 4-hour
    /// intervals totalling 8h, Complete.</summary>
    [Fact]
    public void ShortGapInMiddle_DropsWhicheverSideHasTheSmallerOtherGap()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);
        var punches = new List<AttendanceLog>
        {
            DevicePunch(employee.Pin, new TimeOnly(8, 0)),
            DevicePunch(employee.Pin, new TimeOnly(12, 0)),
            DevicePunch(employee.Pin, new TimeOnly(12, 5)),
            DevicePunch(employee.Pin, new TimeOnly(13, 0)),
            DevicePunch(employee.Pin, new TimeOnly(17, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(8, 0), summary.ClockIn);
        Assert.Equal(new TimeOnly(17, 0), summary.ClockOut);
        Assert.Equal(8.0, summary.Worked_H, precision: 3);

        Assert.Equal(4, result.ClaimedPunches.Count);
        var unclaimed = Assert.Single(result.UnclaimedPunches);
        Assert.Equal(new TimeOnly(12, 5), TimeOnly.FromDateTime(unclaimed.Timestamp));
    }

    /// <summary>An odd count where the smallest adjacent gap is still at or
    /// above FlexibleMinimumBreakGap doesn't get normalized -- there's no
    /// confident duplicate to remove, so this falls through to the
    /// pre-existing dangling-last-punch behavior (Partial, and Worked_H
    /// reflecting only the sequentially-paired punches).</summary>
    [Fact]
    public void NoGapUnderThreshold_FallsBackToDanglingLastPunch()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);
        var punches = new List<AttendanceLog>
        {
            DevicePunch(employee.Pin, new TimeOnly(8, 0)),
            DevicePunch(employee.Pin, new TimeOnly(12, 0)),
            DevicePunch(employee.Pin, new TimeOnly(17, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        // Unchanged pre-existing behavior: (8:00,12:00) pairs, 17:00 dangles.
        Assert.Equal(PunchStatus.Partial, summary.Status);
        Assert.Equal(4.0, summary.Worked_H, precision: 3);
        Assert.Equal(3, result.ClaimedPunches.Count);
        Assert.Empty(result.UnclaimedPunches);
    }
}
