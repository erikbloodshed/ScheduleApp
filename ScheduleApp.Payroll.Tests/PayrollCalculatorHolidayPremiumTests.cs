using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Covers the configurable Holiday premium -- PayrollPolicy.HolidayPremiumPercentage
/// (company default, 1.00m) and Employee.HolidayPremiumPercentage (per-employee
/// override, null means inherit) -- which replaced CalculateHolidayPay's previous
/// hardcoded "+1 day" (holidayPay += dayRate) with holidayPay += dayRate * premium.
///
/// PayrollCalculatorHolidayPayTests already proves the 1.00 default reproduces every
/// pre-existing figure exactly (dayRate * 1.00 == dayRate) -- confirmed there rather
/// than re-asserted here, since none of its expected numbers needed to change when
/// this field was introduced. This file is specifically about the premium actually
/// varying: a non-default policy value, a per-employee override winning over it, and
/// null correctly falling back to the policy default rather than to zero.
/// </summary>
public class PayrollCalculatorHolidayPremiumTests
{
    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 15);
    private static readonly DateOnly Holiday = new(2026, 8, 7);

    private static Employee DailyEmployee(decimal dailyRate = 800.00m, decimal? holidayPremiumPercentage = null) => new()
    {
        Pin = 4004,
        LastName = "Cruz",
        FirstName = "Liza",
        EmployeeType = EmployeeType.Daily,
        DailyRate = dailyRate,
        HolidayPremiumPercentage = holidayPremiumPercentage,
    };

    [Fact]
    public void CompanyDefaultAt150Percent_PaysHalfADayInsteadOfAFullOne()
    {
        // PH labor law's actual worked-regular-holiday rate is 200% (the 1.00 default),
        // but nothing stops a company configuring something else -- 0.50 here means a
        // worked holiday pays 150% total (Basic Pay's 100% + this 50%).
        var policy = new PayrollPolicy { HolidayPremiumPercentage = 0.50m };
        var employee = DailyEmployee(); // no employee-level override -- inherits the policy

        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, s => s.Status = PunchStatus.Complete));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, policy, summaries, Start, End, [Holiday]);

        // dayRate(800.00) * 0.50 = 400.00 -- half a day's bonus, not a full one.
        Assert.Equal(400.00m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void EmployeeOverride_WinsOverThePolicyDefault()
    {
        var policy = new PayrollPolicy(); // HolidayPremiumPercentage = 1.00 (default)
        var employee = DailyEmployee(holidayPremiumPercentage: 1.50m); // deliberately not 1.00

        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, s => s.Status = PunchStatus.Complete));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, policy, summaries, Start, End, [Holiday]);

        // dayRate(800.00) * 1.50 = 1,200.00 -- the employee's own 150% override, not
        // the policy's 100%.
        Assert.Equal(1200.00m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void NoEmployeeOverride_InheritsThePolicyDefault()
    {
        // The regression guard for the nullable-means-inherit contract itself: a null
        // override has to fall through to PayrollPolicy.HolidayPremiumPercentage, not
        // to zero. If this ever comes back 800.00 (dayRate * 0, i.e. no bonus at all),
        // the inherit fallback has broken and every unconfigured employee silently
        // stops earning a holiday bonus.
        var policy = new PayrollPolicy { HolidayPremiumPercentage = 0.75m };
        var employee = DailyEmployee();
        Assert.Null(employee.HolidayPremiumPercentage);

        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, s => s.Status = PunchStatus.Complete));

        var (amount, _) = PayrollCalculator.CalculateHolidayPay(
            employee, policy, summaries, Start, End, [Holiday]);

        // dayRate(800.00) * 0.75 = 600.00.
        Assert.Equal(600.00m, amount);
    }

    [Fact]
    public void ExplicitZeroOverride_MeansNoHolidayBonusAtAll()
    {
        // The other side of the same contract: 0 is a real, expressible choice distinct
        // from null's "inherit" -- an employee deliberately configured to earn no
        // holiday bonus still gets Basic Pay for the day (BasicPayForDay, untouched by
        // this), just nothing extra here.
        var policy = new PayrollPolicy { HolidayPremiumPercentage = 1.00m };
        var employee = DailyEmployee(holidayPremiumPercentage: 0m);

        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, s => s.Status = PunchStatus.Complete));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, policy, summaries, Start, End, [Holiday]);

        Assert.Equal(0.00m, amount);

        // Still counts as a worked-holiday day for HolidayWorkedDays' own purposes --
        // that count is about whether the day was worked, not how much it paid.
        Assert.Equal(1, days);
    }

    [Fact]
    public void MonthlyUnworkedButStillPaid_AlsoUsesThePremium()
    {
        // The "unworkedButStillPaid" branch (Monthly-rated, listed Holiday not worked
        // at all) shares the same holidayPay += dayRate * holidayPremium line as the
        // worked branch above -- confirming the premium isn't only wired into one of
        // the two call sites.
        var policy = new PayrollPolicy { HolidayPremiumPercentage = 0.50m };
        var employee = MonthlyEmployee(); // MonthlyRate 30,000 -> semiMonthlyRate 15,000
        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, MakeAbsent));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, policy, summaries, Start, End, [Holiday]);

        // divisor = 15 (no Rest Days this period); effectiveDailyRate = 15,000/15 =
        // 1,000.00; amount = 1,000.00 * 0.50 = 500.00.
        Assert.Equal(500.00m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void OvertimeAndNightDiffCopies_AreUnaffectedByThePremium()
    {
        // KNOWN GAP, per PayrollCalculator's own comment: the OT/ND "copies" are
        // deliberately NOT multiplied by holidayPremium -- they stay a flat doubling
        // of the day's ordinary OT/ND pay, same as before this field existed. Pinned
        // here so a future change to that behavior is a deliberate edit to this test,
        // not a silent side effect of some other change.
        var policy = new PayrollPolicy { HolidayPremiumPercentage = 2.00m }; // deliberately not 1.00
        var employee = MonthlyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s =>
            {
                s.OvertimeHours = 2.0;
                s.NightDiffHours = 1.0;
            }));

        var (amount, _) = PayrollCalculator.CalculateHolidayPay(
            employee, policy, summaries, Start, End, [Holiday]);

        // hourlyRate = effectiveDailyRate(1,000)/8 = 125.00.
        // Day component: 1,000 * 2.00 = 2,000.00 (the premium DOES apply here).
        // otCopy = 125 * 2 * 1.25 = 312.50; ndCopy = 125 * 1 * 0.10 = 12.50 (premium
        // does NOT apply to either of these).
        // amount = 2,000.00 + 312.50 + 12.50 = 2,325.00.
        Assert.Equal(2325.00m, amount);
    }
}
