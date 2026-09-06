using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using Xunit;

namespace ScheduleApp.Payroll.Tests;

/// <summary>
/// Phase 3.8's second exit-check bullet: "a Daily-rated employee's payroll
/// output is provably unchanged." Every figure asserted below comes straight
/// from the pre-existing Daily-rated formula (flat DailyRate per credited
/// day; hourlyRate = DailyRate / StandardHoursPerDay for Overtime/Night
/// Diff/Undertime) -- nothing here is Monthly-specific, and per
/// PayrollCalculator §3.2 the Daily branch has zero changed lines from
/// before this feature.
///
/// The period also mixes in a Rest Day (a Daily-rated employee can be called
/// in on one too, per Employee.RestDayWorkPremiumPercentage's own doc
/// comment) specifically to confirm that day neither contributes to nor is
/// deducted from Basic Pay/WorkDays -- it only ever produces the separate,
/// always-applies-to-both-employee-types Rest Day Pay line.
/// </summary>
public class PayrollCalculatorDailyRatedRegressionTests
{
    private static readonly PayrollPolicy Policy = new(); // StandardHoursPerDay=8, OT=25%, ND=10%

    [Fact]
    public void DailyRatedEmployee_BasicPayOvertimeNightDiffAndRestDayPay_AllComputeIndependently()
    {
        var employee = new Employee
        {
            Pin = 2002,
            LastName = "Santos",
            FirstName = "Maria",
            EmployeeType = EmployeeType.Daily,
            DailyRate = 800.00m,
            RestDayWorkPremiumPercentage = 0.20m,
            // QualifiesForRestDayPay: true -- added confirming Pay Eligibility Flags plan
            // Phase 6. This test asserts result.ComputedGrossPay[3] below for the Rest Day
            // Pay line, written back when that line always existed; Phase 4 of that later
            // plan made it conditional on this flag (default false), which would otherwise
            // turn this into an index-out-of-range failure instead of the arithmetic check
            // it's actually meant to be.
            QualifiesForRestDayPay = true,
        };

        var start = new DateOnly(2026, 8, 1);
        var end = new DateOnly(2026, 8, 15);

        var restDaySunday1 = new DateOnly(2026, 8, 2);
        var restDaySunday2Worked = new DateOnly(2026, 8, 9);
        var absentDay = new DateOnly(2026, 8, 7);
        var overtimeDay = new DateOnly(2026, 8, 8);
        var nightDiffDay = new DateOnly(2026, 8, 10);

        List<AttendanceSummary> summaries = [];
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var summary = new AttendanceSummary
            {
                EmployeeId = employee.Pin,
                ShiftDate = date,
                Status = PunchStatus.Complete,
                ScheduleType = ScheduleType.Normal,
            };

            if (date == restDaySunday1)
            {
                summary.ScheduleType = ScheduleType.RestDay;
                summary.Status = PunchStatus.RestDay;
            }
            else if (date == restDaySunday2Worked)
            {
                summary.ScheduleType = ScheduleType.RestDay;
                summary.Status = PunchStatus.RestDay;
                summary.Worked_H = 4; // worked, at the 20% Rest Day premium above
            }
            else if (date == absentDay)
            {
                summary.Status = PunchStatus.Absent;
            }
            else if (date == overtimeDay)
            {
                summary.Overtime_H = 2;
            }
            else if (date == nightDiffDay)
            {
                summary.NightDiff_H = 1;
            }

            summaries.Add(summary);
        }

        var result = PayrollCalculator.Calculate(employee, Policy, summaries, [], start, end);

        // Basic Pay: flat DailyRate * credited days -- 15 total days minus
        // 2 RestDay minus 1 Absent = 12 Complete days -- untouched by any
        // EmployeeType/divisor logic.
        Assert.Equal(9600.00m, result.ComputedGrossPay[0].Amount); // 800 * 12
        Assert.Equal("Basic Pay (12D)", result.ComputedGrossPay[0].Label);
        Assert.Equal(12, result.WorkDays);

        // Overtime/Night Diff: hourlyRate = 800/8 = 100 -- same formula,
        // same figures as before this feature existed.
        Assert.Equal(2.00m, result.OvertimeHours);
        Assert.Equal(250.00m, result.ComputedGrossPay[1].Amount); // 100 * 2 * 1.25
        Assert.Equal(1.00m, result.NightDiffHours);
        Assert.Equal(10.00m, result.ComputedGrossPay[2].Amount); // 100 * 1 * 0.10

        // Rest Day Pay: the one new line, but applies identically to a
        // Daily-rated employee -- 100 * 4 * 1.20 = 480.00 -- and Aug 9 still
        // contributed nothing to (and cost nothing from) Basic Pay above.
        Assert.Equal(4.00m, result.RestDayHours);
        Assert.Equal(480.00m, result.ComputedGrossPay[3].Amount);

        Assert.Equal(0.00m, result.ComputedDeductions[0].Amount); // Undertime
        Assert.Equal(0, result.UnscheduledDayCount);
    }
}
