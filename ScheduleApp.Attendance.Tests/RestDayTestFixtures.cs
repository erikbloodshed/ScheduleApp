using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Small builders shared by the Phase 2.5 test classes, so each test can stay
/// focused on the one punch-timing scenario it's checking rather than
/// re-typing Employee/ScheduleEntry/AttendanceLog boilerplate every time --
/// same spirit as ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures.
///
/// Every test in this project goes through the public
/// AttendanceCalculator.CalculateShift entry point rather than constructing
/// RestDayShiftCalculationStrategy directly -- that class (like every other
/// IShiftCalculationStrategy) is internal, and CalculateShift is the one
/// contract callers (AttendanceWorkflowService, and now this test project)
/// are meant to depend on -- see AttendanceCalculator's own doc comment.
/// </summary>
internal static class RestDayTestFixtures
{
    /// <summary>The date every test in this project uses -- arbitrary, since
    /// none of the scenarios below depend on which real calendar day it is.</summary>
    public static readonly DateOnly Date = new(2026, 8, 9);

    /// <summary>Every buffer/gap/night-window value at its
    /// AttendancePolicy default (ClockInBuffer 2h/2h, ClockOutBuffer 6h/6h,
    /// FlexibleMinimumBreakGap 1h, ClockOutGracePeriod 0.5h, CapEarlyClockIn
    /// true, NightDiff 22:00-06:00) -- nothing in Phase 2.5's scope calls for
    /// a non-default policy.</summary>
    public static readonly AttendancePolicy DefaultPolicy = new();

    public static Employee Employee(
        int pin = 1001,
        bool qualifiesForNightDiff = true) => new()
    {
        Pin = pin,
        LastName = "Cruz",
        FirstName = "Juan",
        QualifiesForNightDiff = qualifiesForNightDiff,
    };

    /// <summary>A RestDay ScheduleEntry for <see cref="Date"/>. Leave
    /// timeIn/workTimeHours both null for the no-schedule mode (the common
    /// case); set both for the windowed mode -- RestDayShiftCalculationStrategy
    /// picks its mode the same way ScheduleEntry itself does, off whether both
    /// are set together.</summary>
    public static ScheduleEntry RestDaySchedule(
        Employee employee,
        TimeOnly? timeIn = null,
        decimal? workTimeHours = null,
        bool? nightDiffEligibleOverride = null) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.RestDay,
        Date = Date,
        TimeIn = timeIn,
        WorkTimeHours = workTimeHours,
        NightDiffEligibleOverride = nightDiffEligibleOverride,
    };

    /// <summary>One device punch (AttendanceLogSource.File) at the given
    /// time-of-day on <see cref="Date"/>. Every scenario below uses plain
    /// device punches -- manual-entry fallback is FlexibleShiftCalculationStrategy/
    /// SingleWindowShiftCalculationStrategy territory (PunchMatching), not
    /// anything RestDayShiftCalculationStrategy does differently, so it's out
    /// of scope for these tests.</summary>
    public static AttendanceLog Punch(int employeeId, TimeOnly timeOfDay) => new()
    {
        EmployeeId = employeeId,
        Timestamp = Date.ToDateTime(timeOfDay),
        Source = AttendanceLogSource.File,
    };
}
