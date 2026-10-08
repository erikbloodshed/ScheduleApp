using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Data.Tests.Fixtures;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// Covers PayrollRunRepository.AddEmployeesAsync -- the bulk counterpart to
/// AddEmployeeAsync that PayrollGroupViewModel.AddEmployeesToGroupAsync's own foreach
/// loop used to be (see that method's own doc comment on why one call replaced it).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PayrollRunRepositoryAddEmployeesAsyncTests(DatabaseFixture fixture) : PersistenceTestBase(fixture)
{
    private static async Task<int> CreateRunAsync(ScheduleDbContext db)
    {
        var repository = new PayrollRunRepository(db);
        var run = await repository.CreateAsync(new PayrollRun
        {
            Label = "Test Run",
            PeriodStart = new DateOnly(2026, 1, 1),
            PeriodEnd = new DateOnly(2026, 1, 15),
            CreatedBy = "tester",
        });
        return run.Id;
    }

    [Fact]
    public async Task InsertsEveryRequestedEmployee()
    {
        var runId = await CreateRunAsync(Db);
        var repository = new PayrollRunRepository(Db);

        var inserted = await repository.AddEmployeesAsync(runId, [1001, 1002, 1003]);

        Assert.Equal(3, inserted);
        using var readDb = NewContext();
        Assert.Equal(3, await readDb.PayrollRunEmployees.CountAsync(e => e.PayrollRunId == runId));
    }

    [Fact]
    public async Task SkipsPinsAlreadyInTheRun_InsertsOnlyTheNewOnes()
    {
        var runId = await CreateRunAsync(Db);
        var repository = new PayrollRunRepository(Db);

        await repository.AddEmployeeAsync(runId, 1001);

        var inserted = await repository.AddEmployeesAsync(runId, [1001, 1002]);

        Assert.Equal(1, inserted); // only 1002 was new.
        using var readDb = NewContext();
        Assert.Equal(2, await readDb.PayrollRunEmployees.CountAsync(e => e.PayrollRunId == runId));
    }

    [Fact]
    public async Task DuplicatePinsInTheRequestedSet_InsertOnlyOnce()
    {
        var runId = await CreateRunAsync(Db);
        var repository = new PayrollRunRepository(Db);

        var inserted = await repository.AddEmployeesAsync(runId, [1001, 1001, 1001]);

        Assert.Equal(1, inserted);
        using var readDb = NewContext();
        Assert.Equal(1, await readDb.PayrollRunEmployees.CountAsync(e => e.PayrollRunId == runId));
    }

    [Fact]
    public async Task EveryRequestedPinAlreadyAMember_ReturnsZero_WritesNothing()
    {
        var runId = await CreateRunAsync(Db);
        var repository = new PayrollRunRepository(Db);
        await repository.AddEmployeeAsync(runId, 1001);

        var inserted = await repository.AddEmployeesAsync(runId, [1001]);

        Assert.Equal(0, inserted);
        using var readDb = NewContext();
        Assert.Equal(1, await readDb.PayrollRunEmployees.CountAsync(e => e.PayrollRunId == runId));
    }

    [Fact]
    public async Task UnknownRunId_ReturnsZero_WritesNothing()
    {
        var repository = new PayrollRunRepository(Db);

        var inserted = await repository.AddEmployeesAsync(runId: 999, [1001, 1002]);

        Assert.Equal(0, inserted);
        using var readDb = NewContext();
        Assert.Equal(0, await readDb.PayrollRunEmployees.CountAsync());
    }

    [Fact]
    public async Task EmptyInput_IsANoOp()
    {
        var runId = await CreateRunAsync(Db);
        var repository = new PayrollRunRepository(Db);

        var inserted = await repository.AddEmployeesAsync(runId, []);

        Assert.Equal(0, inserted);
    }
}
