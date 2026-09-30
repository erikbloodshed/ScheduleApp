using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Data.Repositories;

public class ScheduleRepository(ScheduleDbContext db) : IScheduleRepository
{
    public async Task<List<Department>> GetDepartmentsWithEmployeesAsync(CancellationToken cancellationToken = default)
    {
        return await db.Departments
            .Include(d => d.Employees)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Employee>> GetUnassignedEmployeesAsync(CancellationToken cancellationToken = default)
    {
        return await db.Employees
            .Where(e => e.DepartmentId == null)
            .OrderBy(e => e.LastName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Department>> GetActiveDepartmentsWithEmployeesAsync(CancellationToken cancellationToken = default)
    {
        return await db.Departments
            .Include(d => d.Employees.Where(e => !e.IsBlacklisted))
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Employee>> GetActiveUnassignedEmployeesAsync(CancellationToken cancellationToken = default)
    {
        return await db.Employees
            .Where(e => e.DepartmentId == null && !e.IsBlacklisted)
            .OrderBy(e => e.LastName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Department>> GetDepartmentsForExportAsync(IReadOnlyCollection<int> employeeIds,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default)
    {
        // The outer Where (department has at least one matching employee) plus the filtered
        // Include below (that department's Employees collection narrowed to just those
        // matching employees) is what keeps a department the user didn't select anyone from
        // out of the result entirely, rather than coming back with an empty Employees list --
        // see this method's own doc comment on IScheduleRepository. employeeIds is
        // Employee.Id (the DB key) -- selecting WHICH employees is an Id-based tree pick,
        // entirely orthogonal to ScheduleEntry.Employee's own relationship (keyed off Pin
        // as an alternate key -- see ScheduleDbContext's own remarks on ScheduleEntry) that
        // the nested ThenInclude below rides on.
        return await db.Departments
            .Where(d => d.Employees.Any(e => employeeIds.Contains(e.Id)))
            .Include(d => d.Employees.Where(e => employeeIds.Contains(e.Id)))
                .ThenInclude(e => e.ScheduleEntries.Where(s => s.Date >= rangeStart && s.Date <= rangeEnd))
                    .ThenInclude(s => s.FlexibleSegments)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Employee>> GetUnassignedForExportAsync(IReadOnlyCollection<int> employeeIds,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default)
    {
        return await db.Employees
            .Where(e => e.DepartmentId == null && employeeIds.Contains(e.Id))
            .Include(e => e.ScheduleEntries.Where(s => s.Date >= rangeStart && s.Date <= rangeEnd))
                .ThenInclude(s => s.FlexibleSegments)
            .OrderBy(e => e.LastName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Every schedule entry for one employee, across all time -- see this method's
    /// own doc comment on IScheduleRepository. <paramref name="employeePin"/> matches
    /// Employee.Pin (see ScheduleEntry.EmployeeId's own doc comment).</summary>
    public async Task<List<ScheduleEntry>> GetScheduleEntriesForEmployeeAsync(int employeePin,
        CancellationToken cancellationToken = default)
    {
        return await db.ScheduleEntries
            .Where(s => s.EmployeeId == employeePin)
            .Include(s => s.FlexibleSegments)
            .OrderBy(s => s.Date)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Schedule entries dated within [periodStart, periodEnd] (inclusive), with
    /// each entry's Employee and Employee.Department populated -- see this method's own
    /// doc comment on IScheduleRepository. AttendanceWorkflowService.RunAsync, this
    /// method's only caller, no longer needs its own separate employee fetch/attach step
    /// to populate Employee -- restored to a real Include now that ScheduleEntry.Employee
    /// is a real FK again (see ScheduleDbContext's own remarks on ScheduleEntry).
    /// <paramref name="employeePins"/> matches directly against EmployeeId, which is
    /// Employee.Pin (see ScheduleEntry.EmployeeId's own doc comment) -- no join needed to
    /// reach it, it's sitting right there on the entry itself.</summary>
    public async Task<List<ScheduleEntry>> GetScheduleEntriesForPeriodAsync(DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyCollection<int>? employeePins = null, CancellationToken cancellationToken = default)
    {
        return await db.ScheduleEntries
            .Where(s => s.Date >= periodStart && s.Date <= periodEnd)
            .Where(s => employeePins == null || employeePins.Contains(s.EmployeeId))
            .Include(s => s.Employee)
                .ThenInclude(e => e!.Department)
            .Include(s => s.FlexibleSegments)
            .OrderBy(s => s.EmployeeId)
            .ThenBy(s => s.Date)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Employee>> GetEmployeesByPinsAsync(IReadOnlyCollection<int> pins,
        CancellationToken cancellationToken = default)
    {
        return await db.Employees
            .Where(e => pins.Contains(e.Pin))
            .Include(e => e.Department)
            .OrderBy(e => e.LastName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Department> AddDepartmentAsync(string name, CancellationToken cancellationToken = default)
    {
        var maxOrder = await db.Departments.Select(d => (int?)d.SortOrder).MaxAsync(cancellationToken) ?? 0;
        var department = new Department { Name = name, SortOrder = maxOrder + 1 };
        db.Departments.Add(department);
        await db.SaveChangesAsync(cancellationToken);
        return department;
    }

    /// <summary>
    /// Resolves every department named by an import in one pass: one query for the ones
    /// that already exist, then one MaxAsync (only when at least one is genuinely new) to
    /// find the sort order to append the rest after, incrementing locally from there the
    /// same way AddDepartmentAsync appends a single one. Replaces the per-name
    /// GetOrCreateDepartmentAsync this used to be, which cost a FirstOrDefaultAsync per
    /// department plus a MaxAsync AND a SaveChangesAsync per new one -- a roster workbook
    /// naming twenty departments paid forty-plus round trips before importing a single row.
    ///
    /// Newly-created departments are Added but deliberately NOT saved: the caller's own
    /// single SaveChangesAsync is what commits them, which is what lets a brand-new
    /// department, the employees being created in it, and their schedule entries all land
    /// in one atomic write (EF fills each new Employee.DepartmentId from the Department's
    /// generated Id at save time via the Employee.Department navigation -- same new-parent-
    /// plus-new-children graph PayrollRunRepository.CreateAsync already relies on). The
    /// corollary is that a caller must not return between calling this and its own save, or
    /// it leaves Added departments in the app-lifetime change tracker for some later,
    /// unrelated save to commit by accident.
    ///
    /// The returned dictionary is OrdinalIgnoreCase, and that is load-bearing, not tidiness:
    /// SQL Server's default collation is case-insensitive, so the existing-name query below
    /// matches a workbook's "kitchen" against a stored "Kitchen" and hands back the row
    /// spelled "Kitchen". An ordinal dictionary would then miss on "kitchen", create a
    /// second department, and be rejected by Department.Name's own unique index (see
    /// ScheduleDbContext) -- failing the whole import over a capital letter.
    /// </summary>
    private async Task<Dictionary<string, Department>> ResolveDepartmentsAsync(
        IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var requestedNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Department>(StringComparer.OrdinalIgnoreCase);
        if (requestedNames.Count == 0) return result;

        var existing = await db.Departments
            .Where(d => requestedNames.Contains(d.Name))
            .ToListAsync(cancellationToken);

        foreach (var department in existing)
            result[department.Name] = department;

        var missingNames = requestedNames.Where(name => !result.ContainsKey(name)).ToList();
        if (missingNames.Count == 0) return result;

        var nextSortOrder = await db.Departments.Select(d => (int?)d.SortOrder).MaxAsync(cancellationToken) ?? 0;

        foreach (var name in missingNames)
        {
            var department = new Department { Name = name, SortOrder = ++nextSortOrder };
            db.Departments.Add(department);
            result[name] = department;
        }

        return result;
    }

    public async Task DeleteDepartmentAsync(int departmentId, CancellationToken cancellationToken = default)
    {
        var department = await db.Departments.FindAsync(new object?[] { departmentId }, cancellationToken);
        if (department is null) return;

        // The FK is configured with ON DELETE SET NULL, so this unassigns rather
        // than deletes the department's employees -- no need to load them first.
        db.Departments.Remove(department);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Adds a new employee. <paramref name="pin"/> is required (see Employee.Pin's
    /// own doc comment) -- must already be enrolled on the ZKTeco device before this is
    /// called; there's no "add now, assign a Pin later" path anymore. Throws
    /// DuplicateEmployeeIdException if another employee already has this Pin.</summary>
    public async Task<Employee> AddEmployeeAsync(string lastName, string firstName, int? departmentId, int pin,
        bool qualifiesForOvertime = true, bool qualifiesForNightDiff = true, decimal dailyRate = 0m,
        bool defaultLeaveIsPaid = false, bool applyOvertimeRatePercentageByDefault = true,
        decimal defaultSss = 0m, decimal defaultPhilHealth = 0m, decimal defaultPagIbig = 0m,
        decimal defaultPremiumPay = 0m, decimal defaultAllowance = 0m, decimal defaultCashAdvance = 0m,
        EmployeeType employeeType = EmployeeType.Daily, decimal monthlyRate = 0m,
        decimal? restDayWorkPremiumPercentage = null,
        bool qualifiesForRestDayPay = false, bool qualifiesForPremiumPay = false,
        double? clockInBufferBeforeHours = null, double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null, double? clockOutBufferAfterHours = null,
        decimal? holidayPremiumPercentage = null,
        decimal? defaultWorkTimeHours = null, bool exemptFromUndertimeDeduction = false,
        CancellationToken cancellationToken = default)
    {
        if (await db.Employees.AnyAsync(e => e.Pin == pin, cancellationToken))
            throw new DuplicateEmployeeIdException(pin);

        var employee = new Employee
        {
            LastName = lastName,
            FirstName = firstName,
            DepartmentId = departmentId,
            Pin = pin,
            QualifiesForOvertime = qualifiesForOvertime,
            QualifiesForNightDiff = qualifiesForNightDiff,
            DailyRate = dailyRate,
            DefaultLeaveIsPaid = defaultLeaveIsPaid,
            ApplyOvertimeRatePercentageByDefault = applyOvertimeRatePercentageByDefault,
            DefaultSss = defaultSss,
            DefaultPhilHealth = defaultPhilHealth,
            DefaultPagIbig = defaultPagIbig,
            DefaultPremiumPay = defaultPremiumPay,
            DefaultAllowance = defaultAllowance,
            DefaultCashAdvance = defaultCashAdvance,
            EmployeeType = employeeType,
            MonthlyRate = monthlyRate,
            RestDayWorkPremiumPercentage = restDayWorkPremiumPercentage,
            QualifiesForRestDayPay = qualifiesForRestDayPay,
            QualifiesForPremiumPay = qualifiesForPremiumPay,
            ClockInBufferBeforeHours = clockInBufferBeforeHours,
            ClockInBufferAfterHours = clockInBufferAfterHours,
            ClockOutBufferBeforeHours = clockOutBufferBeforeHours,
            ClockOutBufferAfterHours = clockOutBufferAfterHours,
            HolidayPremiumPercentage = holidayPremiumPercentage,
            DefaultWorkTimeHours = defaultWorkTimeHours,
            ExemptFromUndertimeDeduction = exemptFromUndertimeDeduction
        };
        db.Employees.Add(employee);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniquePinViolation(ex))
        {
            // The check above is a check-then-act race -- another writer could have taken
            // this Pin between the check and this save. The unique index on Pin
            // (see ScheduleDbContext) is the actual guarantee; this just turns that into the
            // same friendly exception the pre-check throws, instead of a raw SQL error.
            //
            // The failed insert must also be detached here: SaveChangesAsync failing does
            // NOT revert the entity's tracked state, so without this, the same broken
            // "Added" employee would be resent (and fail again) on every later unrelated
            // save for the rest of the app session -- ScheduleDbContext lives for the whole
            // session (see App.xaml.cs), not just this one call.
            db.Entry(employee).State = EntityState.Detached;
            throw new DuplicateEmployeeIdException(pin);
        }

        return employee;
    }

    /// <summary>Updates an existing employee. <paramref name="pin"/> is required (see
    /// Employee.Pin's own doc comment) -- same as AddEmployeeAsync, there's no way to clear
    /// an employee's Pin back out through this once it's set. Throws
    /// DuplicateEmployeeIdException if another employee already has this Pin.</summary>
    public async Task UpdateEmployeeAsync(int employeeId, string lastName, string firstName, int? departmentId, int pin,
        bool qualifiesForOvertime = true, bool qualifiesForNightDiff = true, decimal dailyRate = 0m,
        bool defaultLeaveIsPaid = false, bool applyOvertimeRatePercentageByDefault = true,
        decimal defaultSss = 0m, decimal defaultPhilHealth = 0m, decimal defaultPagIbig = 0m,
        decimal defaultPremiumPay = 0m, decimal defaultAllowance = 0m, decimal defaultCashAdvance = 0m,
        EmployeeType employeeType = EmployeeType.Daily, decimal monthlyRate = 0m,
        decimal? restDayWorkPremiumPercentage = null,
        bool qualifiesForRestDayPay = false, bool qualifiesForPremiumPay = false,
        double? clockInBufferBeforeHours = null, double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null, double? clockOutBufferAfterHours = null,
        decimal? holidayPremiumPercentage = null,
        decimal? defaultWorkTimeHours = null, bool exemptFromUndertimeDeduction = false,
        CancellationToken cancellationToken = default)
    {
        var employee = await db.Employees.FindAsync(new object?[] { employeeId }, cancellationToken);
        if (employee is null) return;

        if (await db.Employees.AnyAsync(e => e.Pin == pin && e.Id != employeeId, cancellationToken))
            throw new DuplicateEmployeeIdException(pin);

        employee.LastName = lastName;
        employee.FirstName = firstName;
        employee.DepartmentId = departmentId;
        employee.Pin = pin;
        employee.QualifiesForOvertime = qualifiesForOvertime;
        employee.QualifiesForNightDiff = qualifiesForNightDiff;
        employee.DailyRate = dailyRate;
        employee.DefaultLeaveIsPaid = defaultLeaveIsPaid;
        employee.ApplyOvertimeRatePercentageByDefault = applyOvertimeRatePercentageByDefault;
        employee.DefaultSss = defaultSss;
        employee.DefaultPhilHealth = defaultPhilHealth;
        employee.DefaultPagIbig = defaultPagIbig;
        employee.DefaultPremiumPay = defaultPremiumPay;
        employee.DefaultAllowance = defaultAllowance;
        employee.DefaultCashAdvance = defaultCashAdvance;
        employee.EmployeeType = employeeType;
        employee.MonthlyRate = monthlyRate;
        employee.RestDayWorkPremiumPercentage = restDayWorkPremiumPercentage;
        employee.QualifiesForRestDayPay = qualifiesForRestDayPay;
        employee.QualifiesForPremiumPay = qualifiesForPremiumPay;
        employee.ClockInBufferBeforeHours = clockInBufferBeforeHours;
        employee.ClockInBufferAfterHours = clockInBufferAfterHours;
        employee.ClockOutBufferBeforeHours = clockOutBufferBeforeHours;
        employee.ClockOutBufferAfterHours = clockOutBufferAfterHours;
        employee.HolidayPremiumPercentage = holidayPremiumPercentage;
        employee.DefaultWorkTimeHours = defaultWorkTimeHours;
        employee.ExemptFromUndertimeDeduction = exemptFromUndertimeDeduction;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniquePinViolation(ex))
        {
            // Same check-then-act race as AddEmployeeAsync above. Reload (rather than
            // detach) here: unlike a fresh Add, this entity was already tracked before we
            // touched it (fetched via FindAsync), so reloading pulls its real, current
            // column values back from the DB and clears the "Modified" flags this failed
            // save left behind -- without it, the same bad update would be resent (and
            // fail again) on every later unrelated save for the rest of the app session.
            await db.Entry(employee).ReloadAsync(cancellationToken);
            throw new DuplicateEmployeeIdException(pin);
        }
    }

    /// <summary>
    /// True only for a SQL Server unique-constraint violation (error 2601 "duplicate key
    /// row" or 2627 "violation of unique constraint/index") against specifically the
    /// Pin index -- not just any DbUpdateException. Without the index-name check,
    /// an unrelated failure (e.g. a concurrently-deleted department tripping the
    /// DepartmentId foreign key) would be misreported as a duplicate Employee ID.
    /// </summary>
    private static bool IsUniquePinViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql &&
        sql.Message.Contains("IX_Employees_Pin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Composite key for the (LastName, FirstName) tier of ImportAsync's employee
    /// matching, as one OrdinalIgnoreCase-comparable string. Uses U+0001 (a non-printable
    /// control character) as the separator, rather than a printable one like a comma, so a
    /// surname containing the separator can't collide with a different name pair. Matched
    /// case-insensitively to stay equivalent to the SQL comparison this replaced (SQL
    /// Server's default collation is case-insensitive) -- see ResolveDepartmentsAsync's own
    /// doc comment for the same reasoning applied to department names.</summary>
    private static string NameKey(string lastName, string firstName) => $"{lastName}{firstName}";

    /// <summary>Returns the change tracker to a clean state after a failed import.
    /// SaveChangesAsync failing does NOT revert entity state, and ScheduleDbContext lives
    /// for the whole app session (see App.xaml.cs) -- without this, a failed import's
    /// still-Added departments/employees/entries/segments would be re-sent, and fail again,
    /// on every later unrelated save for the rest of the session. Same reasoning as
    /// AddEmployeeAsync's own single-entity detach above and
    /// SqlAttendanceLogRepository.AddLogsAsync's batch detach, just scoped to the four
    /// entity types an import touches so that unrelated tracked entities (payroll rows,
    /// punch logs) are left alone the way a blanket ChangeTracker.Clear() would not. Detach
    /// rather than Reload: every read path in this app is either AsNoTracking or a fresh
    /// Find/query, so a detached entity is simply re-materialized on next use, at a
    /// fraction of Reload's per-row round trip.</summary>
    private void DetachImportEntities()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.State == EntityState.Unchanged) continue;

            if (entry.Entity is Department or Employee or ScheduleEntry or FlexibleSegment)
                entry.State = EntityState.Detached;
        }
    }

    /// <summary>Deletes an employee, and (by cascade -- the real FK is back, see
    /// ScheduleDbContext's own remarks on ScheduleEntry) every ScheduleEntry keyed to
    /// their Pin. AttendanceLog/ManualAttendanceLog rows for this Pin are deliberately
    /// left alone -- historical punch data, kept even after the employee record
    /// referencing it is gone.</summary>
    public async Task DeleteEmployeeAsync(int employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await db.Employees.FindAsync(new object?[] { employeeId }, cancellationToken);
        if (employee is null) return;

        db.Employees.Remove(employee);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetEmployeeBlacklistAsync(int employeeId, bool isBlacklisted, CancellationToken cancellationToken = default)
    {
        var employee = await db.Employees.FindAsync(new object?[] { employeeId }, cancellationToken);
        if (employee is null) return;

        employee.IsBlacklisted = isBlacklisted;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Sets (creates or overwrites) one employee's schedule for each given date --
    /// see this method's own doc comment on IScheduleRepository. <paramref name="employeePin"/>
    /// matches Employee.Pin (see ScheduleEntry.EmployeeId's own doc comment), and must
    /// already belong to a real employee -- FK-enforced at the database level now (see
    /// ScheduleDbContext's own remarks on ScheduleEntry), so this pre-check exists only to
    /// turn what would otherwise be a raw SQL FK-violation error into the same friendly
    /// exception below, same "check-then-insert isn't the real guarantee, just a friendlier
    /// failure than whatever the database would have done" role PayrollAdjustmentRepository.
    /// AddAsync's own EnsureNoExistingRowAsync plays for a different invariant. In practice
    /// this should never actually trip -- every Employee row has a Pin (see Employee.Pin's
    /// own doc comment), so <paramref name="employeePin"/> failing to match one here would
    /// mean a caller passed a stale or outright wrong value, not a legitimately un-Pinned
    /// employee (there's no such state anymore).</summary>
    public async Task SetScheduleForDatesAsync(int employeePin, IEnumerable<DateOnly> dates, ScheduleType scheduleType,
        decimal? workTimeHours, TimeOnly? timeIn,
        IEnumerable<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)>? flexibleSegments = null,
        double? clockInBufferBeforeHours = null, double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null, double? clockOutBufferAfterHours = null,
        bool? isPaidLeave = null,
        bool? overtimeEligibleOverride = null, bool? nightDiffEligibleOverride = null,
        bool? applyOvertimeRatePercentageOverride = null,
        decimal? overtimeRatePercentageOverride = null, decimal? nightDiffRatePercentageOverride = null,
        TimeOnly? restrictedTimeIn = null, TimeOnly? restrictedTimeOut = null,
        CancellationToken cancellationToken = default)
    {
        var dateList = dates.Distinct().ToList();
        if (dateList.Count == 0) return;

        if (!await db.Employees.AnyAsync(e => e.Pin == employeePin, cancellationToken))
        {
            throw new InvalidOperationException(
                $"No employee has Employee ID {employeePin}.");
        }

        // Materialized once and reused per date below -- each date needs its own
        // FlexibleSegment instances (a segment belongs to exactly one ScheduleEntry),
        // not shared references to the same objects.
        var segmentList = flexibleSegments?.ToList()
            ?? new List<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)>();

        // FlexibleSegments must be loaded here (not AsNoTracking) so that clearing
        // the collection below is recognized by EF as deleting the old rows, rather
        // than just detaching them in memory -- see the class-level note on
        // FlexibleSegment's required FK / cascade-delete mapping.
        var existingByDate = await db.ScheduleEntries
            .Where(s => s.EmployeeId == employeePin && dateList.Contains(s.Date))
            .Include(s => s.FlexibleSegments)
            .ToDictionaryAsync(s => s.Date, cancellationToken);

        foreach (var date in dateList)
        {
            existingByDate.TryGetValue(date, out var entry);
            UpsertScheduleEntry(entry, employeePin, date, scheduleType, workTimeHours, timeIn, segmentList,
                clockInBufferBeforeHours, clockInBufferAfterHours, clockOutBufferBeforeHours, clockOutBufferAfterHours,
                isPaidLeave,
                overtimeEligibleOverride, nightDiffEligibleOverride, applyOvertimeRatePercentageOverride,
                overtimeRatePercentageOverride, nightDiffRatePercentageOverride,
                restrictedTimeIn, restrictedTimeOut);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Updates an existing ScheduleEntry's type/hours/time-in/segments/buffer-overrides
    /// in place, or builds and stages a new one for the given employee/date if
    /// <paramref name="existing"/> is null -- shared by SetScheduleForDatesAsync above and
    /// the per-entry import loop in ImportAsync below, which both apply this same
    /// clear-and-rebuild-segments logic against two differently-shaped "does this date
    /// already have an entry" lookups: SetScheduleForDatesAsync's own existingByDate is
    /// scoped to one employee, while ImportAsync's entriesByEmployeeDate is one dictionary
    /// for the entire workbook, keyed by (EmployeeId, Date). Either way, FlexibleSegments
    /// must already be loaded on <paramref name="existing"/>
    /// (via Include, not AsNoTracking) or clearing it here won't be recognized by EF as
    /// deleting the old rows. clockInBufferBeforeHours/clockInBufferAfterHours/
    /// clockOutBufferBeforeHours/clockOutBufferAfterHours are the Normal-only per-day
    /// overrides (see ScheduleEntry) -- pass null for any that should just inherit the
    /// policy default, which is what most callers want. overtimeEligibleOverride/
    /// nightDiffEligibleOverride/applyOvertimeRatePercentageOverride/
    /// overtimeRatePercentageOverride/nightDiffRatePercentageOverride are the Overtime/
    /// Night Diff per-day overrides added by the Attendance & Payroll Calculation
    /// Refactor Plan (see the matching properties on ScheduleEntry) -- same "null
    /// inherits the default" convention, and ImportAsync leaves all five at their
    /// default null (imported rows never carry a per-day override).
    /// restrictedTimeIn/restrictedTimeOut are Flexible's own optional single-window
    /// bound (see ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut) -- same
    /// null-means-unset convention as everything else here. Unlike the five
    /// Overtime/Night Diff overrides above, ImportAsync below does carry this pair
    /// through (from the incoming ScheduleEntry's own RestrictedTimeIn/
    /// RestrictedTimeOut) rather than always passing null, since -- unlike a
    /// per-day payroll override -- it's part of the day's own shape alongside
    /// FlexibleSegments, which ImportAsync already carries through the same way.
    /// Before returning, this calls ScheduleEntry.ValidateScheduleTypeShape as
    /// defense in depth against a caller mismatching scheduleType and segments/
    /// restricted times (e.g. Flexible with segments, or SplitShift with a
    /// restricted time set) -- the same shape rule the model itself enforces,
    /// just checked here too since this is the one place both write paths
    /// (SetScheduleForDatesAsync above and ImportAsync below) funnel through.
    /// </summary>
    private ScheduleEntry UpsertScheduleEntry(
        ScheduleEntry? existing,
        int employeePin,
        DateOnly date,
        ScheduleType scheduleType,
        decimal? workTimeHours,
        TimeOnly? timeIn,
        IEnumerable<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)> segments,
        double? clockInBufferBeforeHours = null,
        double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null,
        double? clockOutBufferAfterHours = null,
        bool? isPaidLeave = null,
        bool? overtimeEligibleOverride = null,
        bool? nightDiffEligibleOverride = null,
        bool? applyOvertimeRatePercentageOverride = null,
        decimal? overtimeRatePercentageOverride = null,
        decimal? nightDiffRatePercentageOverride = null,
        TimeOnly? restrictedTimeIn = null,
        TimeOnly? restrictedTimeOut = null)
    {
        // Same nullable-when-inapplicable convention as WorkTimeHours/TimeIn --
        // never set outside Leave, and defaults to Paid (true) rather than null
        // for Leave itself when the caller didn't say -- see
        // ScheduleEntry.IsPaidLeave's own doc comment for why that's the expected
        // default, not an omission.
        var resolvedIsPaidLeave = scheduleType == ScheduleType.Leave ? isPaidLeave ?? true : (bool?)null;

        if (existing is not null)
        {
            existing.ScheduleType = scheduleType;
            existing.WorkTimeHours = workTimeHours;
            existing.TimeIn = timeIn;
            existing.IsPaidLeave = resolvedIsPaidLeave;
            existing.ClockInBufferBeforeHours = clockInBufferBeforeHours;
            existing.ClockInBufferAfterHours = clockInBufferAfterHours;
            existing.ClockOutBufferBeforeHours = clockOutBufferBeforeHours;
            existing.ClockOutBufferAfterHours = clockOutBufferAfterHours;
            existing.OvertimeEligibleOverride = overtimeEligibleOverride;
            existing.NightDiffEligibleOverride = nightDiffEligibleOverride;
            existing.ApplyOvertimeRatePercentageOverride = applyOvertimeRatePercentageOverride;
            existing.OvertimeRatePercentageOverride = overtimeRatePercentageOverride;
            existing.NightDiffRatePercentageOverride = nightDiffRatePercentageOverride;
            existing.RestrictedTimeIn = restrictedTimeIn;
            existing.RestrictedTimeOut = restrictedTimeOut;

            existing.FlexibleSegments.Clear();
            foreach (var seg in segments)
                existing.FlexibleSegments.Add(new FlexibleSegment
                {
                    TimeIn = seg.TimeIn,
                    TimeOut = seg.TimeOut,
                    ClockInBufferHours = seg.ClockInBufferHours,
                    ClockOutBufferHours = seg.ClockOutBufferHours,
                });

            existing.ValidateScheduleTypeShape();
            return existing;
        }

        var newEntry = new ScheduleEntry
        {
            EmployeeId = employeePin,
            Date = date,
            ScheduleType = scheduleType,
            WorkTimeHours = workTimeHours,
            TimeIn = timeIn,
            IsPaidLeave = resolvedIsPaidLeave,
            ClockInBufferBeforeHours = clockInBufferBeforeHours,
            ClockInBufferAfterHours = clockInBufferAfterHours,
            ClockOutBufferBeforeHours = clockOutBufferBeforeHours,
            ClockOutBufferAfterHours = clockOutBufferAfterHours,
            OvertimeEligibleOverride = overtimeEligibleOverride,
            NightDiffEligibleOverride = nightDiffEligibleOverride,
            ApplyOvertimeRatePercentageOverride = applyOvertimeRatePercentageOverride,
            OvertimeRatePercentageOverride = overtimeRatePercentageOverride,
            NightDiffRatePercentageOverride = nightDiffRatePercentageOverride,
            RestrictedTimeIn = restrictedTimeIn,
            RestrictedTimeOut = restrictedTimeOut,
        };

        foreach (var seg in segments)
            newEntry.FlexibleSegments.Add(new FlexibleSegment
            {
                TimeIn = seg.TimeIn,
                TimeOut = seg.TimeOut,
                ClockInBufferHours = seg.ClockInBufferHours,
                ClockOutBufferHours = seg.ClockOutBufferHours,
            });

        newEntry.ValidateScheduleTypeShape();
        db.ScheduleEntries.Add(newEntry);
        return newEntry;
    }

    /// <summary>Removes the schedule (if any) for each given date -- see this method's own
    /// doc comment on IScheduleRepository. employeePin matches Employee.Pin (see
    /// ScheduleEntry.EmployeeId's own doc comment); no existence check the way
    /// SetScheduleForDatesAsync has one, since there's nothing unsafe about a no-op clear
    /// against a Pin nothing matches.</summary>
    public async Task ClearScheduleForDatesAsync(int employeePin, IEnumerable<DateOnly> dates,
        CancellationToken cancellationToken = default)
    {
        var dateList = dates.Distinct().ToList();
        if (dateList.Count == 0) return;

        var existing = await db.ScheduleEntries
            .Where(s => s.EmployeeId == employeePin && dateList.Contains(s.Date))
            .ToListAsync(cancellationToken);

        if (existing.Count == 0) return;

        db.ScheduleEntries.RemoveRange(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Inserts or updates employees/entries imported from a full schedule workbook
    /// -- see this method's own doc comment on IScheduleRepository. Every incoming Employee
    /// already has a Pin by construction (ExcelScheduleImporter skips any row with a blank
    /// ID cell before ever building one -- see that class's own parsing loop -- and
    /// Employee.Pin is a plain, non-nullable int now regardless), so unlike the brief
    /// stretch where Pin could be optional, there's no "create the employee anyway, skip
    /// just their schedule" fallback needed here anymore.
    ///
    /// Three SELECTs (departments, employees, schedule entries) and one SaveChangesAsync for
    /// the whole workbook, not one round trip per department/employee/entry the way this
    /// used to work -- a one-month sheet for 100 employees is ~3,000 transient entries,
    /// which cost ~300 round trips plus 100 unbounded full-history fetches before this (see
    /// SqlAttendanceLogRepository.AddLogsAsync's own doc comment for the same "load once
    /// into a dictionary, save once" shape applied to a different table). All-or-nothing: if
    /// anything below throws -- including a Pin that turns out to belong to an employee
    /// outside this sheet's own department, see the DuplicateEmployeeIdException check below
    /// -- nothing from this call is persisted, and the change tracker is cleaned up (see
    /// DetachImportEntities) so a failed import doesn't poison every later unrelated save
    /// for the rest of the app session.
    ///
    /// <paramref name="departmentsWithData"/> is enumerated more than once now (each
    /// Department's Employees, and each Employee's ScheduleEntries), so it must be a
    /// materialized collection, not a lazily-evaluated sequence.</summary>
    public async Task ImportAsync(IReadOnlyCollection<Department> departmentsWithData,
        CancellationToken cancellationToken = default)
    {
        if (departmentsWithData.Count == 0) return;

        var departmentsByName = await ResolveDepartmentsAsync(
            [.. departmentsWithData.Select(d => d.Name)], cancellationToken);

        var incomingPins = departmentsWithData
            .SelectMany(d => d.Employees)
            .Select(e => e.Pin)
            .Distinct()
            .ToList();

        var incomingEntries = departmentsWithData
            .SelectMany(d => d.Employees)
            .SelectMany(e => e.ScheduleEntries)
            .ToList();

        // Every department this workbook resolved to a real, already-saved row -- a
        // department this import is creating has no employees to preload (see
        // ResolveDepartmentsAsync's own doc comment on why newly-created departments
        // aren't saved yet).
        var departmentsById = departmentsByName.Values
            .Where(d => d.Id != 0)
            .ToDictionary(d => d.Id);
        var existingDepartmentIds = departmentsById.Keys.ToList();

        // Every employee this import could possibly need to match against: tier 1 (Pin,
        // wherever they currently live -- including unassigned or a different department,
        // so a collision is caught below instead of surfacing as a raw unique-index
        // failure at SaveChangesAsync) or tier 2 (already in one of the departments this
        // workbook touches, matched by name -- see IScheduleRepository.ImportAsync's own
        // doc comment on match precedence).
        List<Employee> candidates;
        if (incomingPins.Count == 0)
        {
            candidates = [];
        }
        else
        {
            candidates = await db.Employees
                .Where(e => incomingPins.Contains(e.Pin)
                         || (e.DepartmentId != null && existingDepartmentIds.Contains(e.DepartmentId.Value)))
                .OrderBy(e => e.Id)
                .ToListAsync(cancellationToken);
        }

        var employeesByPin = new Dictionary<int, Employee>();

        // Tracks which resolved Department each known Pin is currently associated with --
        // null for a real, already-saved employee with no department -- so a same-Pin
        // collision can be detected by reference regardless of whether the department in
        // question is one this import just resolved (a brand-new employee created a few
        // lines down has no saved DepartmentId yet to compare against). See
        // employeesByNameInDept below for why keying by Department object rather than Id
        // is the same story one level down.
        var departmentByPin = new Dictionary<int, Department?>();

        // Keyed by the resolved Department object, not its Id: a department this import is
        // creating still has Id 0 until the single save at the end, so an Id-keyed lookup
        // would collapse every new department into one bucket and let a same-named
        // employee in new department A satisfy the name-fallback for new department B.
        var employeesByNameInDept = new Dictionary<Department, Dictionary<string, Employee>>();

        foreach (var candidate in candidates)
        {
            if (!employeesByPin.TryAdd(candidate.Pin, candidate)) continue;

            var candidateDepartment = candidate.DepartmentId is { } deptId
                ? departmentsById.GetValueOrDefault(deptId)
                : null;
            departmentByPin[candidate.Pin] = candidateDepartment;

            if (candidateDepartment is null) continue;

            if (!employeesByNameInDept.TryGetValue(candidateDepartment, out var byName))
                employeesByNameInDept[candidateDepartment] = byName = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
            byName.TryAdd(NameKey(candidate.LastName, candidate.FirstName), candidate);
        }

        // One dictionary for the whole workbook, not one per employee -- see this method's
        // own doc comment. Re-seeded as each row resolves below (same double duty
        // SqlAttendanceLogRepository.AddLogsAsync's seenKeys does), so a later row in the
        // sheet for the same date overwrites an earlier one within this same import, and
        // two worksheets that resolve to the same department (e.g. differing only by case)
        // can't each stage a competing insert for the same (EmployeeId, Date) the way two
        // separate, per-employee database queries used to risk.
        var entriesByEmployeeDate = new Dictionary<(int EmployeeId, DateOnly Date), ScheduleEntry>();

        if (incomingEntries.Count > 0)
        {
            var minDate = incomingEntries.Min(e => e.Date);
            var maxDate = incomingEntries.Max(e => e.Date);

            // FlexibleSegments must be loaded here (not AsNoTracking) so that clearing an
            // existing entry's segments in UpsertScheduleEntry is recognized by EF as
            // deleting the old rows, not just detaching them in memory -- see that
            // method's own doc comment.
            var existingEntries = await db.ScheduleEntries
                .Where(s => incomingPins.Contains(s.EmployeeId) && s.Date >= minDate && s.Date <= maxDate)
                .Include(s => s.FlexibleSegments)
                .ToListAsync(cancellationToken);

            foreach (var entry in existingEntries)
                entriesByEmployeeDate[(entry.EmployeeId, entry.Date)] = entry;
        }

        try
        {
            foreach (var incomingDept in departmentsWithData)
            {
                var department = departmentsByName[incomingDept.Name];

                foreach (var incomingEmployee in incomingDept.Employees)
                {
                    var isNewEmployee = false;
                    Employee? employee;

                    if (employeesByPin.TryGetValue(incomingEmployee.Pin, out var pinMatch))
                    {
                        // A real match exists, just not in this sheet's own department --
                        // department-scoped tier 1/2 matching (see
                        // IScheduleRepository.ImportAsync's own doc comment) can't reach
                        // them, and creating a second Employee row with the same Pin
                        // would fail Employee.Pin's own unique index (see
                        // ScheduleDbContext) with a raw SQL error instead of this.
                        // Pre-existing outcome (the import already failed here before
                        // this rewrite) -- the only change is that it now fails
                        // atomically instead of leaving earlier departments/employees in
                        // this same run already committed.
                        if (!ReferenceEquals(departmentByPin[incomingEmployee.Pin], department))
                            throw new DuplicateEmployeeIdException(incomingEmployee.Pin);

                        employee = pinMatch;
                    }
                    else if (employeesByNameInDept.TryGetValue(department, out var byName) &&
                             byName.TryGetValue(NameKey(incomingEmployee.LastName, incomingEmployee.FirstName), out var nameMatch))
                    {
                        employee = nameMatch;
                    }
                    else
                    {
                        employee = null;
                    }

                    if (employee is null)
                    {
                        employee = new Employee
                        {
                            LastName = incomingEmployee.LastName,
                            FirstName = incomingEmployee.FirstName,
                            Pin = incomingEmployee.Pin,
                            Department = department,
                        };
                        db.Employees.Add(employee);
                        isNewEmployee = true;

                        employeesByPin[employee.Pin] = employee;
                        departmentByPin[employee.Pin] = department;

                        if (!employeesByNameInDept.TryGetValue(department, out var byNameNew))
                            employeesByNameInDept[department] = byNameNew = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
                        byNameNew[NameKey(employee.LastName, employee.FirstName)] = employee;
                    }

                    // Seeded once per employee, then updated in-memory as we go, so a
                    // later row in the sheet for the same date overwrites an earlier one
                    // within this same import -- not just against what was already in
                    // the database.
                    foreach (var incomingEntry in incomingEmployee.ScheduleEntries)
                    {
                        entriesByEmployeeDate.TryGetValue((employee.Pin, incomingEntry.Date), out var existingEntry);

                        var entry = UpsertScheduleEntry(
                            existingEntry,
                            employee.Pin,
                            incomingEntry.Date,
                            incomingEntry.ScheduleType,
                            incomingEntry.WorkTimeHours,
                            incomingEntry.TimeIn,
                            incomingEntry.FlexibleSegments.Select(s =>
                                (s.TimeIn, s.TimeOut, s.ClockInBufferHours, s.ClockOutBufferHours)),
                            incomingEntry.ClockInBufferBeforeHours,
                            incomingEntry.ClockInBufferAfterHours,
                            incomingEntry.ClockOutBufferBeforeHours,
                            incomingEntry.ClockOutBufferAfterHours,
                            incomingEntry.IsPaidLeave,
                            restrictedTimeIn: incomingEntry.RestrictedTimeIn,
                            restrictedTimeOut: incomingEntry.RestrictedTimeOut);

                        // Belt-and-braces for a brand-new employee only: EF orders the
                        // Employee INSERT before this entry's from the EmployeeId value
                        // alone (the FK targets Employee.Pin as an alternate key -- see
                        // ScheduleDbContext's own remarks on ScheduleEntry -- and Pin is
                        // client-assigned here, so EF's insert-ordering graph matches
                        // the two commands up by value with no navigation involved).
                        // Setting it anyway makes that dependency explicit rather than
                        // implicit, at the cost of one assignment. Only for new
                        // employees: an existing one has nothing left to order.
                        if (isNewEmployee) entry.Employee = employee;

                        // Re-seed the map with whatever this row resolved to (existing
                        // or newly-added) -- see the comment above entriesByEmployeeDate
                        // for why.
                        entriesByEmployeeDate[(employee.Pin, incomingEntry.Date)] = entry;
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DetachImportEntities();
            throw;
        }
    }

    /// <summary>Inserts or updates employees from a roster workbook -- see this method's
    /// own doc comment on IScheduleRepository. Matched globally by Pin, deliberately
    /// unlike ImportAsync's department-scoped two-tier match -- a roster row carries no
    /// per-department context to scope against, and this is the authoritative employee
    /// record, not a per-department view of one (see IScheduleRepository's own doc comment
    /// on this asymmetry). One department-name resolve (ResolveDepartmentsAsync), one
    /// employee SELECT, and one SaveChangesAsync for the whole file, not one round trip
    /// per row. All-or-nothing on failure, same DetachImportEntities cleanup and reasoning
    /// as ImportAsync above.</summary>
    public async Task ImportEmployeeRosterAsync(IReadOnlyCollection<EmployeeImportRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) return;

        var departmentNames = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.DepartmentName))
            .Select(r => r.DepartmentName!)
            .ToList();
        var departmentsByName = await ResolveDepartmentsAsync(departmentNames, cancellationToken);

        var pins = rows.Select(r => r.Pin).Distinct().ToList();
        var existingEmployees = await db.Employees
            .Where(e => pins.Contains(e.Pin))
            .ToListAsync(cancellationToken);

        var employeesByPin = new Dictionary<int, Employee>();
        foreach (var employee in existingEmployees)
            employeesByPin[employee.Pin] = employee;

        try
        {
            foreach (var row in rows)
            {
                Department? department = null;
                if (!string.IsNullOrWhiteSpace(row.DepartmentName))
                    department = departmentsByName[row.DepartmentName];

                if (!employeesByPin.TryGetValue(row.Pin, out var employee))
                {
                    employee = new Employee
                    {
                        Pin = row.Pin,
                        LastName = row.LastName,
                        FirstName = row.FirstName,
                        Department = department,
                    };
                    ApplyOptionalImportFields(employee, row);
                    db.Employees.Add(employee);

                    // EmployeeRosterImporter already rejects duplicate Pins within one
                    // file (see that class's own doc comment) -- re-seeding here anyway
                    // makes this loop's own correctness independent of that upstream
                    // guarantee, same "seenKeys does double duty" reasoning
                    // SqlAttendanceLogRepository.AddLogsAsync applies to its own
                    // batch-plus-database dictionary.
                    employeesByPin[employee.Pin] = employee;
                }
                else
                {
                    employee.LastName = row.LastName;
                    employee.FirstName = row.FirstName;
                    if (department is not null)
                        employee.Department = department;
                    ApplyOptionalImportFields(employee, row);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DetachImportEntities();
            throw;
        }
    }

    /// <summary>
    /// Applies EmployeeImportRow's optional payroll-related fields (everything
    /// except EmployeeId/LastName/FirstName/Department, which ImportEmployeeRosterAsync
    /// already handles above) to an employee, leaving each one untouched when the
    /// row didn't supply a value -- same "blank means don't change this on update"
    /// rule ImportEmployeeRosterAsync already applies to DepartmentName, just
    /// extended to the newer optional columns. On a brand-new Employee this
    /// naturally leaves the class's own field-initializer default in place instead
    /// (e.g. QualifiesForOvertime stays true) since nothing gets assigned. Shared
    /// by both the new-employee and existing-employee branches above so the same
    /// eight fields aren't checked twice.
    /// </summary>
    private static void ApplyOptionalImportFields(Employee employee, EmployeeImportRow row)
    {
        if (row.DailyRate is decimal dailyRate) employee.DailyRate = dailyRate;
        if (row.QualifiesForOvertime is bool qualifiesForOvertime) employee.QualifiesForOvertime = qualifiesForOvertime;
        if (row.ApplyOvertimeRatePercentageByDefault is bool applyOvertimeRatePercentageByDefault)
            employee.ApplyOvertimeRatePercentageByDefault = applyOvertimeRatePercentageByDefault;
        if (row.QualifiesForNightDiff is bool qualifiesForNightDiff) employee.QualifiesForNightDiff = qualifiesForNightDiff;
        if (row.DefaultSss is decimal defaultSss) employee.DefaultSss = defaultSss;
        if (row.DefaultPhilHealth is decimal defaultPhilHealth) employee.DefaultPhilHealth = defaultPhilHealth;
        if (row.DefaultPagIbig is decimal defaultPagIbig) employee.DefaultPagIbig = defaultPagIbig;
        if (row.DefaultLeaveIsPaid is bool defaultLeaveIsPaid) employee.DefaultLeaveIsPaid = defaultLeaveIsPaid;
    }
}