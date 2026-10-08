using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Data.Tests.Fixtures;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// The Dapper reads in HolidayRepository, PayrollRunRepository, PayrollAdjustmentRepository and
/// PayrollUndertimeWaiverRepository.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PayrollReadTests(DatabaseFixture fixture) : PersistenceTestBase(fixture)
{
    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 3, 15);

    private static PayrollAdjustment Adjustment(int employeeId, PayrollAdjustmentType type, decimal amount,
        DateOnly? periodStart = null, DateTime? createdAt = null) => new()
        {
            EmployeeId = employeeId,
            PeriodStart = periodStart ?? PeriodStart,
            PeriodEnd = PeriodEnd,
            Type = type,
            Amount = amount,
            Description = $"{type} {amount}",
            EnteredBy = "tester",
            CreatedAt = createdAt ?? new DateTime(2026, 3, 16, 9, 0, 0),
        };

    [Fact]
    public async Task Holidays_ComeByDate_AndDatesFilterToThePeriod()
    {
        Db.Holidays.AddRange(
            new Holiday { Date = new DateOnly(2026, 4, 9), Name = "Araw ng Kagitingan" },
            new Holiday { Date = new DateOnly(2026, 1, 1), Name = "New Year's Day" },
            new Holiday { Date = new DateOnly(2026, 4, 2), Name = "Maundy Thursday" });
        await Db.SaveChangesAsync();
        var repository = new HolidayRepository(Db);

        var holidays = await repository.ListAsync();
        var dates = await repository.ListDatesForPeriodAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 9));

        Assert.Equal(["New Year's Day", "Maundy Thursday", "Araw ng Kagitingan"], holidays.Select(h => h.Name));
        Assert.Equal([new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 9)], dates);
    }

    [Fact]
    public async Task PayrollRuns_ListNewestFirst_EachWithItsMembers()
    {
        var older = new PayrollRun
        {
            Label = "Older", PeriodStart = PeriodStart, PeriodEnd = PeriodEnd, CreatedBy = "tester",
            CreatedAt = new DateTime(2026, 3, 16, 8, 0, 0, 123).AddTicks(4567),
            Employees = [new PayrollRunEmployee { EmployeeId = 1002 }, new PayrollRunEmployee { EmployeeId = 1001 }],
        };
        var newer = new PayrollRun
        {
            Label = "Newer", PeriodStart = PeriodStart, PeriodEnd = PeriodEnd, CreatedBy = "tester",
            CreatedAt = new DateTime(2026, 3, 17, 8, 0, 0),
        };
        Db.PayrollRuns.AddRange(older, newer);
        await Db.SaveChangesAsync();
        var repository = new PayrollRunRepository(Db);

        var runs = await repository.ListAsync();
        var byId = await repository.GetByIdAsync(older.Id);

        Assert.Equal(["Newer", "Older"], runs.Select(r => r.Label));
        Assert.Empty(runs[0].Employees);
        Assert.Equal([1002, 1001], runs[1].Employees.Select(e => e.EmployeeId));
        Assert.NotNull(byId);
        Scalars.AssertEqual(older, byId);
        Assert.Equal([1002, 1001], byId.Employees.Select(e => e.EmployeeId));
        Assert.Null(await repository.GetByIdAsync(older.Id + newer.Id + 1));
    }

    [Fact]
    public async Task Adjustments_ForOneEmployee_ComeByTypeNameThenCreatedAt()
    {
        Db.PayrollAdjustments.AddRange(
            Adjustment(1001, PayrollAdjustmentType.SSS, 450m),
            Adjustment(1001, PayrollAdjustmentType.Allowance, 200m, createdAt: new DateTime(2026, 3, 16, 10, 0, 0)),
            Adjustment(1001, PayrollAdjustmentType.Allowance, 100m, createdAt: new DateTime(2026, 3, 16, 8, 0, 0)),
            Adjustment(1001, PayrollAdjustmentType.CashAdvance, 500m),
            Adjustment(1001, PayrollAdjustmentType.Incentive, 50m, periodStart: new DateOnly(2026, 3, 2)),
            Adjustment(1002, PayrollAdjustmentType.Charge, 75m));
        await Db.SaveChangesAsync();

        var adjustments = await new PayrollAdjustmentRepository(Db).GetForEmployeePeriodAsync(1001, PeriodStart, PeriodEnd);

        // Type is stored by name, so these sort alphabetically, not by the enum's value.
        Assert.Equal(
            [(PayrollAdjustmentType.Allowance, 100m), (PayrollAdjustmentType.Allowance, 200m),
             (PayrollAdjustmentType.CashAdvance, 500m), (PayrollAdjustmentType.SSS, 450m)],
            adjustments.Select(a => (a.Type, a.Amount)));
        Assert.Equal("Allowance 100", adjustments[0].Description);
    }

    [Fact]
    public async Task Adjustments_ForSeveralEmployees_ComeByEmployeeThenType()
    {
        var saved = Adjustment(1002, PayrollAdjustmentType.PhilHealth, 250.75m);
        Db.PayrollAdjustments.AddRange(
            saved,
            Adjustment(1001, PayrollAdjustmentType.PagIbig, 100m),
            Adjustment(1002, PayrollAdjustmentType.Charge, 75m),
            Adjustment(1003, PayrollAdjustmentType.Charge, 10m));
        await Db.SaveChangesAsync();
        var repository = new PayrollAdjustmentRepository(Db);

        var adjustments = await repository.GetForEmployeesPeriodAsync([1002, 1001], PeriodStart, PeriodEnd);

        Assert.Equal(
            [(1001, PayrollAdjustmentType.PagIbig), (1002, PayrollAdjustmentType.Charge), (1002, PayrollAdjustmentType.PhilHealth)],
            adjustments.Select(a => (a.EmployeeId, a.Type)));
        Scalars.AssertEqual(saved, adjustments[2]);
        Assert.Empty(await repository.GetForEmployeesPeriodAsync([], PeriodStart, PeriodEnd));
    }

    [Fact]
    public async Task UndertimeWaivers_MatchTheExactPeriod()
    {
        Db.PayrollUndertimeWaivers.AddRange(
            new PayrollUndertimeWaiver { EmployeeId = 1001, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd },
            new PayrollUndertimeWaiver { EmployeeId = 1002, PeriodStart = new DateOnly(2026, 3, 16), PeriodEnd = new DateOnly(2026, 3, 31) },
            new PayrollUndertimeWaiver { EmployeeId = 1003, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd });
        await Db.SaveChangesAsync();
        var repository = new PayrollUndertimeWaiverRepository(Db);

        Assert.True(await repository.IsWaivedAsync(1001, PeriodStart, PeriodEnd));
        Assert.False(await repository.IsWaivedAsync(1002, PeriodStart, PeriodEnd));
        Assert.Equal([1001], await repository.GetWaivedPinsAsync([1001, 1002], PeriodStart, PeriodEnd));
        Assert.Empty(await repository.GetWaivedPinsAsync([], PeriodStart, PeriodEnd));
    }
}
