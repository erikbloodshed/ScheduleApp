using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using Xunit;
using static ScheduleApp.Payroll.Tests.PayrollCalculatorTestFixtures;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Employee.ExemptFromUndertimeDeduction -- for a daily-rated employee whose day
/// doesn't need to be completed to earn a full day's pay, only exceeded to earn
/// extra. Basic Pay was already a flat DailyRate per credited day regardless of
/// actual hours worked (see PayrollCalculator.BasicPayForDay); the only thing that
/// ever reduced pay for falling short of a day's scheduled Work Time was the
/// separate Undertime deduction line. The Undertime line is omitted from
/// ComputedDeductions entirely for an exempt employee -- not shown-but-never-
/// subtracted, which was this feature's original shape before the Payroll Summary
/// was asked to stop surfacing it for them at all -- while UndertimeHours/
/// UndertimePayAmount/UndertimeWaived on PayrollResult still carry the real
/// figures for a reader that wants them regardless (e.g. PayrollExcelExporter).
/// The exemption holds regardless of whatever that period's own manual waiver
/// toggle (IPayrollUndertimeWaiverRepository) happens to be set to.
///
/// Overtime is deliberately untouched by any of this -- hours worked *beyond* the
/// day's scheduled Work Time were already priced independently of Undertime before
/// this flag existed, so the tests below confirm that stayed true rather than
/// re-deriving it.
///
/// Shared scenario: Daily-rated, DailyRate 800.00 (hourlyRate = 800/8 = 100.00 --
/// StandardHoursPerDay is the PayrollPolicy default of 8), Aug 1-15 2026, Aug 5 two
/// hours short (Undertime) and Aug 6 one hour over (Overtime) -- both days otherwise
/// resolve to PunchStatus.Complete same as every other day in the period, so Basic
/// Pay credits all 15.
/// </summary>
public class PayrollCalculatorUndertimeExemptionTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, OT=25%

    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 15);
    private static readonly DateOnly UndertimeDay = new(2026, 8, 5);
    private static readonly DateOnly OvertimeDay = new(2026, 8, 6);

    private static Employee DailyEmployee(bool exempt) => new()
    {
        Pin = 3001,
        LastName = "Reyes",
        FirstName = "Ana",
        EmployeeType = EmployeeType.Daily,
        DailyRate = 800.00m,
        ExemptFromUndertimeDeduction = exempt,
    };

    private static PayrollResult Run(Employee employee, bool undertimeWaived = false)
    {
        var summaries = BuildDays(employee.Pin, Start, End,
            (UndertimeDay, s => s.RemainHours = 2),
            (OvertimeDay, s => s.OvertimeHours = 1));

        return PayrollCalculator.Calculate(employee, Policy, summaries, [], Start, End, undertimeWaived);
    }

    [Fact]
    public void NormalEmployee_UndertimeDeductsFromTotal()
    {
        // Baseline -- the pre-existing behaviour every unexempted employee (the
        // default; ExemptFromUndertimeDeduction is false unless explicitly turned
        // on) still gets, unchanged by this feature existing at all.
        var employee = DailyEmployee(exempt: false);
        var result = Run(employee);

        // 100.00 * 2 = 200.00
        Assert.Equal(2.00m, result.UndertimeHours);
        Assert.Equal(200.00m, result.ComputedDeductions[0].Amount);
        Assert.False(result.ComputedDeductions[0].Waived);
        Assert.True(result.ComputedDeductions[0].SupportsWaiver);
        Assert.Equal("Undertime (2.00H)", result.ComputedDeductions[0].Label);
        Assert.Equal(200.00m, result.TotalDeductions);
    }

    [Fact]
    public void ExemptEmployee_OmitsTheLineEntirely()
    {
        // The core of the feature: no "Undertime" line at all for this employee --
        // not present-with-Waived-true (this test's own name until the Payroll
        // Summary was asked to stop surfacing it for exempt employees at all), the
        // same "omit rather than show excluded" treatment Rest Day Pay gets from
        // QualifiesForRestDayPay (see PayrollCalculatorPayEligibilityFlagsTests).
        // That also means there's no per-period Exclude/Include toggle to show --
        // trivially true of an empty list, so no separate assertion for it.
        var employee = DailyEmployee(exempt: true);
        var result = Run(employee);

        Assert.Empty(result.ComputedDeductions);

        // The figures themselves are still computed and available off
        // PayrollResult directly, same as the non-exempt case above -- only
        // whether they're shown as a line (and whether they count) differs.
        Assert.Equal(2.00m, result.UndertimeHours);
        Assert.Equal(200.00m, result.UndertimePayAmount);
        Assert.True(result.UndertimeWaived);

        // The 200.00 that reduced NormalEmployee's total above simply isn't
        // subtracted here -- this employee's Net Pay is 200.00 higher for the
        // same hours worked.
        Assert.Equal(0.00m, result.TotalDeductions);
    }

    [Fact]
    public void ExemptEmployee_StaysExcludedEvenWithThePerPeriodWaiverOff()
    {
        // undertimeWaived: false -- nobody has touched this period's own manual
        // waiver toggle (IPayrollUndertimeWaiverRepository). The employee-level
        // flag alone must still omit/exclude the deduction; this is the regression
        // guard for the OR, not a replace, in PayrollCalculator.Calculate.
        var employee = DailyEmployee(exempt: true);
        var result = Run(employee, undertimeWaived: false);

        Assert.Empty(result.ComputedDeductions);
        Assert.True(result.UndertimeWaived);
        Assert.Equal(0.00m, result.TotalDeductions);
    }

    [Fact]
    public void NonExemptEmployee_ManualWaiverStillWorksNormally()
    {
        // The other regression guard: ORing in employee.ExemptFromUndertimeDeduction
        // must not disturb the pre-existing per-period manual waiver for an ordinary
        // employee who simply had this one period's Undertime disregarded by hand.
        var employee = DailyEmployee(exempt: false);
        var result = Run(employee, undertimeWaived: true);

        Assert.True(result.ComputedDeductions[0].Waived);
        Assert.True(result.ComputedDeductions[0].SupportsWaiver);
        Assert.Equal(0.00m, result.TotalDeductions);
    }

    [Fact]
    public void ExemptEmployee_OvertimeBeyondScheduledHoursStillPaysNormally()
    {
        // The other half of what this employee is opted into: exceeding the day's
        // scheduled Work Time is still Overtime, identical to any other employee --
        // only the shortfall side (Undertime) is affected by the flag.
        var exemptResult = Run(DailyEmployee(exempt: true));
        var normalResult = Run(DailyEmployee(exempt: false));

        // 100.00 * 1 * 1.25 = 125.00
        Assert.Equal(125.00m, exemptResult.ComputedGrossPay[1].Amount);
        Assert.Equal(normalResult.ComputedGrossPay[1].Amount, exemptResult.ComputedGrossPay[1].Amount);
    }

    [Fact]
    public void ExemptEmployee_BasicPayStaysFullRegardlessOfShortfall()
    {
        // Basic Pay was already a flat DailyRate per credited day before this
        // feature existed -- Undertime is a wholly separate deduction line (see
        // PayrollCalculator.BasicPayForDay). Pinned here since it's the other half
        // of "always get their full salary" the feature request itself described,
        // even though nothing about Basic Pay's own computation actually changed
        // to make it true.
        var employee = DailyEmployee(exempt: true);
        var result = Run(employee);

        // All 15 days resolve to PunchStatus.Complete -- RemainHours/OvertimeHours
        // don't change a day's Status -- so all 15 are credited: 800.00 * 15.
        Assert.Equal(12000.00m, result.ComputedGrossPay[0].Amount);
        Assert.Equal("Basic Pay (15D)", result.ComputedGrossPay[0].Label);
    }
}
