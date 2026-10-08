using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Data.Tests.Fixtures;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// ScheduleRepository's reads, which go through Dapper (see ScheduleApp.Data/Queries): the
/// graphs come back with the same navigations, filters and order the EF queries they replaced
/// had, and every column of the wide Employee/ScheduleEntry rows maps back to its property.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ScheduleRepositoryReadTests(DatabaseFixture fixture) : PersistenceTestBase(fixture)
{
    private static readonly DateOnly Day1 = new(2026, 3, 2);
    private static readonly DateOnly Day2 = new(2026, 3, 3);
    private static readonly DateOnly Day3 = new(2026, 3, 4);

    private ScheduleRepository Repository => new(Db);

    private static Employee NewEmployee(int pin, string lastName, Department? department = null, bool blacklisted = false) =>
        new() { Pin = pin, LastName = lastName, FirstName = "Test", Department = department, IsBlacklisted = blacklisted };

    private static ScheduleEntry Normal(int pin, DateOnly date) =>
        new() { EmployeeId = pin, Date = date, ScheduleType = ScheduleType.Normal, WorkTimeHours = 8m, TimeIn = new TimeOnly(8, 0) };

    private static ScheduleEntry Split(int pin, DateOnly date) => new()
    {
        EmployeeId = pin,
        Date = date,
        ScheduleType = ScheduleType.SplitShift,
        WorkTimeHours = 8m,
        FlexibleSegments =
        [
            new FlexibleSegment { TimeIn = new TimeOnly(6, 0), TimeOut = new TimeOnly(10, 0), ClockInBufferHours = 0.5 },
            new FlexibleSegment { TimeIn = new TimeOnly(22, 0), TimeOut = new TimeOnly(2, 0), ClockOutBufferHours = 1.5 },
        ],
    };

    /// <summary>Kitchen (SortOrder 2) with Cruz and blacklisted Bautista, Bar (SortOrder 1)
    /// with Reyes, and unassigned Santos and blacklisted Aquino.</summary>
    private async Task SeedRosterAsync()
    {
        var kitchen = new Department { Name = "Kitchen", SortOrder = 2 };
        var bar = new Department { Name = "Bar", SortOrder = 1 };
        Db.Departments.AddRange(kitchen, bar, new Department { Name = "Empty", SortOrder = 3 });
        Db.Employees.AddRange(
            NewEmployee(1001, "Cruz", kitchen),
            NewEmployee(1002, "Bautista", kitchen, blacklisted: true),
            NewEmployee(1003, "Reyes", bar),
            NewEmployee(1004, "Santos"),
            NewEmployee(1005, "Aquino", blacklisted: true));
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task DepartmentsWithEmployees_ComeBySortOrder_WithEveryEmployee()
    {
        await SeedRosterAsync();

        var departments = await Repository.GetDepartmentsWithEmployeesAsync();

        Assert.Equal(["Bar", "Kitchen", "Empty"], departments.Select(d => d.Name));
        Assert.Equal([1001, 1002], departments[1].Employees.Select(e => e.Pin));
        Assert.Empty(departments[2].Employees);
        Assert.All(departments[1].Employees, e => Assert.Same(departments[1], e.Department));
    }

    [Fact]
    public async Task ActiveDepartmentsWithEmployees_LeaveOutBlacklisted()
    {
        await SeedRosterAsync();

        var departments = await Repository.GetActiveDepartmentsWithEmployeesAsync();

        Assert.Equal([1001], departments.Single(d => d.Name == "Kitchen").Employees.Select(e => e.Pin));
    }

    [Fact]
    public async Task UnassignedEmployees_ComeByLastName_AndActiveLeavesOutBlacklisted()
    {
        await SeedRosterAsync();

        Assert.Equal(["Aquino", "Santos"], (await Repository.GetUnassignedEmployeesAsync()).Select(e => e.LastName));
        Assert.Equal(["Santos"], (await Repository.GetActiveUnassignedEmployeesAsync()).Select(e => e.LastName));
    }

    [Fact]
    public async Task Employee_ReadsBackEveryColumn()
    {
        var department = new Department { Name = "Kitchen" };
        var saved = new Employee
        {
            Pin = 2001,
            LastName = "Cruz",
            FirstName = "Juan",
            Department = department,
            IsBlacklisted = true,
            QualifiesForOvertime = false,
            QualifiesForNightDiff = false,
            ApplyOvertimeRatePercentageByDefault = false,
            EmployeeType = EmployeeType.Monthly,
            DailyRate = 610.50m,
            MonthlyRate = 18000.25m,
            ClockInBufferBeforeHours = 1.5,
            ClockInBufferAfterHours = 2.25,
            ClockOutBufferBeforeHours = 3.5,
            ClockOutBufferAfterHours = 4.75,
            DefaultWorkTimeHours = 9.5m,
            ExemptFromUndertimeDeduction = true,
            RestDayWorkPremiumPercentage = 0.3m,
            QualifiesForRestDayPay = true,
            DefaultLeaveIsPaid = true,
            DefaultSss = 450m,
            DefaultPhilHealth = 250.75m,
            DefaultPagIbig = 100m,
            HolidayPremiumPercentage = 0.125m,
            QualifiesForPremiumPay = true,
            DefaultPremiumPay = 300m,
            DefaultAllowance = 150.5m,
            DefaultCashAdvance = 1000m,
        };
        Db.Employees.Add(saved);
        await Db.SaveChangesAsync();

        var read = (await Repository.GetDepartmentsWithEmployeesAsync()).Single().Employees.Single();

        Scalars.AssertEqual(saved, read);
    }

    [Fact]
    public async Task DepartmentsForExport_HaveOnlyThePickedEmployees_AndTheirEntriesInRange()
    {
        await SeedRosterAsync();
        Db.ScheduleEntries.AddRange(Normal(1001, Day1), Split(1001, Day2), Normal(1001, Day3), Normal(1003, Day1));
        await Db.SaveChangesAsync();
        var cruzId = Db.Employees.Single(e => e.Pin == 1001).Id;

        var departments = await Repository.GetDepartmentsForExportAsync([cruzId], Day1, Day2);

        var kitchen = Assert.Single(departments);
        Assert.Equal("Kitchen", kitchen.Name);
        var cruz = Assert.Single(kitchen.Employees);
        Assert.Equal([Day1, Day2], cruz.ScheduleEntries.Select(s => s.Date).Order());
        var split = cruz.ScheduleEntries.Single(s => s.ScheduleType == ScheduleType.SplitShift);
        Assert.Equal([new TimeOnly(6, 0), new TimeOnly(22, 0)], split.FlexibleSegments.Select(f => f.TimeIn));
        Assert.Equal([0.5, null], split.FlexibleSegments.Select(f => f.ClockInBufferHours));
        Assert.Equal([null, 1.5], split.FlexibleSegments.Select(f => f.ClockOutBufferHours));
        Assert.Same(cruz, split.Employee);
    }

    [Fact]
    public async Task DepartmentsForExport_WithNoPicks_IsEmpty()
    {
        await SeedRosterAsync();

        Assert.Empty(await Repository.GetDepartmentsForExportAsync([], Day1, Day3));
        Assert.Empty(await Repository.GetUnassignedForExportAsync([], Day1, Day3));
    }

    [Fact]
    public async Task UnassignedForExport_HasOnlyPickedUnassignedEmployees()
    {
        await SeedRosterAsync();
        Db.ScheduleEntries.Add(Normal(1004, Day1));
        await Db.SaveChangesAsync();
        var ids = Db.Employees.Where(e => e.Pin == 1004 || e.Pin == 1001).Select(e => e.Id).ToList();

        var santos = Assert.Single(await Repository.GetUnassignedForExportAsync(ids, Day1, Day3));

        Assert.Equal(1004, santos.Pin);
        Assert.Equal(Day1, Assert.Single(santos.ScheduleEntries).Date);
    }

    [Fact]
    public async Task ScheduleEntriesForEmployee_ComeByDate_WithSegments()
    {
        await SeedRosterAsync();
        Db.ScheduleEntries.AddRange(Normal(1001, Day3), Split(1001, Day1), Normal(1003, Day2));
        await Db.SaveChangesAsync();

        var entries = await Repository.GetScheduleEntriesForEmployeeAsync(1001);

        Assert.Equal([Day1, Day3], entries.Select(s => s.Date));
        Assert.Equal(2, entries[0].FlexibleSegments.Count);
        Assert.Empty(entries[1].FlexibleSegments);
    }

    [Fact]
    public async Task ScheduleEntriesForPeriod_CarryEmployeeAndDepartment_ByEmployeeThenDate()
    {
        await SeedRosterAsync();
        Db.ScheduleEntries.AddRange(Normal(1004, Day2), Normal(1001, Day2), Split(1001, Day1), Normal(1003, Day3));
        await Db.SaveChangesAsync();

        var entries = await Repository.GetScheduleEntriesForPeriodAsync(Day1, Day2);

        Assert.Equal([(1001, Day1), (1001, Day2), (1004, Day2)], entries.Select(s => (s.EmployeeId, s.Date)));
        Assert.Equal("Cruz", entries[0].Employee!.LastName);
        Assert.Equal("Kitchen", entries[0].Employee!.Department!.Name);
        Assert.Null(entries[2].Employee!.Department);
        Assert.Equal(2, entries[0].FlexibleSegments.Count);
    }

    [Fact]
    public async Task ScheduleEntriesForPeriod_FilterByPins_WhenGiven()
    {
        await SeedRosterAsync();
        Db.ScheduleEntries.AddRange(Normal(1001, Day1), Normal(1003, Day1));
        await Db.SaveChangesAsync();

        Assert.Equal([1003], (await Repository.GetScheduleEntriesForPeriodAsync(Day1, Day1, [1003])).Select(s => s.EmployeeId));
        Assert.Empty(await Repository.GetScheduleEntriesForPeriodAsync(Day1, Day1, []));
    }

    [Fact]
    public async Task ScheduleEntry_ReadsBackEveryColumn()
    {
        await SeedRosterAsync();
        var saved = new ScheduleEntry
        {
            EmployeeId = 1001,
            Date = Day1,
            ScheduleType = ScheduleType.Flexible,
            WorkTimeHours = 7.5m,
            IsPaidLeave = false,
            RestrictedTimeIn = new TimeOnly(7, 30),
            RestrictedTimeOut = new TimeOnly(19, 45),
            ClockInBufferBeforeHours = 1.25,
            ClockInBufferAfterHours = 2.5,
            ClockOutBufferBeforeHours = 3.75,
            ClockOutBufferAfterHours = 4,
            OvertimeEligibleOverride = false,
            NightDiffEligibleOverride = true,
            ApplyOvertimeRatePercentageOverride = false,
            OvertimeRatePercentageOverride = 0.25m,
            NightDiffRatePercentageOverride = 0.1m,
        };
        Db.ScheduleEntries.Add(saved);
        await Db.SaveChangesAsync();

        var read = Assert.Single(await Repository.GetScheduleEntriesForEmployeeAsync(1001));

        Scalars.AssertEqual(saved, read);
    }

    [Fact]
    public async Task EmployeesByPins_ComeByLastName_WithDepartment()
    {
        await SeedRosterAsync();

        var employees = await Repository.GetEmployeesByPinsAsync([1003, 1004, 1001, 9999]);

        Assert.Equal(["Cruz", "Reyes", "Santos"], employees.Select(e => e.LastName));
        Assert.Equal("Bar", employees[1].Department!.Name);
        Assert.Null(employees[2].Department);
    }
}
