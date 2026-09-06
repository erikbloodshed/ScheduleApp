using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Data.Repositories;

public interface IScheduleRepository
{
    /// <summary>All departments with their employees (schedule entries not included).</summary>
    Task<List<Department>> GetDepartmentsWithEmployeesAsync(CancellationToken cancellationToken = default);

    /// <summary>Employees with no department assigned yet.</summary>
    Task<List<Employee>> GetUnassignedEmployeesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as GetDepartmentsWithEmployeesAsync, but each department's Employees
    /// collection excludes blacklisted employees (see Employee.IsBlacklisted).
    /// Used by consumers that only care about the active roster -- report scope,
    /// the Attendance tree, and payroll rosters/wizard -- so a blacklisted
    /// employee can't be picked for a report, attendance action, or payroll run
    /// without needing every one of those call sites to filter it out themselves.
    /// </summary>
    Task<List<Department>> GetActiveDepartmentsWithEmployeesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as GetUnassignedEmployeesAsync, but excludes blacklisted employees --
    /// see GetActiveDepartmentsWithEmployeesAsync's own doc comment for why.
    /// </summary>
    Task<List<Employee>> GetActiveUnassignedEmployeesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Departments with their Employees collection restricted to employeeIds, and each of
    /// those employees' ScheduleEntries restricted to [rangeStart, rangeEnd] (inclusive) --
    /// backs the Export Schedule scope dialog (MainViewModel.ExportScheduleAsync,
    /// ExcelScheduleExporter). A department with no employee in employeeIds is omitted
    /// entirely, not returned with an empty Employees collection -- so ExcelScheduleExporter
    /// never has to special-case an empty sheet for a department the user didn't select
    /// anyone from (see GetUnassignedForExportAsync's own "if(unassignedList.Count > 0)"
    /// check, which this mirrors by construction instead of by a post-query filter).
    /// </summary>
    Task<List<Department>> GetDepartmentsForExportAsync(IReadOnlyCollection<int> employeeIds, DateOnly rangeStart,
        DateOnly rangeEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same idea as GetDepartmentsForExportAsync, but for employees with no department --
    /// see that method's own doc comment. Only employees in employeeIds with
    /// DepartmentId == null are returned, each with ScheduleEntries restricted to
    /// [rangeStart, rangeEnd] (inclusive).
    /// </summary>
    Task<List<Employee>> GetUnassignedForExportAsync(IReadOnlyCollection<int> employeeIds, DateOnly rangeStart,
        DateOnly rangeEnd, CancellationToken cancellationToken = default);

    /// <summary>Every schedule entry for one employee, across all time. employeePin
    /// matches Employee.Pin -- see ScheduleEntry.EmployeeId's own doc comment.</summary>
    Task<List<ScheduleEntry>> GetScheduleEntriesForEmployeeAsync(int employeePin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedule entries dated within [periodStart, periodEnd] (inclusive). Employee is left
    /// unpopulated on every returned entry (see ScheduleEntry.Employee's own doc comment) --
    /// a caller that needs it fetches employees separately (by Pin) and attaches it itself,
    /// the way AttendanceWorkflowService.RunAsync does.
    /// </summary>
    /// <param name="employeePins">When non-null, restricts to entries whose EmployeeId
    /// (which matches Employee.Pin -- see ScheduleEntry.EmployeeId's own doc comment) is in
    /// this set -- lets a caller that only cares about specific employees (e.g.
    /// AttendanceWorkflowService.RunAsync's TargetPins) avoid pulling every other employee's
    /// entries across just to filter them back out afterward. Null (the default) means every
    /// employee, same as before this parameter existed.</param>
    Task<List<ScheduleEntry>> GetScheduleEntriesForPeriodAsync(DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyCollection<int>? employeePins = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Employees whose Pin is in the given set, each with Department populated --
    /// the lightweight counterpart to GetDepartmentsWithEmployeesAsync/
    /// GetUnassignedEmployeesAsync for a caller (AttendanceWorkflowService.RunAsync's
    /// TargetPins-scoped path) that only needs a handful of specific employees resolved
    /// rather than the whole company roster. An employee with no Pin set can never be
    /// matched here, same as everywhere else Pin is the join key (see
    /// AttendanceWorkflowService's own remarks on skippedNoPin).
    /// </summary>
    Task<List<Employee>> GetEmployeesByPinsAsync(IReadOnlyCollection<int> pins,
        CancellationToken cancellationToken = default);

    Task<Department> AddDepartmentAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a department. Its employees are unassigned (kept, DepartmentId set to null), not deleted.</summary>
    Task DeleteDepartmentAsync(int departmentId, CancellationToken cancellationToken = default);

    /// <param name="pin">Required (see Employee.Pin's own doc comment) -- the employee's
    /// ID on the ZKTeco device, assigned there before they can be added here at all.</param>
    /// <param name="employeeType">See Employee.EmployeeType.</param>
    /// <param name="monthlyRate">See Employee.MonthlyRate.</param>
    /// <param name="restDayWorkPremiumPercentage">See Employee.RestDayWorkPremiumPercentage.</param>
    /// <param name="defaultSss">See Employee.DefaultSss.</param>
    /// <param name="defaultPhilHealth">See Employee.DefaultPhilHealth.</param>
    /// <param name="defaultPagIbig">See Employee.DefaultPagIbig.</param>
    /// <param name="defaultPremiumPay">See Employee.DefaultPremiumPay.</param>
    /// <param name="defaultAllowance">See Employee.DefaultAllowance.</param>
    /// <param name="defaultCashAdvance">See Employee.DefaultCashAdvance.</param>
    /// <param name="qualifiesForRestDayPay">See Employee.QualifiesForRestDayPay.</param>
    /// <param name="qualifiesForPremiumPay">See Employee.QualifiesForPremiumPay.</param>
    /// <param name="clockInBufferBeforeHours">See Employee.ClockInBufferBeforeHours.</param>
    /// <param name="clockInBufferAfterHours">See Employee.ClockInBufferAfterHours.</param>
    /// <param name="clockOutBufferBeforeHours">See Employee.ClockOutBufferBeforeHours.</param>
    /// <param name="clockOutBufferAfterHours">See Employee.ClockOutBufferAfterHours.</param>
    /// <exception cref="Core.Exceptions.DuplicateEmployeeIdException">
    /// pin already belongs to a different employee.</exception>
    Task<Employee> AddEmployeeAsync(string lastName, string firstName, int? departmentId, int pin,
        bool qualifiesForOvertime = true, bool qualifiesForNightDiff = true, decimal dailyRate = 0m,
        bool defaultLeaveIsPaid = false, bool applyOvertimeRatePercentageByDefault = true,
        decimal defaultSss = 0m, decimal defaultPhilHealth = 0m, decimal defaultPagIbig = 0m,
        decimal defaultPremiumPay = 0m, decimal defaultAllowance = 0m, decimal defaultCashAdvance = 0m,
        EmployeeType employeeType = EmployeeType.Daily, decimal monthlyRate = 0m,
        decimal restDayWorkPremiumPercentage = 0m,
        bool qualifiesForRestDayPay = false, bool qualifiesForPremiumPay = false,
        double? clockInBufferBeforeHours = null, double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null, double? clockOutBufferAfterHours = null,
        CancellationToken cancellationToken = default);

    /// <summary>Renames, re-IDs, and/or moves an existing employee to a different (or no) department.</summary>
    /// <param name="pin">Required, same as AddEmployeeAsync's own -- there's no way to
    /// clear an employee's Pin back out through this.</param>
    /// <param name="employeeType">See Employee.EmployeeType.</param>
    /// <param name="monthlyRate">See Employee.MonthlyRate.</param>
    /// <param name="restDayWorkPremiumPercentage">See Employee.RestDayWorkPremiumPercentage.</param>
    /// <param name="defaultSss">See Employee.DefaultSss.</param>
    /// <param name="defaultPhilHealth">See Employee.DefaultPhilHealth.</param>
    /// <param name="defaultPagIbig">See Employee.DefaultPagIbig.</param>
    /// <param name="defaultPremiumPay">See Employee.DefaultPremiumPay.</param>
    /// <param name="defaultAllowance">See Employee.DefaultAllowance.</param>
    /// <param name="defaultCashAdvance">See Employee.DefaultCashAdvance.</param>
    /// <param name="qualifiesForRestDayPay">See Employee.QualifiesForRestDayPay.</param>
    /// <param name="qualifiesForPremiumPay">See Employee.QualifiesForPremiumPay.</param>
    /// <param name="clockInBufferBeforeHours">See Employee.ClockInBufferBeforeHours.</param>
    /// <param name="clockInBufferAfterHours">See Employee.ClockInBufferAfterHours.</param>
    /// <param name="clockOutBufferBeforeHours">See Employee.ClockOutBufferBeforeHours.</param>
    /// <param name="clockOutBufferAfterHours">See Employee.ClockOutBufferAfterHours.</param>
    /// <exception cref="Core.Exceptions.DuplicateEmployeeIdException">
    /// pin already belongs to a different employee.</exception>
    Task UpdateEmployeeAsync(int employeeId, string lastName, string firstName, int? departmentId, int pin,
        bool qualifiesForOvertime = true, bool qualifiesForNightDiff = true, decimal dailyRate = 0m,
        bool defaultLeaveIsPaid = false, bool applyOvertimeRatePercentageByDefault = true,
        decimal defaultSss = 0m, decimal defaultPhilHealth = 0m, decimal defaultPagIbig = 0m,
        decimal defaultPremiumPay = 0m, decimal defaultAllowance = 0m, decimal defaultCashAdvance = 0m,
        EmployeeType employeeType = EmployeeType.Daily, decimal monthlyRate = 0m,
        decimal restDayWorkPremiumPercentage = 0m,
        bool qualifiesForRestDayPay = false, bool qualifiesForPremiumPay = false,
        double? clockInBufferBeforeHours = null, double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null, double? clockOutBufferAfterHours = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an employee and (by cascade) all of their schedule entries.</summary>
    Task DeleteEmployeeAsync(int employeeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (or clears) an employee's blacklisted flag -- see Employee.IsBlacklisted.
    /// No-op if the employee doesn't exist.
    /// </summary>
    Task SetEmployeeBlacklistAsync(int employeeId, bool isBlacklisted, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (creates or overwrites) one employee's schedule for each given date.
    /// This is the only way schedules get written outside of import -- there's no
    /// separate "add"/"edit" since a day either has a schedule or it doesn't.
    /// <paramref name="employeePin"/> matches Employee.Pin (see ScheduleEntry.EmployeeId's
    /// own doc comment) and must already belong to a real employee -- throws
    /// InvalidOperationException otherwise (see the implementation's own doc comment for
    /// why this is a backstop, not the primary guard).
    /// timeIn is only meaningful for Normal (the shift start) -- pass null for
    /// Leave/Flexible/SplitShift. flexibleSegments is only meaningful when
    /// scheduleType is SplitShift (that day's allowed punching windows) -- pass
    /// null or empty for Normal/Leave/Flexible. ClockInBufferHours/
    /// ClockOutBufferHours on each tuple element are optional per-segment
    /// overrides for AttendancePolicy's FlexibleSegmentClockInBuffer/
    /// FlexibleSegmentClockOutBuffer defaults (see FlexibleSegment,
    /// FlexibleShiftCalculationStrategy) -- pass null for either (or both) to
    /// just inherit the policy default, which is what most callers want. Each
    /// date gets its own copy of the segments (new FlexibleSegment rows), not
    /// shared instances.
    /// clockInBufferBeforeHours/clockInBufferAfterHours/clockOutBufferBeforeHours/
    /// clockOutBufferAfterHours are the Normal-only equivalent -- optional
    /// per-day overrides for AttendancePolicy's ClockInBufferBefore/
    /// ClockInBufferAfter/ClockOutBufferBefore/ClockOutBufferAfter defaults (see
    /// ScheduleEntry, SingleWindowShiftCalculationStrategy). Pass null for any
    /// (or all) to just inherit the policy default. Meaningless for Flexible/
    /// SplitShift/Leave -- pass null for those, same as timeIn.
    /// restrictedTimeIn/restrictedTimeOut are only meaningful when scheduleType
    /// is Flexible (see ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut) --
    /// pass null (the common case, meaning no restriction on that side) for
    /// Normal/Leave/OfficialBusiness/SplitShift, same as flexibleSegments'
    /// null-for-inapplicable-types convention above but the other way round.
    /// A ScheduleType.Flexible date with flexibleSegments non-empty, or a
    /// ScheduleType.SplitShift date with either restricted time set, throws --
    /// see ScheduleEntry.ValidateScheduleTypeShape, which this enforces as it
    /// writes.
    /// </summary>
    /// <param name="isPaidLeave">Only meaningful when scheduleType is Leave; ignored
    /// otherwise. Null (the default) means Paid -- see ScheduleEntry.IsPaidLeave's own
    /// doc comment for why that's the expected default rather than an error.</param>
    /// <param name="overtimeEligibleOverride">Per-day override of
    /// Employee.QualifiesForOvertime -- null (the default, and by far the common
    /// case) means "no override, use the employee's own default" (see
    /// ScheduleEntry.OvertimeEligibleOverride).</param>
    /// <param name="nightDiffEligibleOverride">Same idea as
    /// <paramref name="overtimeEligibleOverride"/>, but for
    /// Employee.QualifiesForNightDiff.</param>
    /// <param name="applyOvertimeRatePercentageOverride">Per-day override of
    /// Employee.ApplyOvertimeRatePercentageByDefault -- the separate "does the
    /// overtime premium actually apply, once eligible" toggle. Null (the
    /// default) means "no override, use the employee's own default."</param>
    /// <param name="overtimeRatePercentageOverride">Per-day override of
    /// PayrollPolicy.OvertimeRatePercentage. Null (the default) means "no
    /// override, use the global policy default."</param>
    /// <param name="nightDiffRatePercentageOverride">Same idea as
    /// <paramref name="overtimeRatePercentageOverride"/>, but for
    /// PayrollPolicy.NightDiffRatePercentage.</param>
    Task SetScheduleForDatesAsync(int employeePin, IEnumerable<DateOnly> dates, ScheduleType scheduleType,
        decimal? workTimeHours, TimeOnly? timeIn,
        IEnumerable<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)>? flexibleSegments = null,
        double? clockInBufferBeforeHours = null, double? clockInBufferAfterHours = null,
        double? clockOutBufferBeforeHours = null, double? clockOutBufferAfterHours = null,
        bool? isPaidLeave = null,
        bool? overtimeEligibleOverride = null, bool? nightDiffEligibleOverride = null,
        bool? applyOvertimeRatePercentageOverride = null,
        decimal? overtimeRatePercentageOverride = null, decimal? nightDiffRatePercentageOverride = null,
        TimeOnly? restrictedTimeIn = null, TimeOnly? restrictedTimeOut = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the schedule (if any) for each given date. employeePin matches
    /// Employee.Pin (see ScheduleEntry.EmployeeId's own doc comment).</summary>
    Task ClearScheduleForDatesAsync(int employeePin, IEnumerable<DateOnly> dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates employees/entries imported from a full schedule workbook
    /// (one sheet per department, each row a date range that gets expanded into one
    /// ScheduleEntry per day). Employees are matched by (DepartmentId, Pin), otherwise by
    /// name, otherwise created new -- every incoming Employee already has a Pin by
    /// construction (see ExcelScheduleImporter's own parsing loop, and Employee.Pin's own
    /// doc comment). Per-day entries are upserted, so a later row in the sheet covering the
    /// same date as an earlier one wins -- this is how legacy "Temporary override" rows
    /// (now just a second Normal row with different hours) still take effect after the
    /// baseline row for the same day.
    /// </summary>
    Task ImportAsync(IEnumerable<Department> departmentsWithData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates employees from a roster workbook (Employees.xlsx-style,
    /// header-named columns -- see EmployeeRosterImporter for the recognized set).
    /// Matched by Pin. For each optional field on EmployeeImportRow (Department,
    /// DailyRate, IsOvertimeEligible, HasOvertimePremium, HasNightDiff, SSS,
    /// PhilHealth, PagIBIG, HasLeaveWithPay), a null value leaves a matched
    /// existing employee's own field unchanged, or leaves a new employee at that
    /// field's normal class default.
    /// </summary>
    Task ImportEmployeeRosterAsync(IEnumerable<EmployeeImportRow> rows, CancellationToken cancellationToken = default);
}