using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Turns the Employee Pay Types &amp; Rest Day design doc's §11 worked
/// examples directly into asserts (Implementation Phases doc, 3.8). Every
/// expected number below is copied straight out of §11 rather than
/// independently re-derived, so a failing test here means
/// PayrollCalculator.Calculate's output has drifted from the design doc's
/// own hand-verified math -- not from an assumption this test file invented
/// on its own.
///
/// The first five scenarios share one employee/period shape unless a test
/// says otherwise: Monthly-rated, MonthlyRate = 30,000.00, period Aug 1-15
/// 2026 (15 calendar days), Aug 2 &amp; Aug 9 marked RestDay, Aug 7 Absent,
/// everything else Complete -- exactly §11's base example. The final three
/// scenarios cover Aug 16-31 (16 real calendar days -- one day beyond the
/// nominal 15-day window) and the confirmed excess-day bonus/neutral-absence
/// policy described in PayrollCalculator.Calculate's own comments.
/// </summary>
public class PayrollCalculatorRestDayWorkedExampleTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, OT=25%, ND=10% -- all §11 defaults

    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 15);

    [Fact]
    public void Aug1To15_BaseExample_TwoRestDaysOneAbsence()
    {
        var employee = MonthlyEmployee();

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // divisor = 15 - 2 = 13; effectiveDailyRate = 15,000/13 = 1,153.8462;
        // uncreditedDays = 1 (Aug 7); basicPay = 15,000 - 1,153.8462 = 13,846.15.
        Assert.Equal(13846.15m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (Semi-Monthly, 1D deducted)", result.ComputedGrossPay[0].Label);
        Assert.Equal(12, result.WorkDays); // divisor(13) - uncreditedDays(1)
        Assert.Equal(0.00m, result.RestDayHours);
        Assert.Equal(0.00m, result.ComputedGrossPay[3].Amount); // Rest Day Pay, both unworked
        Assert.Equal(0, result.UnscheduledDayCount);
    }

    [Fact]
    public void OneRestDayWorkedAtThirtyPercentPremium_PaysExactly1500ForEightHours()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s, workedHours: 8)),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // §11: at full precision, (15,000/13/8) * 8.00 * 1.30 = 1,500.00
        // exactly -- the one-centavo-short trap the doc warns about only
        // shows up if hourlyRate is rounded before multiplying, which
        // PayrollCalculator never does (Round is applied once, to the
        // final summed line).
        Assert.Equal(8.00m, result.RestDayHours);
        Assert.Equal(1500.00m, result.ComputedGrossPay[3].Amount);

        // Rest Day Pay is additional, not a substitute for Basic Pay -- Aug 9
        // still counts toward restDays/the divisor exactly like an unworked
        // Rest Day would, so Basic Pay is unaffected by it being worked.
        Assert.Equal(13846.15m, result.ComputedGrossPay[0].Amount);
    }

    [Fact]
    public void RestDayWithOneUnpairedPunch_EarnsNothing()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        // §5's "safe" rule: an unpaired trailing punch zeroes the whole day.
        // RestDayShiftCalculationStrategy guarantees Worked_H == 0 in that
        // case before PayrollCalculator ever sees the row, so this scenario
        // is indistinguishable, from PayrollCalculator's side, from an
        // unworked Rest Day -- which is exactly the point being tested.
        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s, workedHours: 0)),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        Assert.Equal(0.00m, result.RestDayHours);
        Assert.Equal(0.00m, result.ComputedGrossPay[3].Amount);
    }

    [Fact]
    public void RestDayWorkedIntoTheNight_AddsASeparateNightDiffLine()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s =>
            {
                MakeRestDay(s, workedHours: 8);
                s.NightDiff_H = 2; // 2 of the 8 worked hours fall in the Night Diff window
            }),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // §7 item 3: nightDiffPay = hourlyRate * 2.00 * 0.10 = 144.2307... * 0.20 = 28.85
        // (rounded) -- a fourth line, on top of Basic Pay/Rest Day Pay, that
        // neither reduces nor is reduced by the 1,500.00 Rest Day Pay above.
        Assert.Equal(2.00m, result.NightDiffHours);
        Assert.Equal(28.85m, result.ComputedGrossPay[2].Amount);
        Assert.Equal(8.00m, result.RestDayHours);
        Assert.Equal(1500.00m, result.ComputedGrossPay[3].Amount);
    }

    [Fact]
    public void RestDayWithScheduledWindow_PremiumCoversFullHoursPastTheSchedule()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            // Scheduled 8:00 AM-4:00 PM (8h); both a clock-in and a clock-out
            // were found (8:00 AM-7:00 PM), so this is a duty (§5's windowed
            // mode) and the premium applies to the entire 11 hours actually
            // worked, not just the scheduled 8.
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s, workedHours: 11)),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // §11: at full precision, (15,000/13/8) * 11.00 * 1.30 = 2,062.50 exactly.
        Assert.Equal(11.00m, result.RestDayHours);
        Assert.Equal(2062.50m, result.ComputedGrossPay[3].Amount);
    }

    [Fact]
    public void Aug16To31_ThirtyOneDayMonth_Day31PresentEarnsAProratedExcessDayBonus()
    {
        var employee = MonthlyEmployee();
        var start = new DateOnly(2026, 8, 16);
        var end = new DateOnly(2026, 8, 31); // 16 real calendar days

        var summaries = BuildDays(employee.Pin, start, end,
            (new DateOnly(2026, 8, 16), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 23), s => MakeRestDay(s)));
            // Aug 31 is left at BuildDays' default: plain Complete, not RestDay.

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], start, end);

        // Confirmed company policy (superseding the original assumption 9
        // framing, which paid a flat rate regardless of real period length):
        // the divisor still assumes a nominal 15 days -- only the 2 Sundays
        // inside the first 15 of those 16 days count toward it -- but Aug 31,
        // being a real calendar day beyond that nominal window that was
        // actually worked, now adds one effectiveDailyRate (15,000/13 =
        // 1,153.846154) on top of the flat semiMonthlyRate: 15,000 +
        // 1,153.846154 = 16,153.846154 -> 16,153.85.
        Assert.Equal(16153.85m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (Semi-Monthly, 1D excess)", result.ComputedGrossPay[0].Label);
        Assert.Equal(14, result.WorkDays); // divisor(13) - uncreditedDays(0) + excessCreditedDays(1)
    }

    [Fact]
    public void Aug16To31_ThirtyOneDayMonth_Day31AbsentForfeitsOnlyItsOwnBonusNoFurtherDeduction()
    {
        var employee = MonthlyEmployee();
        var start = new DateOnly(2026, 8, 16);
        var end = new DateOnly(2026, 8, 31);

        var summaries = BuildDays(employee.Pin, start, end,
            (new DateOnly(2026, 8, 16), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 23), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 31), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], start, end);

        // Confirmed policy: an excess day (beyond the nominal 15-day window)
        // that's Absent instead of worked is neutral -- it simply doesn't
        // earn the bonus the present-and-credited version of this test earns
        // above, but it does NOT additionally deduct from the flat
        // semiMonthlyRate the way an absence *within* the nominal window
        // would. So this period, despite Aug 31 being Absent, still pays the
        // same flat 15,000.00 a fully-attended nominal-15-day period would.
        Assert.Equal(15000.00m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (Semi-Monthly)", result.ComputedGrossPay[0].Label);
        Assert.Equal(13, result.WorkDays); // divisor(13) - uncreditedDays(0) + excessCreditedDays(0)
    }

    [Fact]
    public void Aug16To31_ThirtyOneDayMonth_Day31AbsentHoliday_ExcessDayNeverSpecialCasedByHolidayPay()
    {
        // Same setup as Day31AbsentForfeitsOnlyItsOwnBonusNoFurtherDeduction
        // above, plus Aug 31 -- the excess day itself -- also listed as a
        // Holiday. CalculateHolidayPay's own day loop asks only "is this
        // ShiftDate in holidayDates", with no nominal-vs-excess branch of its
        // own (unlike Basic Pay's separate excessCreditedDays/uncreditedDays
        // split above), so a Holiday landing on day 16 of this 16-real-day
        // period is handled identically to one landing anywhere inside the
        // nominal 15-day window -- confirmed here rather than assumed, and
        // pinned as a permanent regression check so it can't regress
        // silently.
        var employee = MonthlyEmployee();
        var start = new DateOnly(2026, 8, 16);
        var end = new DateOnly(2026, 8, 31);
        var excessDayHoliday = new DateOnly(2026, 8, 31);

        var summaries = BuildDays(employee.Pin, start, end,
            (new DateOnly(2026, 8, 16), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 23), s => MakeRestDay(s)),
            (excessDayHoliday, MakeAbsent));

        var result = PayrollCalculator.Calculate(
            employee, Policy, summaries, [], start, end, holidayDates: [excessDayHoliday]);

        // Basic Pay: unchanged from the non-Holiday version of this exact
        // scenario -- an Absent excess day is still neutral, forfeiting only
        // its own bonus, not a further deduction. Nothing about the day also
        // being a listed Holiday feeds back into Basic Pay's own math.
        Assert.Equal(15000.00m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (Semi-Monthly)", result.ComputedGrossPay[0].Label);

        // Holiday Pay: Monthly + unworked (Absent, not a credited day) still
        // earns the flat day component (this file's sibling
        // PayrollCalculatorHolidayPayTests class covers that rule on its
        // own) -- same divisor CalculateHolidayPay computes for itself, only
        // the 2 Rest Days inside the nominal 15-day window (Aug 16, Aug 23)
        // count, so divisor = 13 and effectiveDailyRate = 15,000/13 =
        // 1,153.846154 -> 1,153.85, same figure an unworked Holiday inside
        // the nominal window would earn.
        Assert.Equal(1153.85m, result.HolidayPayAmount);
        Assert.Equal(1, result.HolidayWorkedDays);
    }

    [Fact]
    public void Aug16To31_ThirtyOneDayMonth_NominalAbsenceAndExcessBonusCombineInOneLabel()
    {
        var employee = MonthlyEmployee();
        var start = new DateOnly(2026, 8, 16);
        var end = new DateOnly(2026, 8, 31);

        // Aug 20 & Aug 21 are both within the nominal window (<= Aug 30) and
        // Absent -- two ordinary deductions. Aug 31 is beyond the nominal
        // window and Complete -- one excess-day bonus. Deliberately unequal
        // counts (2 vs 1) so the combined result isn't a coincidental wash,
        // unlike an equal-counts case would be.
        var summaries = BuildDays(employee.Pin, start, end,
            (new DateOnly(2026, 8, 16), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 23), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 20), MakeAbsent),
            (new DateOnly(2026, 8, 21), MakeAbsent));
            // Aug 31 left at BuildDays' default: plain Complete.

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], start, end);

        // effectiveDailyRate = 15,000/13 = 1,153.846154. basicPay = 15,000 -
        // (2 * 1,153.846154) + (1 * 1,153.846154) = 15,000 - 1,153.846154 =
        // 13,846.153846 -> 13,846.15. Both mechanisms -- the nominal-window
        // deduction and the excess-window bonus -- apply independently in
        // the same period and both show up in the label.
        Assert.Equal(13846.15m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (Semi-Monthly, 2D deducted, 1D excess)", result.ComputedGrossPay[0].Label);
        Assert.Equal(12, result.WorkDays); // divisor(13) - uncreditedDays(2) + excessCreditedDays(1)
    }
}
