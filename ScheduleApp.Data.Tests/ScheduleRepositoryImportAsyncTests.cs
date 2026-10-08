using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Data.Tests.Fixtures;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// Covers the batched ScheduleRepository.ImportAsync rewrite against a real SQL Server
/// database (see Fixtures/DatabaseFixture.cs) -- specifically the traps the batching itself introduced risk for, not a
/// full re-test of every ScheduleEntry field ImportAsync happens to carry through
/// unchanged from the workbook (UpsertScheduleEntry, which does that mapping, is
/// unchanged by this refactor and untested here for the same reason
/// SetScheduleForDatesAsync -- its other caller -- isn't).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ScheduleRepositoryImportAsyncTests(DatabaseFixture fixture) : PersistenceTestBase(fixture)
{
    private static Department BuildDepartment(string name, params Employee[] employees)
    {
        var department = new Department { Name = name };
        foreach (var employee in employees)
            department.Employees.Add(employee);
        return department;
    }

    private static Employee BuildEmployee(int pin, string lastName, string firstName, params ScheduleEntry[] entries)
    {
        var employee = new Employee { Pin = pin, LastName = lastName, FirstName = firstName };
        foreach (var entry in entries)
            employee.ScheduleEntries.Add(entry);
        return employee;
    }

    // Plain new TimeOnly(hour, minute) rather than TimeOnly.Parse -- avoids CA1305
    // (locale-sensitive parsing) entirely instead of threading CultureInfo.InvariantCulture
    // through every call site.
    private static TimeOnly Time(int hour, int minute = 0) => new(hour, minute);

    private static ScheduleEntry NormalDay(DateOnly date, decimal hours = 8m, TimeOnly? timeIn = null) =>
        new()
        {
            Date = date,
            ScheduleType = ScheduleType.Normal,
            WorkTimeHours = hours,
            TimeIn = timeIn ?? Time(8),
        };

    private static ScheduleEntry SplitShiftDay(DateOnly date, params (TimeOnly In, TimeOnly Out)[] segments)
    {
        var entry = new ScheduleEntry { Date = date, ScheduleType = ScheduleType.SplitShift, WorkTimeHours = 8m };
        foreach (var (timeIn, timeOut) in segments)
            entry.FlexibleSegments.Add(new FlexibleSegment { TimeIn = timeIn, TimeOut = timeOut });
        return entry;
    }

    [Fact]
    public async Task NewDepartmentAndEmployee_LandInOneSave()
    {
        var repository = new ScheduleRepository(Db);
        var date = new DateOnly(2026, 1, 5);

        // Proves the alternate-key FK insert ordering (ScheduleEntry.EmployeeId ->
        // Employee.Pin) and the Department navigation both work in one SaveChangesAsync:
        // department, employee, and entry are all brand new here.
        var department = BuildDepartment("Kitchen", BuildEmployee(1001, "Cruz", "Juan", NormalDay(date)));

        await repository.ImportAsync([department]);

        using var readDb = NewContext();
        var savedDept = await readDb.Departments.SingleAsync(d => d.Name == "Kitchen");
        var savedEmployee = await readDb.Employees.SingleAsync(e => e.Pin == 1001);
        var savedEntry = await readDb.ScheduleEntries.SingleAsync(s => s.EmployeeId == 1001);

        Assert.Equal(savedDept.Id, savedEmployee.DepartmentId);
        Assert.Equal(date, savedEntry.Date);
        Assert.Equal(ScheduleType.Normal, savedEntry.ScheduleType);
    }

    [Fact]
    public async Task ExistingEmployee_MatchedByPinInSameDepartment_IsReused()
    {
        var repository = new ScheduleRepository(Db);

        var seedDept = new Department { Name = "Kitchen" };
        var seedEmployee = new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = seedDept };
        Db.Departments.Add(seedDept);
        Db.Employees.Add(seedEmployee);
        await Db.SaveChangesAsync();

        var date = new DateOnly(2026, 1, 5);
        var department = BuildDepartment("Kitchen", BuildEmployee(1001, "Cruz", "Juan", NormalDay(date)));

        await repository.ImportAsync([department]);

        using var readDb = NewContext();
        Assert.Equal(1, await readDb.Employees.CountAsync(e => e.Pin == 1001));
        Assert.Equal(1, await readDb.ScheduleEntries.CountAsync(s => s.EmployeeId == 1001));
    }

    [Fact]
    public async Task ExistingEmployee_MatchedByNameAfterPinChange_KeepsWritingUnderTheExistingPin()
    {
        var repository = new ScheduleRepository(Db);

        var seedDept = new Department { Name = "Kitchen" };
        var seedEmployee = new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = seedDept };
        Db.Departments.Add(seedDept);
        Db.Employees.Add(seedEmployee);
        await Db.SaveChangesAsync();

        // The workbook now shows a different Pin for the same person -- tier 1 (Pin)
        // misses (5001 doesn't exist anywhere yet), tier 2 (name, within this
        // department) matches the existing Cruz/Juan row. Original behavior (see
        // ScheduleRepository.ImportAsync's own doc comment): entries land under the
        // MATCHED employee's own Pin (1001), not the workbook's (5001).
        var date = new DateOnly(2026, 1, 5);
        var department = BuildDepartment("Kitchen", BuildEmployee(5001, "Cruz", "Juan", NormalDay(date)));

        await repository.ImportAsync([department]);

        using var readDb = NewContext();
        Assert.Equal(1, await readDb.Employees.CountAsync());
        var employee = await readDb.Employees.SingleAsync();
        Assert.Equal(1001, employee.Pin); // Pin is NOT overwritten by the workbook's value.
        Assert.Equal(1001, (await readDb.ScheduleEntries.SingleAsync()).EmployeeId);
    }

    [Fact]
    public async Task NameFallback_TwoExistingCandidatesWithSameName_PicksLowestIdDeterministically()
    {
        var repository = new ScheduleRepository(Db);

        var seedDept = new Department { Name = "Kitchen" };
        Db.Departments.Add(seedDept);
        var first = new Employee { Pin = 100, LastName = "Cruz", FirstName = "Juan", Department = seedDept };
        var second = new Employee { Pin = 200, LastName = "Cruz", FirstName = "Juan", Department = seedDept };
        Db.Employees.AddRange(first, second);
        await Db.SaveChangesAsync();

        var lowerId = first.Id < second.Id ? first : second;

        var date = new DateOnly(2026, 1, 5);
        // A third, brand-new Pin under the same name -- tier 1 misses for both existing
        // rows, tier 2 finds both by name; the match must be deterministic (lowest Id),
        // not whichever an unordered query happened to return first.
        var department = BuildDepartment("Kitchen", BuildEmployee(999, "Cruz", "Juan", NormalDay(date)));

        await repository.ImportAsync([department]);

        using var readDb = NewContext();
        Assert.Equal(2, await readDb.Employees.CountAsync()); // no third employee created
        var entry = await readDb.ScheduleEntries.SingleAsync();
        Assert.Equal(lowerId.Pin, entry.EmployeeId);
    }

    [Fact]
    public async Task LaterRowForSameDate_OverwritesTheEarlierOne()
    {
        var repository = new ScheduleRepository(Db);
        var date = new DateOnly(2026, 1, 5);

        var employee = new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan" };
        employee.ScheduleEntries.Add(NormalDay(date, hours: 8m, timeIn: Time(8)));
        employee.ScheduleEntries.Add(NormalDay(date, hours: 10m, timeIn: Time(6))); // same date, later in the list

        await repository.ImportAsync([BuildDepartment("Kitchen", employee)]);

        using var readDb = NewContext();
        var entry = await readDb.ScheduleEntries.SingleAsync(s => s.EmployeeId == 1001 && s.Date == date);
        Assert.Equal(10m, entry.WorkTimeHours);
        Assert.Equal(Time(6), entry.TimeIn);
    }

    [Fact]
    public async Task SamePin_OnTwoSheetsResolvingToTheSameDepartment_DoesNotThrowAndDedupsEntries()
    {
        var repository = new ScheduleRepository(Db);
        var date = new DateOnly(2026, 1, 5);

        // Two sheets differing only by case, both brand new -- ResolveDepartmentsAsync's
        // own OrdinalIgnoreCase dedup must collapse them into ONE department before
        // either employee/entry is staged, or this throws a DuplicateEmployeeIdException
        // (the employee looks like it belongs to two different departments) or fails the
        // (EmployeeId, Date) unique index (two competing inserts for the same day).
        var sheet1 = BuildDepartment("Kitchen", BuildEmployee(1001, "Cruz", "Juan", NormalDay(date, hours: 8m)));
        var sheet2 = BuildDepartment("KITCHEN", BuildEmployee(1001, "Cruz", "Juan", NormalDay(date, hours: 10m)));

        await repository.ImportAsync([sheet1, sheet2]);

        using var readDb = NewContext();
        Assert.Equal(1, await readDb.Departments.CountAsync());
        Assert.Equal(1, await readDb.Employees.CountAsync());
        var entry = await readDb.ScheduleEntries.SingleAsync();
        Assert.Equal(10m, entry.WorkTimeHours); // sheet2 (processed later) wins.
    }

    [Fact]
    public async Task Pin_BelongingToEmployeeInADifferentDepartment_ThrowsAndCommitsNothing()
    {
        var repository = new ScheduleRepository(Db);

        var otherDept = new Department { Name = "Accounting" };
        Db.Departments.Add(otherDept);
        Db.Employees.Add(new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = otherDept });
        await Db.SaveChangesAsync();

        var date = new DateOnly(2026, 1, 5);
        // A brand-new department that happens to also introduce Pin 1001 -- both
        // matching tiers are department-scoped, so neither reaches the Accounting row;
        // this must fail cleanly instead of hitting Employee.Pin's own unique index.
        var newDept = BuildDepartment("Kitchen", BuildEmployee(1001, "Reyes", "Ana", NormalDay(date)));

        var ex = await Assert.ThrowsAsync<DuplicateEmployeeIdException>(() => repository.ImportAsync([newDept]));
        Assert.Equal(1001, ex.EmployeeId);

        using var readDb = NewContext();
        Assert.Equal(1, await readDb.Departments.CountAsync()); // "Kitchen" was never committed.
        Assert.Equal(1, await readDb.Employees.CountAsync()); // no second employee for Pin 1001.
        Assert.Equal(0, await readDb.ScheduleEntries.CountAsync());
    }

    [Fact]
    public async Task FailedImport_LeavesTheContextUsableForALaterUnrelatedSave()
    {
        var repository = new ScheduleRepository(Db);

        var otherDept = new Department { Name = "Accounting" };
        Db.Departments.Add(otherDept);
        Db.Employees.Add(new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = otherDept });
        await Db.SaveChangesAsync();

        var date = new DateOnly(2026, 1, 5);
        var newDept = BuildDepartment("Kitchen", BuildEmployee(1001, "Reyes", "Ana", NormalDay(date)));

        await Assert.ThrowsAsync<DuplicateEmployeeIdException>(() => repository.ImportAsync([newDept]));

        // Same db/context instance as the failed call above -- if DetachImportEntities
        // didn't clean up the still-Added Department/Employee/ScheduleEntry from that
        // failed import, this unrelated write would fail too (or re-insert the same
        // broken rows), the same way the app's own single, session-lifetime
        // ScheduleDbContext would misbehave for the rest of the run.
        await repository.AddDepartmentAsync("Housekeeping");

        using var readDb = NewContext();
        Assert.Equal(2, await readDb.Departments.CountAsync()); // Accounting + Housekeeping only.
        Assert.Equal(1, await readDb.Employees.CountAsync());
    }

    [Fact]
    public async Task EntriesOutsideTheWorkbookDateRange_AreLeftUntouched()
    {
        var repository = new ScheduleRepository(Db);

        var untouchedDate = new DateOnly(2020, 6, 1);
        var seedDept = new Department { Name = "Kitchen" };
        var seedEmployee = new Employee { Pin = 1001, LastName = "Cruz", FirstName = "Juan", Department = seedDept };
        seedEmployee.ScheduleEntries.Add(NormalDay(untouchedDate, hours: 9m));
        Db.Departments.Add(seedDept);
        Db.Employees.Add(seedEmployee);
        await Db.SaveChangesAsync();

        // This import's own date range is nowhere near untouchedDate -- the entry
        // preload is scoped to [min, max] of the incoming entries, so the 2020 row
        // should never even be loaded, let alone altered.
        var importedDate = new DateOnly(2026, 1, 5);
        var department = BuildDepartment("Kitchen", BuildEmployee(1001, "Cruz", "Juan", NormalDay(importedDate)));

        await repository.ImportAsync([department]);

        using var readDb = NewContext();
        var untouchedEntry = await readDb.ScheduleEntries.SingleAsync(s => s.Date == untouchedDate);
        Assert.Equal(9m, untouchedEntry.WorkTimeHours);
        Assert.Equal(2, await readDb.ScheduleEntries.CountAsync());
    }

    [Fact]
    public async Task ReplacingSplitShiftSegments_DeletesTheOldRows()
    {
        var repository = new ScheduleRepository(Db);
        var date = new DateOnly(2026, 1, 5);

        var department1 = BuildDepartment("Kitchen", BuildEmployee(1001, "Cruz", "Juan",
            SplitShiftDay(date, (Time(5), Time(9)), (Time(13), Time(17)))));
        await repository.ImportAsync([department1]);

        using (var readDb1 = NewContext())
            Assert.Equal(2, await readDb1.FlexibleSegments.CountAsync());

        // Re-import the same employee/date with a single, different segment -- the old
        // two rows must be deleted (UpsertScheduleEntry's FlexibleSegments.Clear() on a
        // tracked, Included collection), not left as orphans alongside the new one.
        var department2 = BuildDepartment("Kitchen", BuildEmployee(1001, "Cruz", "Juan",
            SplitShiftDay(date, (Time(6), Time(14)))));
        await repository.ImportAsync([department2]);

        using var readDb2 = NewContext();
        Assert.Equal(1, await readDb2.FlexibleSegments.CountAsync());
        var segment = await readDb2.FlexibleSegments.SingleAsync();
        Assert.Equal(Time(6), segment.TimeIn);
    }

    [Fact]
    public async Task EmptyInput_IsANoOp()
    {
        var repository = new ScheduleRepository(Db);

        await repository.ImportAsync([]);

        using var readDb = NewContext();
        Assert.Equal(0, await readDb.Departments.CountAsync());
    }

    [Fact]
    public async Task ExistingDepartment_DifferingOnlyByCase_IsReused()
    {
        // SQL Server's default collation is case-insensitive, so the existing-name query in
        // ResolveDepartmentsAsync finds "Kitchen" for a sheet named "kitchen" -- and the
        // OrdinalIgnoreCase dictionary it builds must then match it, or the import creates a
        // second department and fails Department.Name's unique index.
        var repository = new ScheduleRepository(Db);
        Db.Departments.Add(new Department { Name = "Kitchen" });
        await Db.SaveChangesAsync();

        await repository.ImportAsync([BuildDepartment("kitchen", BuildEmployee(1001, "Cruz", "Juan", NormalDay(new DateOnly(2026, 1, 5))))]);

        using var readDb = NewContext();
        var department = await readDb.Departments.SingleAsync();
        Assert.Equal("Kitchen", department.Name);
        Assert.Equal(department.Id, (await readDb.Employees.SingleAsync()).DepartmentId);
    }
}
