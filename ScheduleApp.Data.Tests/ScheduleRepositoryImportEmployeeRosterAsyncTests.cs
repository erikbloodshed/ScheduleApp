using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// Covers the batched ScheduleRepository.ImportEmployeeRosterAsync rewrite -- matching is
/// global by Pin here, deliberately unlike ImportAsync's department-scoped two-tier match
/// (see IScheduleRepository's own doc comment on that asymmetry), so these tests are
/// smaller and don't need the name-fallback/collision coverage
/// ScheduleRepositoryImportAsyncTests has.
/// </summary>
public class ScheduleRepositoryImportEmployeeRosterAsyncTests
{
    [Fact]
    public async Task NewEmployee_IsCreatedInTheNamedDepartment()
    {
        using var database = TestDbContext.Create();
        var repository = new ScheduleRepository(database.Db);

        var row = new EmployeeImportRow
        {
            Pin = 1001,
            LastName = "Cruz",
            FirstName = "Juan",
            DepartmentName = "Kitchen",
            DailyRate = 500m,
        };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = database.NewContext();
        var employee = await readDb.Employees.Include(e => e.Department).SingleAsync();
        Assert.Equal("Kitchen", employee.Department!.Name);
        Assert.Equal(500m, employee.DailyRate);
    }

    [Fact]
    public async Task ExistingEmployee_MatchedGloballyByPin_IgnoresDepartmentScoping()
    {
        using var database = TestDbContext.Create();
        var repository = new ScheduleRepository(database.Db);

        var deptA = new Department { Name = "Kitchen" };
        var deptB = new Department { Name = "Housekeeping" };
        database.Db.Departments.AddRange(deptA, deptB);
        database.Db.Employees.Add(new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = deptA });
        await database.Db.SaveChangesAsync();

        // Unlike ImportAsync, a roster row carries no per-department scoping at all --
        // this must find the Pin 1001 row and move it into Housekeeping, not create a
        // second employee.
        var row = new EmployeeImportRow { Pin = 1001, LastName = "Cruz", FirstName = "Juan", DepartmentName = "Housekeeping" };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = database.NewContext();
        Assert.Equal(1, await readDb.Employees.CountAsync());
        var employee = await readDb.Employees.Include(e => e.Department).SingleAsync();
        Assert.Equal("Housekeeping", employee.Department!.Name);
    }

    [Fact]
    public async Task BlankDepartmentCell_OnAnExistingEmployee_LeavesTheirDepartmentUnchanged()
    {
        using var database = TestDbContext.Create();
        var repository = new ScheduleRepository(database.Db);

        var dept = new Department { Name = "Kitchen" };
        database.Db.Departments.Add(dept);
        database.Db.Employees.Add(new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = dept });
        await database.Db.SaveChangesAsync();

        var row = new EmployeeImportRow { Pin = 1001, LastName = "Cruz", FirstName = "Juan", DepartmentName = null };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = database.NewContext();
        var employee = await readDb.Employees.Include(e => e.Department).SingleAsync();
        Assert.Equal("Kitchen", employee.Department!.Name);
    }

    [Fact]
    public async Task OptionalFieldsLeftNull_OnANewEmployee_KeepClassDefaults()
    {
        using var database = TestDbContext.Create();
        var repository = new ScheduleRepository(database.Db);

        var row = new EmployeeImportRow { Pin = 1001, LastName = "Cruz", FirstName = "Juan" };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = database.NewContext();
        var employee = await readDb.Employees.SingleAsync();
        Assert.True(employee.QualifiesForOvertime); // class field-initializer default.
        Assert.Equal(0m, employee.DailyRate);
        Assert.Null(employee.DepartmentId);
    }

    [Fact]
    public async Task EmptyInput_IsANoOp()
    {
        using var database = TestDbContext.Create();
        var repository = new ScheduleRepository(database.Db);

        await repository.ImportEmployeeRosterAsync([]);

        using var readDb = database.NewContext();
        Assert.Equal(0, await readDb.Employees.CountAsync());
    }
}
