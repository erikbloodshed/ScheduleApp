using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Phase 6, checklist item 3's other half: the §11/assumption-9 worked
/// examples in <see cref="PayrollCalculatorRestDayWorkedExampleTests"/> only
/// exercise the "real period longer than the nominal 15 days" direction
/// (Aug 16-31, 16 real days). This file covers the opposite direction --
/// February's short second half (Feb 16-28, 2026 is not a leap year, so 13
/// real days) -- which stresses the same divisor logic from the other side
/// and was flagged as unverified during Phase 6's sign-off pass (no test
/// exercised it before this file).
///
/// Both tests below share Feb 16-28, 2026 with one Rest Day (Feb 22); the
/// second additionally marks Feb 25 Absent. Expected figures are hand-traced
/// against PayrollCalculator.Calculate's actual arithmetic (divisor = 15 -
/// restDays; nominalPeriodEnd = periodStart + 14 days is always used
/// regardless of periodEnd, so a period shorter than 15 real days never
/// changes which formula applies -- restDays/uncreditedDays are simply
/// bounded by however many real AttendanceSummary rows exist, and 13 rows
/// can never accidentally trip the divisor &lt;= 0 guard), not independently
/// re-derived, same discipline as the §11 test file.
/// </summary>
public class PayrollCalculatorShortPeriodTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, OT=25%, ND=10%

    private static readonly DateOnly Start = new(2026, 2, 16);
    private static readonly DateOnly End = new(2026, 2, 28); // 13 real calendar days

    [Fact]
    public void Feb16To28_ThirteenDayMonth_AllDaysCreditedPaysFlatSemiMonthlyRate()
    {
        var employee = MonthlyEmployee();

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 2, 22), s => MakeRestDay(s)));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // divisor = 15 - 1 = 14 (the shorter real period never inflates or
        // shrinks the nominal-15 assumption -- see nominalPeriodEnd's own
        // comment in PayrollCalculator). No uncredited days, so basicPayDays
        // stays exactly divisor (14) and Basic Pay is effectiveDailyRate * 14
        // = the flat semiMonthlyRate -- same "fully attended period" shape
        // the Aug 16-31 present-case test asserts, just with the label's day
        // count (14) now exceeding the real 13-day span instead of falling
        // short of it.
        Assert.Equal(15000.00m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (14D)", result.ComputedGrossPay[0].Label);
        Assert.Equal(14, result.WorkDays); // divisor(14) - uncreditedDays(0)
        Assert.Equal(0, result.UnscheduledDayCount);
    }

    [Fact]
    public void Feb16To28_ThirteenDayMonth_AbsenceDeductsAtTheSameNominalFifteenDivisorRate()
    {
        var employee = MonthlyEmployee();

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 2, 22), s => MakeRestDay(s)),
            (new DateOnly(2026, 2, 25), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // basicPayDays = divisor(14) - uncreditedDays(1) = 13.
        // effectiveDailyRate = 15,000/14 = 1,071.428571...; basicPay =
        // 1,071.428571... * 13 = 13,928.571428... -> 13,928.57 (rounded
        // once, away-from-zero, same convention as every other Amount).
        // Same effectiveDailyRate a longer period would use for the same
        // divisor -- nothing about the 13-real-day span changes the rate a
        // single absence is deducted at, matching assumption 9's second
        // policy point (already asserted for the Aug 31-absent direction;
        // this is the Feb-short-period side of the same claim).
        Assert.Equal(13928.57m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (13D)", result.ComputedGrossPay[0].Label);
        Assert.Equal(13, result.WorkDays); // divisor(14) - uncreditedDays(1)
        Assert.Equal(0, result.UnscheduledDayCount);
    }
}
