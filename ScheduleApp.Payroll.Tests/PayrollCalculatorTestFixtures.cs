using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Small builders shared by the Phase 3.8 test classes, so each test can
/// stay focused on the one thing it's hand-tracing from the design doc's
/// §11 worked examples rather than re-typing 15 days' worth of
/// AttendanceSummary boilerplate every time.
/// </summary>
internal static class PayrollCalculatorTestFixtures
{
    /// <summary>One AttendanceSummary per calendar day in [start, end], each
    /// defaulting to a plain Complete/Normal day with every hour figure at
    /// zero -- matching §11's own "everything else Complete" framing. Pass
    /// (date, configure) pairs to override just the days that matter for a
    /// given scenario (a Rest Day, an Absence, extra Overtime/Night Diff
    /// hours, etc.).</summary>
    public static List<AttendanceSummary> BuildDays(
        int employeeId,
        DateOnly start,
        DateOnly end,
        params (DateOnly Date, Action<AttendanceSummary> Configure)[] overrides)
    {
        var overrideMap = overrides.ToDictionary(o => o.Date, o => o.Configure);
        var result = new List<AttendanceSummary>();

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var summary = new AttendanceSummary
            {
                EmployeeId = employeeId,
                ShiftDate = date,
                Status = PunchStatus.Complete,
                ScheduleType = ScheduleType.Normal,
            };

            if (overrideMap.TryGetValue(date, out var configure))
            {
                configure(summary);
            }

            result.Add(summary);
        }

        return result;
    }

    /// <summary>Marks a day as a Rest Day -- Status and ScheduleType always
    /// travel together for RestDay (see AttendanceDayStatus/
    /// RestDayShiftCalculationStrategy: a day is never partly RestDay).
    /// workedHours defaults to 0 -- an unworked Rest Day -- which is also
    /// exactly what an unpaired-punch Rest Day looks like by the time
    /// PayrollCalculator sees it (§5's "safe" rule zeroes Worked_H before
    /// this summary is ever built), so the same helper covers both cases.</summary>
    public static void MakeRestDay(AttendanceSummary summary, double workedHours = 0)
    {
        summary.ScheduleType = ScheduleType.RestDay;
        summary.Status = PunchStatus.RestDay;
        summary.Worked_H = workedHours;
    }

    public static void MakeAbsent(AttendanceSummary summary)
    {
        summary.Status = PunchStatus.Absent;
    }

    /// <summary>QualifiesForRestDayPay = true (added confirming Pay Eligibility Flags plan
    /// Phase 6, not part of this builder when it was first written): every existing caller
    /// of this builder is asserting the pre-existing Rest Day Pay math itself
    /// (PayrollCalculatorRestDayWorkedExampleTests/PayrollCalculatorShortPeriodTests, both
    /// predating that plan), several via result.ComputedGrossPay[3] -- the Rest Day Pay
    /// line's fixed index back when every employee had one. That plan's Phase 4 made the
    /// line conditional on this flag, defaulting false for a plain `new Employee`; left
    /// unset here, those same asserts would now throw (index 3 out of range on a 3-line
    /// list) instead of checking the figure they were written to check. True keeps every
    /// existing caller's intent intact; nothing in either test file needs it false.</summary>
    public static Employee MonthlyEmployee(
        int pin = 1001,
        decimal monthlyRate = 30_000.00m,
        decimal restDayWorkPremiumPercentage = 0m) => new()
    {
        Pin = pin,
        LastName = "Cruz",
        FirstName = "Juan",
        EmployeeType = EmployeeType.Monthly,
        MonthlyRate = monthlyRate,
        RestDayWorkPremiumPercentage = restDayWorkPremiumPercentage,
        QualifiesForRestDayPay = true,
    };
}
