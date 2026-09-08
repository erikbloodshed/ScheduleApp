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
/// separate Undertime deduction line. This flag reuses the pre-existing
/// PayrollLineItem.Waived/PayrollResult.TotalDeductions machinery a person's manual
/// per-period "disregard" already goes through (see IPayrollUndertimeWaiverRepository)
/// rather than a parallel "zero out the hours" path -- the Undertime figure keeps
/// showing on the payslip for reference, it just never subtracts, and the exemption
/// holds regardless of whatever that period's own manual waiver toggle happens to be
/// set to.
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
    public void ExemptEmployee_UndertimeStillShowsButNeverReducesTheTotal()
    {
        // The core of the feature: same figure computed and displayed as the
        // non-exempt case above -- only whether it counts toward the total differs.
        var employee = DailyEmployee(exempt: true);
        var result = Run(employee);

        Assert.Equal(2.00m, result.UndertimeHours);
        Assert.Equal(200.00m, result.ComputedDeductions[0].Amount);
        Assert.True(result.ComputedDeductions[0].Waived);
        Assert.Equal("Undertime (2.00H), Excluded", result.ComputedDeductions[0].Label);

        // The 200.00 that reduced NormalEmployee's total above simply isn't
        // subtracted here -- this employee's Net Pay is 200.00 higher for the
        // same hours worked.
        Assert.Equal(0.00m, result.TotalDeductions);
    }

    [Fact]
    public void ExemptEmployee_HidesTheManualWaiverToggle()
    {
        // PayrollSummaryView's own per-period Exclude/Include toggle would be a
        // no-op for this employee (Waived above is already permanently true
        // regardless of what that toggle is set to) -- hidden entirely rather
        // than offered as a button that visibly does nothing when clicked.
        var employee = DailyEmployee(exempt: true);
        var result = Run(employee);

        Assert.False(result.ComputedDeductions[0].SupportsWaiver);
    }

    [Fact]
    public void ExemptEmployee_StaysExcludedEvenWithThePerPeriodWaiverOff()
    {
        // undertimeWaived: false -- nobody has touched this period's own manual
        // waiver toggle (IPayrollUndertimeWaiverRepository). The employee-level
        // flag alone must still exclude the deduction; this is the regression
        // guard for the OR, not a replace, in PayrollCalculator.Calculate.
        var employee = DailyEmployee(exempt: true);
        var result = Run(employee, undertimeWaived: false);

        Assert.True(result.ComputedDeductions[0].Waived);
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
