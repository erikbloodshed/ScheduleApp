using Dapper;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Data.Queries;

/// <summary>
/// The department / employee / schedule-entry graphs ScheduleRepository's reads hand back. A
/// collection navigation is a second query stitched on by id rather than a join, which would
/// repeat the parent's columns once per child. Children come back in Id order, the order EF's
/// Include returned them in; each child also gets its parent navigation set, as EF's fix-up did.
/// </summary>
internal static class ScheduleReads
{
    private static readonly string EmployeeColumns = Columns.Of<Employee>("e");

    /// <summary>
    /// Every department, by SortOrder then Name, each with its employees -- only the
    /// non-blacklisted ones when <paramref name="activeOnly"/>.
    /// </summary>
    public static Task<List<Department>> GetDepartmentsWithEmployeesAsync(
        ScheduleDbContext db, bool activeOnly, CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var departments = await db.QueryAsync<Department>(
                $"SELECT {Columns.Of<Department>("d")} FROM Departments d ORDER BY d.SortOrder, d.Name",
                null, cancellationToken);

            var employees = await db.QueryAsync<Employee>(
                $"""
                SELECT {EmployeeColumns} FROM Employees e
                WHERE e.DepartmentId IS NOT NULL{(activeOnly ? " AND e.IsBlacklisted = 0" : "")}
                ORDER BY e.Id
                """,
                null, cancellationToken);

            AttachEmployees(departments, employees);
            return departments;
        }, cancellationToken);

    /// <summary>Employees with no department, by LastName -- only the non-blacklisted ones when <paramref name="activeOnly"/>.</summary>
    public static Task<List<Employee>> GetUnassignedEmployeesAsync(
        ScheduleDbContext db, bool activeOnly, CancellationToken cancellationToken) =>
        db.QueryAsync<Employee>(
            $"""
            SELECT {EmployeeColumns} FROM Employees e
            WHERE e.DepartmentId IS NULL{(activeOnly ? " AND e.IsBlacklisted = 0" : "")}
            ORDER BY e.LastName, e.Id
            """,
            null, cancellationToken);

    /// <summary>
    /// The departments with at least one of <paramref name="employeeIds"/> (Employee.Id), by
    /// SortOrder then Name, each with just those employees, each with their entries dated within
    /// [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>] and the entries' segments.
    /// </summary>
    public static Task<List<Department>> GetDepartmentsForExportAsync(ScheduleDbContext db,
        IReadOnlyCollection<int> employeeIds, DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var ids = DapperReads.IdList(employeeIds);

            var employees = await db.QueryAsync<Employee>(
                $"""
                SELECT {EmployeeColumns} FROM Employees e
                WHERE e.DepartmentId IS NOT NULL AND e.Id IN ({DapperReads.IdsTable})
                ORDER BY e.Id
                """,
                new { ids }, cancellationToken);

            var departments = await db.QueryAsync<Department>(
                $"""
                SELECT {Columns.Of<Department>("d")} FROM Departments d
                WHERE d.Id IN (SELECT e.DepartmentId FROM Employees e WHERE e.Id IN ({DapperReads.IdsTable}))
                ORDER BY d.SortOrder, d.Name
                """,
                new { ids }, cancellationToken);

            AttachEmployees(departments, employees);
            await AttachScheduleEntriesAsync(db, employees, rangeStart, rangeEnd, cancellationToken);
            return departments;
        }, cancellationToken);

    /// <summary>
    /// The unassigned employees among <paramref name="employeeIds"/>, by LastName, with their
    /// entries dated within the range and the entries' segments.
    /// </summary>
    public static Task<List<Employee>> GetUnassignedForExportAsync(ScheduleDbContext db,
        IReadOnlyCollection<int> employeeIds, DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var employees = await db.QueryAsync<Employee>(
                $"""
                SELECT {EmployeeColumns} FROM Employees e
                WHERE e.DepartmentId IS NULL AND e.Id IN ({DapperReads.IdsTable})
                ORDER BY e.LastName, e.Id
                """,
                new { ids = DapperReads.IdList(employeeIds) }, cancellationToken);

            await AttachScheduleEntriesAsync(db, employees, rangeStart, rangeEnd, cancellationToken);
            return employees;
        }, cancellationToken);

    /// <summary>Every entry for one employee (by Pin), by Date, with its segments.</summary>
    public static Task<List<ScheduleEntry>> GetScheduleEntriesForEmployeeAsync(
        ScheduleDbContext db, int employeePin, CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var entries = await db.QueryAsync<ScheduleEntry>(
                $"SELECT {Columns.Of<ScheduleEntry>("s")} FROM ScheduleEntries s WHERE s.EmployeeId = @employeePin ORDER BY s.Date",
                new { employeePin }, cancellationToken);

            await AttachSegmentsAsync(db, entries, cancellationToken);
            return entries;
        }, cancellationToken);

    /// <summary>
    /// Entries dated within the period, for <paramref name="employeePins"/> only when given, by
    /// employee then date: each with its Employee (and that employee's Department) and its segments.
    /// </summary>
    public static Task<List<ScheduleEntry>> GetScheduleEntriesForPeriodAsync(ScheduleDbContext db,
        DateOnly periodStart, DateOnly periodEnd, IReadOnlyCollection<int>? employeePins, CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var pinFilter = employeePins is null ? "" : $" AND s.EmployeeId IN ({DapperReads.IdsTable})";

            var entries = (await db.Connection().QueryAsync<ScheduleEntry, Employee, Department?, ScheduleEntry>(
                db.Command(
                    $"""
                    SELECT {Columns.Of<ScheduleEntry>("s")}, {EmployeeColumns}, {Columns.Of<Department>("d")}
                    FROM ScheduleEntries s
                    JOIN Employees e ON e.Pin = s.EmployeeId
                    LEFT JOIN Departments d ON d.Id = e.DepartmentId
                    WHERE s.Date >= @periodStart AND s.Date <= @periodEnd{pinFilter}
                    ORDER BY s.EmployeeId, s.Date
                    """,
                    new { periodStart, periodEnd, ids = employeePins is null ? null : DapperReads.IdList(employeePins) },
                    cancellationToken),
                (entry, employee, department) =>
                {
                    employee.Department = department;
                    entry.Employee = employee;
                    return entry;
                },
                splitOn: "Id,Id")).AsList();

            await AttachSegmentsAsync(db, entries, cancellationToken);
            return entries;
        }, cancellationToken);

    /// <summary>The employees whose Pin is among <paramref name="pins"/>, by LastName, each with its Department.</summary>
    public static async Task<List<Employee>> GetEmployeesByPinsAsync(
        ScheduleDbContext db, IReadOnlyCollection<int> pins, CancellationToken cancellationToken) =>
        await db.ReadAsync(async () => (await db.Connection().QueryAsync<Employee, Department?, Employee>(
            db.Command(
                $"""
                SELECT {EmployeeColumns}, {Columns.Of<Department>("d")}
                FROM Employees e
                LEFT JOIN Departments d ON d.Id = e.DepartmentId
                WHERE e.Pin IN ({DapperReads.IdsTable})
                ORDER BY e.LastName, e.Id
                """,
                new { ids = DapperReads.IdList(pins) },
                cancellationToken),
            (employee, department) =>
            {
                employee.Department = department;
                return employee;
            },
            splitOn: "Id")).AsList(), cancellationToken);

    private static void AttachEmployees(IEnumerable<Department> departments, IEnumerable<Employee> employees)
    {
        var byDepartment = employees.ToLookup(e => e.DepartmentId);
        foreach (var department in departments)
        {
            department.Employees = [.. byDepartment[department.Id]];
            foreach (var employee in department.Employees)
                employee.Department = department;
        }
    }

    /// <summary>
    /// Each employee's entries dated within the range, with their segments. Entries are keyed by
    /// Pin (ScheduleEntry.EmployeeId's FK targets Employee.Pin -- see ScheduleDbContext).
    /// </summary>
    private static async Task AttachScheduleEntriesAsync(ScheduleDbContext db, List<Employee> employees,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        if (employees.Count == 0) return;

        var entries = await db.QueryAsync<ScheduleEntry>(
            $"""
            SELECT {Columns.Of<ScheduleEntry>("s")} FROM ScheduleEntries s
            WHERE s.EmployeeId IN ({DapperReads.IdsTable}) AND s.Date >= @rangeStart AND s.Date <= @rangeEnd
            ORDER BY s.Id
            """,
            new { ids = DapperReads.IdList(employees.Select(e => e.Pin)), rangeStart, rangeEnd },
            cancellationToken);

        await AttachSegmentsAsync(db, entries, cancellationToken);

        var byPin = entries.ToLookup(s => s.EmployeeId);
        foreach (var employee in employees)
        {
            employee.ScheduleEntries = [.. byPin[employee.Pin]];
            foreach (var entry in employee.ScheduleEntries)
                entry.Employee = employee;
        }
    }

    private static async Task AttachSegmentsAsync(
        ScheduleDbContext db, List<ScheduleEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        var segments = await db.QueryAsync<FlexibleSegment>(
            $"""
            SELECT {Columns.Of<FlexibleSegment>("f")} FROM FlexibleSegments f
            WHERE f.ScheduleEntryId IN ({DapperReads.IdsTable})
            ORDER BY f.Id
            """,
            new { ids = DapperReads.IdList(entries.Select(s => s.Id)) },
            cancellationToken);

        var byEntry = segments.ToLookup(f => f.ScheduleEntryId);
        foreach (var entry in entries)
        {
            entry.FlexibleSegments = [.. byEntry[entry.Id]];
            foreach (var segment in entry.FlexibleSegments)
                segment.ScheduleEntry = entry;
        }
    }
}
