using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Data.Tests.Fixtures;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// Covers the batched ScheduleRepository.ImportEmployeeRosterAsync rewrite -- matching is
/// global by Pin here, deliberately unlike ImportAsync's department-scoped two-tier match
/// (see IScheduleRepository's own doc comment on that asymmetry), so these tests are
/// smaller and don't need the name-fallback/collision coverage
/// ScheduleRepositoryImportAsyncTests has.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ScheduleRepositoryImportEmployeeRosterAsyncTests(DatabaseFixture fixture) : PersistenceTestBase(fixture)
{
    [Fact]
    public async Task NewEmployee_IsCreatedInTheNamedDepartment()
    {
        var repository = new ScheduleRepository(Db);

        var row = new EmployeeImportRow
        {
            Pin = 1001,
            LastName = "Cruz",
            FirstName = "Juan",
            DepartmentName = "Kitchen",
            DailyRate = 500m,
        };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = NewContext();
        var employee = await readDb.Employees.Include(e => e.Department).SingleAsync();
        Assert.Equal("Kitchen", employee.Department!.Name);
        Assert.Equal(500m, employee.DailyRate);
    }

    [Fact]
    public async Task ExistingEmployee_MatchedGloballyByPin_IgnoresDepartmentScoping()
    {
        var repository = new ScheduleRepository(Db);

        var deptA = new Department { Name = "Kitchen" };
        var deptB = new Department { Name = "Housekeeping" };
        Db.Departments.AddRange(deptA, deptB);
        Db.Employees.Add(new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = deptA });
        await Db.SaveChangesAsync();

        // Unlike ImportAsync, a roster row carries no per-department scoping at all --
        // this must find the Pin 1001 row and move it into Housekeeping, not create a
        // second employee.
        var row = new EmployeeImportRow { Pin = 1001, LastName = "Cruz", FirstName = "Juan", DepartmentName = "Housekeeping" };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = NewContext();
        Assert.Equal(1, await readDb.Employees.CountAsync());
        var employee = await readDb.Employees.Include(e => e.Department).SingleAsync();
        Assert.Equal("Housekeeping", employee.Department!.Name);
    }

    [Fact]
    public async Task BlankDepartmentCell_OnAnExistingEmployee_LeavesTheirDepartmentUnchanged()
    {
        var repository = new ScheduleRepository(Db);

        var dept = new Department { Name = "Kitchen" };
        Db.Departments.Add(dept);
        Db.Employees.Add(new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = dept });
        await Db.SaveChangesAsync();

        var row = new EmployeeImportRow { Pin = 1001, LastName = "Cruz", FirstName = "Juan", DepartmentName = null };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = NewContext();
        var employee = await readDb.Employees.Include(e => e.Department).SingleAsync();
        Assert.Equal("Kitchen", employee.Department!.Name);
    }

    [Fact]
    public async Task OptionalFieldsLeftNull_OnANewEmployee_KeepClassDefaults()
    {
        var repository = new ScheduleRepository(Db);

        var row = new EmployeeImportRow { Pin = 1001, LastName = "Cruz", FirstName = "Juan" };

        await repository.ImportEmployeeRosterAsync([row]);

        using var readDb = NewContext();
        var employee = await readDb.Employees.SingleAsync();
        Assert.True(employee.QualifiesForOvertime); // class field-initializer default.
        Assert.Equal(0m, employee.DailyRate);
        Assert.Null(employee.DepartmentId);
    }

    [Fact]
    public async Task EmptyInput_IsANoOp()
    {
        var repository = new ScheduleRepository(Db);

        await repository.ImportEmployeeRosterAsync([]);

        using var readDb = NewContext();
        Assert.Equal(0, await readDb.Employees.CountAsync());
    }
}
