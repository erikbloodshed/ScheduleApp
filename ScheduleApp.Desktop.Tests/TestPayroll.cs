using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.Tests;

/// <summary>Payroll figures for the view-model tests: an empty batch, a result with its gross
/// pay all basic and one deduction, and a computation service paying everyone that.</summary>
internal static class TestPayroll
{
    /// <summary>What <see cref="Computation"/> pays each employee.</summary>
    public const decimal Gross = 1000m;
    public const decimal Deduction = 100m;

    /// <summary>A computation service that pays every employee <see cref="Gross"/> less
    /// <see cref="Deduction"/>, one at a time or in a batch, with no attendance rows.</summary>
    public static IPayrollComputationService Computation()
    {
        var payroll = Substitute.For<IPayrollComputationService>();
        var batch = EmptyBatch();
        payroll.PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(batch);
        payroll.SeedContributionsForBatchAsync(Arg.Any<IReadOnlyCollection<Employee>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), batch, Arg.Any<CancellationToken>())
            .Returns(batch);
        payroll.ComputeOneFromBatchAsync(Arg.Any<Employee>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), batch, Arg.Any<CancellationToken>())
            .Returns(call => Computed(call.Arg<Employee>()));
        payroll.ComputeOneAsync(Arg.Any<Employee>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Computed(call.Arg<Employee>()));
        payroll.ComputeOneAdjustmentsOnlyAsync(Arg.Any<Employee>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Computed(call.Arg<Employee>()));
        return payroll;
    }

    private static Task<(PayrollResult, IReadOnlyList<AttendanceSummary>)> Computed(Employee employee) =>
        Task.FromResult<(PayrollResult, IReadOnlyList<AttendanceSummary>)>((Result(employee, Gross, Deduction), []));

    public static PayrollBatchContext EmptyBatch() => new()
    {
        AllSummaries = [],
        AdjustmentsByPin = new Dictionary<int, IReadOnlyList<ScheduleApp.Core.Payroll.PayrollAdjustment>>(),
        WaivedPins = new HashSet<int>(),
        HolidayDates = [],
    };

    public static PayrollResult Result(Employee employee, decimal gross, decimal deduction) => new()
    {
        EmployeeId = employee.Pin,
        EmployeeName = employee.DisplayName,
        PeriodStart = new DateOnly(2026, 9, 16),
        PeriodEnd = new DateOnly(2026, 9, 30),
        // Basic, Overtime and Night Diff, always in that order, as PayrollCalculator produces them.
        ComputedGrossPay =
        [
            new() { Label = "Basic Pay", Amount = gross },
            new() { Label = "Overtime", Amount = 0m },
            new() { Label = "Night Differential", Amount = 0m },
        ],
        ComputedDeductions = [new() { Label = "Undertime", Amount = deduction }],
        WorkDays = 11,
        UnscheduledDayCount = 0,
        OvertimeHours = 0,
        NightDiffHours = 0,
        RestDayHours = 0,
        RestDayPayAmount = 0,
        HolidayPayAmount = 0,
        HolidayWorkedDays = 0,
        UndertimeHours = 0,
        UndertimePayAmount = deduction,
        UndertimeWaived = false,
        GrossPayAdjustmentGroups = [],
        DeductionAdjustmentGroups = [],
        NetPayRoundingMultiple = 0,
    };
}
