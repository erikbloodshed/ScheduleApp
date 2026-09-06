using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Covers the Holiday Pay policy update: a Monthly-rated employee now earns a
/// listed Holiday's flat day component even when they didn't actually work it
/// (Absent/unpaid Leave/an unworked Rest Day), as long as that day isn't one
/// Basic Pay already pays in full (OfficialBusiness/paid Leave, which stay
/// excluded to avoid double-paying the same day) -- see
/// PayrollCalculator.CalculateHolidayPay's own doc comment for the full rule.
/// Also covers the accompanying change that lets Holiday Pay stack with Rest
/// Day Pay for a Holiday landing on a scheduled Rest Day (both employee
/// types), which used to be a blanket "no stacking" exclusion.
///
/// Every scenario uses Aug 1-15 2026 (15 calendar days, the nominal window in
/// full) so a Monthly employee's divisor/effectiveDailyRate stays easy to
/// hand-verify: no Rest Day in the period means divisor = 15 and
/// effectiveDailyRate = 15,000/15 = 1,000.00 exactly, except the two
/// scenarios that deliberately put the Holiday on a Rest Day, where the
/// divisor drops to 14 instead (documented on that test).
/// </summary>
public class PayrollCalculatorHolidayPayTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, OT=25%, ND=10%
    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 15);
    private static readonly DateOnly Holiday = new(2026, 8, 7);

    private static Employee DailyEmployee(decimal dailyRate = 800.00m) => new()
    {
        Pin = 3003,
        LastName = "Reyes",
        FirstName = "Ana",
        EmployeeType = EmployeeType.Daily,
        DailyRate = dailyRate,
    };

    [Fact]
    public void MonthlyAbsentOnHoliday_StillEarnsTheFlatDayComponent()
    {
        var employee = MonthlyEmployee(); // MonthlyRate 30,000 -> semiMonthlyRate 15,000
        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, MakeAbsent));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        // divisor = 15 (no Rest Days this period); effectiveDailyRate = 15,000/15 = 1,000.00.
        // Basic Pay's own uncreditedDays deduction docks this same 1,000.00 for the
        // Aug 7 absence -- this premium is what puts it back, netting the employee
        // their full pay for the holiday despite not working it.
        Assert.Equal(1000.00m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void MonthlyUnpaidLeaveOnHoliday_StillEarnsTheFlatDayComponent()
    {
        var employee = MonthlyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s => { s.Status = PunchStatus.Leave; s.IsPaidLeave = false; }));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        Assert.Equal(1000.00m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void MonthlyPaidLeaveOnHoliday_EarnsNothingExtra_BasicPayAlreadyCoversTheDay()
    {
        var employee = MonthlyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s => { s.Status = PunchStatus.Leave; s.IsPaidLeave = true; }));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        // Unchanged from before this feature: Basic Pay already pays this day in
        // full (paid Leave is a credited day), so no second payment here.
        Assert.Equal(0.00m, amount);
        Assert.Equal(0, days);
    }

    [Fact]
    public void MonthlyOfficialBusinessOnHoliday_EarnsNothingExtra_BasicPayAlreadyCoversTheDay()
    {
        var employee = MonthlyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s => s.Status = PunchStatus.OfficialBusiness));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        Assert.Equal(0.00m, amount);
        Assert.Equal(0, days);
    }

    [Fact]
    public void DailyRatedAbsentOnHoliday_StillEarnsNothing_NoWorkNoHolidayPay()
    {
        var employee = DailyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End, (Holiday, MakeAbsent));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        // Daily-rated is deliberately unchanged by this feature -- still strictly
        // no work, no Holiday Pay.
        Assert.Equal(0.00m, amount);
        Assert.Equal(0, days);
    }

    [Fact]
    public void MonthlyUnworkedRestDayHoliday_StillEarnsTheFlatDayComponent()
    {
        var employee = MonthlyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s => MakeRestDay(s)));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        // The Rest Day itself drops the divisor to 14 (15 - 1), so
        // effectiveDailyRate = 15,000/14 = 1,071.428571... -> 1,071.43 rounded.
        // An unworked Rest Day was never paid by Basic Pay to begin with, so this
        // premium is the employee's only pay for the date -- matching a plain
        // unworked-holiday day's worth of pay.
        Assert.Equal(1071.43m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void WorkedRestDayHoliday_NowStacksWithHolidayPay_BothEmployeeTypes()
    {
        var employee = DailyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s => MakeRestDay(s, workedHours: 8)));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        // Previously excluded entirely ("no stacking" with Rest Day Pay) -- now
        // pays the flat day component same as any other worked holiday, on top of
        // whatever Rest Day Pay Calculate's own per-day loop separately credits
        // for the same duty.
        Assert.Equal(800.00m, amount);
        Assert.Equal(1, days);
    }

    [Fact]
    public void MonthlyCompleteHolidayWithOvertimeAndNightDiff_UnchangedRegression()
    {
        var employee = MonthlyEmployee();
        var summaries = BuildDays(employee.Pin, Start, End,
            (Holiday, s =>
            {
                s.OvertimeHours = 2.0;
                s.NightDiffHours = 1.0;
            }));

        var (amount, days) = PayrollCalculator.CalculateHolidayPay(
            employee, Policy, summaries, Start, End, [Holiday]);

        // hourlyRate = effectiveDailyRate(1,000)/8 = 125.00.
        // otCopy = 125 * 2 * 1.25 = 312.50; ndCopy = 125 * 1 * 0.10 = 12.50.
        // amount = dayRate(1,000) + 312.50 + 12.50 = 1,325.00.
        Assert.Equal(1325.00m, amount);
        Assert.Equal(1, days);
    }
}
