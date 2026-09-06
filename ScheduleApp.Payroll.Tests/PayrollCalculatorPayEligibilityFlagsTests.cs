using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Pay Eligibility Flags plan, Phase 7 -- the coverage Phase 6's own Bug 3
/// fix explicitly left unwritten. That fix restored six pre-existing asserts
/// to green by setting <c>QualifiesForRestDayPay = true</c> on their
/// employees, which only re-confirmed the *eligible* side of the flag --
/// already covered, incidentally, by every test in
/// <see cref="PayrollCalculatorRestDayWorkedExampleTests"/> and
/// <see cref="PayrollCalculatorDailyRatedRegressionTests"/> now that both
/// set it too. Nothing anywhere yet exercised the *false* side of either
/// flag, or the two together. The three cases below (lettered to match the
/// plan's own Phase 7 bullets) are exactly that gap:
/// (a) <c>QualifiesForRestDayPay == false</c> with a Rest Day genuinely
/// worked, (b) <c>QualifiesForPremiumPay == false</c> with a
/// <see cref="PayrollAdjustmentType.PremiumHoliday"/> row already on file,
/// and (c) both flags <c>true</c> together, as an explicit regression check
/// that turning both on at once doesn't disturb either flag's own math.
///
/// Cases (a) and (c) reuse the exact Aug 1-15 2026 / Aug 2 &amp; Aug 9
/// RestDay / Aug 7 Absent shape
/// <see cref="PayrollCalculatorRestDayWorkedExampleTests"/> hand-verifies
/// against the design doc's own §11 worked example, so the Basic Pay
/// (13,846.15) and worked-Rest-Day-at-30%-premium (1,500.00) figures
/// asserted here aren't independently re-derived -- only whether the two
/// eligibility flags correctly gate numbers already known to be correct.
/// </summary>
public class PayrollCalculatorPayEligibilityFlagsTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, OT=25%, ND=10%

    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 15);

    /// <summary>Case (a): QualifiesForRestDayPay = false with a worked Rest
    /// Day day still present in the summaries -- asserts no "Rest Day Pay"
    /// line in ComputedGrossPay.</summary>
    [Fact]
    public void QualifiesForRestDayPay_False_OmitsTheLineEvenWhenARestDayWasActuallyWorked()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        // MonthlyEmployee() always sets this true (see its own doc comment,
        // written for the pre-existing tests that need it) -- this is the
        // one caller that needs it false, so it's flipped directly on the
        // returned Employee rather than adding a builder parameter neither
        // of the other two test files would ever pass.
        employee.QualifiesForRestDayPay = false;

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s, workedHours: 8)),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End);

        // The line itself: gone, not present-at-zero -- only the three
        // always-shown lines (Basic Pay/Overtime/Night Diff) remain.
        Assert.Equal(3, result.ComputedGrossPay.Count);
        Assert.DoesNotContain(result.ComputedGrossPay, line => line.Label.StartsWith("Rest Day Pay", StringComparison.Ordinal));

        // RestDayHours stays the real worked-hours figure regardless of
        // eligibility (PayrollResult.RestDayHours' own doc comment: "hours
        // worked is a fact independent of whether they're paid for it") --
        // the per-day accumulation loop that builds it has no
        // QualifiesForRestDayPay check at all. RestDayPayAmount is the
        // opposite: gated to 0.00, since it stands in for what was actually
        // credited this period, and an ineligible employee is credited
        // nothing.
        Assert.Equal(8.00m, result.RestDayHours);
        Assert.Equal(0.00m, result.RestDayPayAmount);

        // Basic Pay is untouched by this flag, and TotalGrossPay is exactly
        // that figure -- confirming the would-be 1,500.00 Rest Day Pay (see
        // OneRestDayWorkedAtThirtyPercentPremium_PaysExactly1500ForEightHours
        // for the identical scenario, eligible) never reaches the total
        // either, not just the line list.
        Assert.Equal(13846.15m, result.ComputedGrossPay[0].Amount);
        Assert.Equal(13846.15m, result.TotalGrossPay);
    }

    /// <summary>Case (b): QualifiesForPremiumPay = false with an existing
    /// PremiumHoliday adjustment row passed in -- asserts no group with that
    /// type in GrossPayAdjustmentGroups.</summary>
    [Fact]
    public void QualifiesForPremiumPay_False_OmitsTheGroupEvenWithAnExistingAdjustmentRowOnFile()
    {
        var employee = MonthlyEmployee();

        // QualifiesForPremiumPay is already false -- Employee's own default
        // (Phase 1), never set true by MonthlyEmployee(). Also forced
        // QualifiesForRestDayPay false here (unlike case (a), where it's the
        // property under test) purely to keep ComputedGrossPay/TotalGrossPay
        // to the plain 3-line/no-adjustment shape, so this test's only
        // moving part is the Premium Pay group.
        employee.QualifiesForRestDayPay = false;

        var summaries = BuildDays(employee.Pin, Start, End); // no overrides -- a plain, fully-attended 15-day period

        // A PremiumHoliday row already on file for this employee/period --
        // e.g. typed in before the flag was turned off, or before it existed
        // at all. Phase 4's own gate is documented as non-destructive: a row
        // like this is never deleted, just excluded from the group (and the
        // total) while the flag is off.
        var premiumHolidayAdjustment = new PayrollAdjustment
        {
            EmployeeId = employee.Pin,
            PeriodStart = Start,
            PeriodEnd = End,
            Type = PayrollAdjustmentType.PremiumHoliday,
            Amount = 1000.00m,
            Description = "Regular holiday premium",
        };

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [premiumHolidayAdjustment], Start, End);

        // The group itself: omitted outright (PayrollAdjustmentGroup's own
        // class doc comment), not present with an empty Adjustments list --
        // only Allowance and Incentive remain, in the same relative
        // (enum-declaration) order the Phase 6 Bug 2 fix preserves.
        Assert.DoesNotContain(result.GrossPayAdjustmentGroups, g => g.Type == PayrollAdjustmentType.PremiumHoliday);
        Assert.Equal(
            [PayrollAdjustmentType.Allowance, PayrollAdjustmentType.Incentive],
            result.GrossPayAdjustmentGroups.Select(g => g.Type));

        // The 1,000.00 row is skipped, not silently double-counted or
        // reattributed to another card (the exact failure Bug 2 describes) --
        // TotalGrossPay is just Basic Pay for a plain 15-day period, with
        // nothing from the excluded row folded in anywhere.
        Assert.Equal(15000.00m, result.ComputedGrossPay[0].Amount);
        Assert.Equal(15000.00m, result.TotalGrossPay);
    }

    /// <summary>Case (c): both true -- unchanged existing behavior
    /// (regression).</summary>
    [Fact]
    public void BothFlagsTrue_RestDayPayLineAndPremiumHolidayGroupBothAppear_Unchanged()
    {
        var employee = MonthlyEmployee(restDayWorkPremiumPercentage: 0.30m);

        // QualifiesForRestDayPay is already true from the builder;
        // QualifiesForPremiumPay is the one flag it doesn't set, so it's
        // flipped on directly here -- same reasoning as case (a) above, just
        // in the true direction.
        employee.QualifiesForPremiumPay = true;

        var summaries = BuildDays(employee.Pin, Start, End,
            (new DateOnly(2026, 8, 2), s => MakeRestDay(s)),
            (new DateOnly(2026, 8, 9), s => MakeRestDay(s, workedHours: 8)),
            (new DateOnly(2026, 8, 7), MakeAbsent));

        var premiumHolidayAdjustment = new PayrollAdjustment
        {
            EmployeeId = employee.Pin,
            PeriodStart = Start,
            PeriodEnd = End,
            Type = PayrollAdjustmentType.PremiumHoliday,
            Amount = 1000.00m,
            Description = "Regular holiday premium",
        };

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [premiumHolidayAdjustment], Start, End);

        // Rest Day Pay line: back to the same 1,500.00 figure
        // OneRestDayWorkedAtThirtyPercentPremium_PaysExactly1500ForEightHours
        // already hand-verifies for this exact scenario -- eligibility being
        // on for both flags at once doesn't perturb either one's own math.
        Assert.Equal(4, result.ComputedGrossPay.Count);
        Assert.Equal(1500.00m, result.ComputedGrossPay[3].Amount);
        Assert.Equal(8.00m, result.RestDayHours);
        Assert.Equal(1500.00m, result.RestDayPayAmount);

        // Premium Pay group: present, first (PremiumHoliday's own
        // declaration-order position, ahead of Allowance/Incentive), carrying
        // the one row on file as its Subtotal.
        Assert.Equal(3, result.GrossPayAdjustmentGroups.Count);
        Assert.Equal(PayrollAdjustmentType.PremiumHoliday, result.GrossPayAdjustmentGroups[0].Type);
        Assert.Equal(1000.00m, result.GrossPayAdjustmentGroups[0].Subtotal);

        // Everything lands in the total: 13,846.15 Basic Pay + 1,500.00 Rest
        // Day Pay + 1,000.00 Premium Pay, nothing excluded now that both
        // flags are on.
        Assert.Equal(16346.15m, result.TotalGrossPay);
    }
}
