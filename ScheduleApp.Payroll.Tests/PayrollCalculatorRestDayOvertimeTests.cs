using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// The two-tier Rest Day rate PH labor law defines: the first
/// PayrollPolicy.StandardHoursPerDay hours of an actually-worked Rest Day at 130%,
/// everything past that at a further 30% on top of *that* rate (1.30 * 1.30 = 169%,
/// not 1.30 + 0.30 = 160%). Before this, every worked Rest Day hour priced at the
/// same flat rate with no 8-hour boundary at all.
///
/// Also pins the premium's new resolution order -- Employee.RestDayWorkPremiumPercentage
/// when non-null, else PayrollPolicy.RestDayPremiumPercentage -- since the migration
/// that made that column nullable (AddRestDayPremiumOverride) rewrote every stored 0
/// to null specifically so the company default would start applying. A regression
/// that quietly restored "0 means 0" would leave every employee back on straight
/// time for rest day work, which is exactly the bug the migration exists to fix, so
/// InheritsThePolicyDefault below is the guard for it.
///
/// Same shared employee/period shape as PayrollCalculatorRestDayWorkedExampleTests
/// unless a test says otherwise: Monthly-rated, MonthlyRate = 30,000.00, Aug 1-15
/// 2026, Aug 2 and Aug 9 marked RestDay -- so divisor = 15 - 2 = 13,
/// effectiveDailyRate = 15,000/13, and hourlyRate = 15,000/13/8 = 144.230769...
/// Every expected figure below is annotated with the arithmetic that produces it.
/// </summary>
public class PayrollCalculatorRestDayOvertimeTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, RestDay=30%, RestDayOT=30%

    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 15);

    /// <summary>Builds the shared scenario with Aug 9 worked for the given hours.</summary>
    private static PayrollResult RunWithRestDayHours(Employee employee, double workedHours)
    {
        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s, workedHours)));

        return PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);
    }

    [Fact]
    public void ExactlyEightHours_StaysOnTheFirstTier()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var result = RunWithRestDayHours(employee, workedHours: 8);

        // 144.230769... * 1.30 * 8.00 = 1,500.00 exactly -- the boundary hour itself
        // belongs to the first tier, so nothing spills into the overtime one.
        Assert.Equal(1500.00m, result.ComputedGrossPay[3].Amount);

        // No "+ OT" segment: the label only splits when a second tier actually
        // contributed, so an ordinary 8-hour Rest Day reads exactly as it always did.
        Assert.Equal("Rest Day Pay (8.00H)", result.ComputedGrossPay[3].Label);
    }

    [Fact]
    public void HalfAnHourPastEight_PricesOnlyThatHalfHourAtTheOvertimeTier()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var result = RunWithRestDayHours(employee, workedHours: 8.5);

        //   first 8.0h : 144.230769... * 1.30        * 8.00 = 1,500.000
        //   next  0.5h : 144.230769... * 1.30 * 1.30 * 0.50 =   121.875
        //                                                      ----------
        //                                                      1,621.875
        //
        // Which rounds to 1,621.87, NOT the 1,621.88 that midpoint would give under
        // exact arithmetic. 15,000/13/8 doesn't terminate in decimal, so the real
        // intermediate is a hair under 1,621.875 and never reaches the midpoint
        // MidpointRounding.AwayFromZero would round up. Worth pinning precisely
        // because it's the one place these tiers can land on a half-centavo at all
        // (a whole-hour split always ends in .00/.25/.50/.75), and because it
        // demonstrates the calculator's rounding contract: round once, at the end,
        // off full-precision intermediates -- rounding hourlyRate to 2dp first would
        // give a different, wronger answer here.
        Assert.Equal(8.50m, result.RestDayHours);
        Assert.Equal(1621.87m, result.ComputedGrossPay[3].Amount);
        Assert.Equal("Rest Day Pay (8.00H + 0.50H OT)", result.ComputedGrossPay[3].Label);
    }

    [Fact]
    public void ElevenHours_SplitsEightAndThree()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var result = RunWithRestDayHours(employee, workedHours: 11);

        //   first 8h : 144.230769... * 1.30        * 8.00 = 1,500.00
        //   next  3h : 144.230769... * 1.30 * 1.30 * 3.00 =   731.25
        //                                                   ---------
        //                                                    2,231.25
        Assert.Equal(11.00m, result.RestDayHours);
        Assert.Equal(2231.25m, result.ComputedGrossPay[3].Amount);
        Assert.Equal("Rest Day Pay (8.00H + 3.00H OT)", result.ComputedGrossPay[3].Label);
    }

    [Fact]
    public void EmployeeOverride_WinsOverThePolicyDefault()
    {
        // 50% rather than the policy's 30% -- so 150% on the first tier and
        // 150% * 1.30 = 195% on the second. The overtime percentage itself has no
        // per-employee tier (see PayrollPolicy.RestDayOvertimeRatePercentage), so
        // only the base premium moves.
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.50m);

        var result = RunWithRestDayHours(employee, workedHours: 11);

        //   first 8h : 144.230769... * 1.50        * 8.00 = 1,730.769230...
        //   next  3h : 144.230769... * 1.50 * 1.30 * 3.00 =   843.750000
        //                                                   --------------
        //                                                    2,574.52 (rounded once, at the end)
        Assert.Equal(2574.52m, result.ComputedGrossPay[3].Amount);
    }

    [Fact]
    public void NoEmployeeOverride_InheritsThePolicyDefault()
    {
        // The regression guard described in this class's own doc comment: a null
        // override has to fall through to PayrollPolicy.RestDayPremiumPercentage,
        // not to zero. If this ever comes back 1,153.85 (straight time,
        // 144.230769... * 8.00), the nullable-means-inherit contract has broken and
        // every unconfigured employee is silently back on pre-migration behaviour.
        var employee = MonthlyEmployee();
        Assert.Null(employee.RestDayWorkPremiumPercentage);

        var result = RunWithRestDayHours(employee, workedHours: 8);

        Assert.Equal(1500.00m, result.ComputedGrossPay[3].Amount);
    }

    [Fact]
    public void ExplicitZeroOverride_StillMeansStraightTime()
    {
        // The other side of the same contract: 0 is now a real, expressible choice
        // that means "no premium at all", distinct from null's "inherit". Nothing in
        // the app writes it today (the dialog leaves the box blank), but the column
        // can hold it, so it should behave predictably rather than being quietly
        // treated as unset.
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0m);

        var result = RunWithRestDayHours(employee, workedHours: 8);

        // 144.230769... * 1.00 * 8.00 = 1,153.85 (rounded).
        Assert.Equal(1153.85m, result.ComputedGrossPay[3].Amount);
    }

    [Fact]
    public void DailyRatedEmployee_GetsBothTiersToo()
    {
        // Rest Day Pay was never gated behind Pay Type (a Daily-rated employee can
        // be called in on a Rest Day just as easily), and neither is the split --
        // this is the Daily-rated counterpart to ElevenHours_SplitsEightAndThree
        // above, on a rate that divides evenly so the arithmetic is checkable at a
        // glance: 800.00 / 8 = 100.00 per hour, no repeating decimal in sight.
        var employee = new Employee
        {
            Pin = 2002,
            LastName = "Santos",
            FirstName = "Maria",
            EmployeeType = EmployeeType.Daily,
            DailyRate = 800.00m,
            QualifiesForRestDayPay = true,
        };

        var result = RunWithRestDayHours(employee, workedHours: 11);

        //   first 8h : 100.00 * 1.30        * 8.00 = 1,040.00
        //   next  3h : 100.00 * 1.30 * 1.30 * 3.00 =   507.00
        //                                            ---------
        //                                             1,547.00
        Assert.Equal(1547.00m, result.ComputedGrossPay[3].Amount);
        Assert.Equal("Rest Day Pay (8.00H + 3.00H OT)", result.ComputedGrossPay[3].Label);
    }

    [Fact]
    public void NightDiffOnALongRestDay_StaysOnTheFirstTierRate()
    {
        // The documented simplification in PayrollCalculator's Night Diff block:
        // AttendanceSummary carries only a total NightDiffHours, with nothing saying
        // which of those hours fell past the eighth, so all of them price at the
        // 130% first-tier rate even on a day that reached the 169% tier. Pinned here
        // so the choice is visible as a decision rather than rediscovered as a
        // surprise -- if per-tier night-diff allocation is ever wanted, this is the
        // test that should fail and be rewritten.
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s =>
            {
                MakeRestDay(s, workedHours: 11);
                s.NightDiffHours = 2; // both of them past the eighth hour, in practice
            }));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // 144.230769... * 1.30 * 2.00 * 0.10 = 37.50 -- the same figure an 8-hour
        // Rest Day would produce, not 144.230769... * 1.69 * 2.00 * 0.10 = 48.75.
        Assert.Equal(37.50m, result.ComputedGrossPay[2].Amount);
    }

    [Fact]
    public void OrdinaryWorkdayNightDiff_IsUnaffected()
    {
        // Guard on the other half of the ndBaseRate change: only a RestDay day
        // switches base. A Normal day's Night Diff must still be taken against the
        // plain hourly rate, or every ordinary night shift in the app silently got a
        // 30% raise alongside this feature.
        var employee = MonthlyEmployee();

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 5), s =>
            {
                s.ScheduleType = ScheduleType.Normal;
                s.Status = PunchStatus.Complete;
                s.NightDiffHours = 2;
            }));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // 144.230769... * 2.00 * 0.10 = 28.85 (rounded) -- base rate, no 1.30.
        Assert.Equal(28.85m, result.ComputedGrossPay[2].Amount);
    }
}
