using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using Xunit;
using static ScheduleApp.Attendance.Tests.RestDayTestFixtures;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Night-window overlap → NightDiff_H > 0, gated correctly by
/// ResolveNightDiffEligible. Only reachable through the windowed mode now
/// that the no-schedule mode never looks at punches at all (see
/// RestDayUnscheduledModeTests) -- so every scenario here uses a scheduled
/// 9:00 PM-11:00 PM window (RestDayDutyCheckBox checked), with a clean
/// clock-in/clock-out pair matching it. 9-11 PM, 1 hour of which (10-11 PM)
/// falls inside the default 10 PM-6 AM night window, so the only thing that
/// differs between scenarios is the employee's eligibility, isolating the
/// gate itself from the hours math NightDifferentialCalculator.CalculateHours
/// already owns.
/// </summary>
public class RestDayNightDiffTests
{
    private static readonly TimeOnly ScheduledTimeIn = new(21, 0);
    private static readonly decimal ScheduledWorkTimeHours = 2m; // scheduled TimeOut = 11:00 PM

    private static List<AttendanceLog> NineToElevenPmPunches(int employeeId) =>
    [
        Punch(employeeId, new TimeOnly(21, 0)),
        Punch(employeeId, new TimeOnly(23, 0)),
    ];

    [Fact]
    public void EligibleEmployee_CreditsTheOverlappingHourAsNightDiff()
    {
        var employee = Employee(qualifiesForNightDiff: true);
        var schedule = RestDaySchedule(employee, ScheduledTimeIn, ScheduledWorkTimeHours);
        var punches = NineToElevenPmPunches(employee.Pin);

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(2.0, summary.Worked_H);
        Assert.Equal(1.0, summary.NightDiff_H); // only the 10-11 PM portion
    }

    [Fact]
    public void IneligibleEmployee_ReportsZeroNightDiffDespiteTheSameOverlap()
    {
        var employee = Employee(qualifiesForNightDiff: false);
        var schedule = RestDaySchedule(employee, ScheduledTimeIn, ScheduledWorkTimeHours);
        var punches = NineToElevenPmPunches(employee.Pin);

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        // Worked_H is unaffected by night-diff eligibility -- only NightDiff_H
        // is suppressed.
        Assert.Equal(2.0, summary.Worked_H);
        Assert.Equal(0.0, summary.NightDiff_H);
    }

    [Fact]
    public void PerDayOverrideTakesPriorityOverTheEmployeesOwnDefault()
    {
        // Employee defaults to eligible, but this specific Rest Day entry
        // overrides it off -- NightDifferentialCalculator.ResolveEligible
        // checks schedule.NightDiffEligibleOverride before falling back to
        // Employee.QualifiesForNightDiff.
        var employee = Employee(qualifiesForNightDiff: true);
        var schedule = RestDaySchedule(
            employee, ScheduledTimeIn, ScheduledWorkTimeHours, nightDiffEligibleOverride: false);
        var punches = NineToElevenPmPunches(employee.Pin);

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(2.0, summary.Worked_H);
        Assert.Equal(0.0, summary.NightDiff_H);
    }
}
