using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Covers FlexibleShiftCalculationStrategy's device/manual punch preference,
/// which used to be decided for the whole day (any device punch that day
/// discarded every manual punch outright) and is now decided per-punch (a
/// manual punch is only dropped when a device punch lands within
/// AttendancePolicy.FlexibleMinimumBreakGap of it). See the class doc
/// comment and CalculateUnrestrictedDay's own note for the rationale.
///
/// Goes through the public AttendanceCalculator.CalculateShift entry point,
/// same convention as RestDayUnscheduledModeTests -- FlexibleShiftCalculationStrategy
/// itself is internal.
/// </summary>
public class FlexibleShiftManualPunchTests
{
    private static readonly DateOnly Date = new(2026, 8, 20);
    private static readonly AttendancePolicy DefaultPolicy = new(); // FlexibleMinimumBreakGap = 1.0h

    private static Employee Employee(int pin = 1001) => new()
    {
        Pin = pin,
        LastName = "Cruz",
        FirstName = "Juan",
    };

    private static ScheduleEntry FlexibleSchedule(
        Employee employee,
        decimal? workTimeHours,
        TimeOnly? restrictedTimeIn = null,
        TimeOnly? restrictedTimeOut = null) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.Flexible,
        Date = Date,
        WorkTimeHours = workTimeHours,
        RestrictedTimeIn = restrictedTimeIn,
        RestrictedTimeOut = restrictedTimeOut,
    };

    private static AttendanceLog DevicePunch(int employeeId, TimeOnly timeOfDay) => new()
    {
        EmployeeId = employeeId,
        Timestamp = Date.ToDateTime(timeOfDay),
        Source = AttendanceLogSource.File,
    };

    private static AttendanceLog ManualPunch(int employeeId, TimeOnly timeOfDay) => new()
    {
        EmployeeId = employeeId,
        Timestamp = Date.ToDateTime(timeOfDay),
        Source = AttendanceLogSource.Manual,
        Reason = "Forgot to badge in",
        EnteredBy = "Test Fixture",
    };

    /// <summary>The bug this fix addresses: a manual punch far from the
    /// day's only device punch used to be discarded wholesale, leaving a
    /// lone unpaired device punch and a Partial day with zero credited
    /// hours -- even though the manual+device pair together satisfy the
    /// day's required hours almost exactly. WorkTimeHours 9.00h,
    /// RestrictedTimeIn 6:00 AM, RestrictedTimeOut 8:00 PM, punches 8:00 AM
    /// (Manual) and 5:26 PM (device) -- the exact scenario reported.</summary>
    [Fact]
    public void ManualPunchFarFromDevicePunch_IsKeptAndPairsInsteadOfBeingDiscarded()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(
            employee,
            workTimeHours: 9.0m,
            restrictedTimeIn: new TimeOnly(6, 0),
            restrictedTimeOut: new TimeOnly(20, 0));

        var punches = new List<AttendanceLog>
        {
            ManualPunch(employee.Pin, new TimeOnly(8, 0)),
            DevicePunch(employee.Pin, new TimeOnly(17, 26)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        // Both punches paired: 8:00 AM (manual) -> 5:26 PM (device) = 9h26m.
        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(8, 0), summary.ClockIn);
        Assert.True(summary.ClockInIsManual);
        Assert.Equal(new TimeOnly(17, 26), summary.ClockOut);
        Assert.False(summary.ClockOutIsManual);
        Assert.Equal(9.0 + 26.0 / 60.0, summary.Worked_H, precision: 3);
        Assert.Equal(0.0, summary.Remain_H);
        Assert.True(summary.Overtime_H > 0, "9h26m against a 9.00h requirement should register a small overtime credit.");

        // Both punches actually used -- nothing left unclaimed.
        Assert.Equal(2, result.ClaimedPunches.Count);
        Assert.Empty(result.UnclaimedPunches);
    }

    /// <summary>A manual punch minutes away from a device punch is a
    /// duplicate of the same physical event (e.g. the device eventually
    /// synced a punch someone had already hand-typed) and should still be
    /// dropped in favor of the device one -- the fix narrows the
    /// device-first preference, it doesn't remove it.</summary>
    [Fact]
    public void ManualPunchMinutesFromDevicePunch_IsStillTreatedAsADuplicateAndDropped()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);

        var manualIn = ManualPunch(employee.Pin, new TimeOnly(8, 0));
        var deviceInDuplicate = DevicePunch(employee.Pin, new TimeOnly(8, 2)); // 2 min later -- same event
        var deviceOut = DevicePunch(employee.Pin, new TimeOnly(17, 0));

        var punches = new List<AttendanceLog> { manualIn, deviceInDuplicate, deviceOut };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(new TimeOnly(8, 2), summary.ClockIn); // device punch wins, not the manual duplicate
        Assert.False(summary.ClockInIsManual);
        Assert.Equal(2, result.ClaimedPunches.Count);

        // The duplicate manual punch is reported back, not silently gone.
        var unclaimed = Assert.Single(result.UnclaimedPunches);
        Assert.Same(manualIn, unclaimed);
    }

    /// <summary>A day with only manual punches (no device punches at all)
    /// behaves exactly as before -- nothing to compare a manual punch
    /// against, so nothing is ever flagged as a duplicate.</summary>
    [Fact]
    public void ManualOnlyDay_AllManualPunchesUsed()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);
        var punches = new List<AttendanceLog>
        {
            ManualPunch(employee.Pin, new TimeOnly(8, 0)),
            ManualPunch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(8.0, summary.Worked_H);
        Assert.Equal(2, result.ClaimedPunches.Count);
        Assert.Empty(result.UnclaimedPunches);
    }

    /// <summary>A day with only device punches (no manual punches at all)
    /// also behaves exactly as before.</summary>
    [Fact]
    public void DeviceOnlyDay_UnaffectedByTheFix()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);
        var punches = new List<AttendanceLog>
        {
            DevicePunch(employee.Pin, new TimeOnly(8, 0)),
            DevicePunch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(8.0, summary.Worked_H);
        Assert.Equal(2, result.ClaimedPunches.Count);
        Assert.Empty(result.UnclaimedPunches);
    }
}
