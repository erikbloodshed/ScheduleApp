using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Abstraction over ScheduleApp's PayrollRuns/PayrollRunEmployees tables (see
/// PayrollRunRepository). Interface and implementation live together here in
/// Data/Repositories -- same co-location as IPayrollAdjustmentRepository/
/// PayrollAdjustmentRepository.
/// </summary>
public interface IPayrollRunRepository
{
    /// <summary>Persists a new payroll run -- the header (Label/PeriodStart/
    /// PeriodEnd/CreatedBy, set by the caller) and its full membership list
    /// (run.Employees, each with just EmployeeId set) in one round trip. Id and
    /// CreatedAt are assigned here, same as IPayrollAdjustmentRepository.AddAsync
    /// does for Id/CreatedAt; returns the run back with those set (and each
    /// PayrollRunEmployee's own Id set too). A run's own membership is fixed at
    /// creation (see PayrollRun's own doc comment: re-running the wizard makes a new
    /// run rather than editing an old one) -- AddEmployeeAsync/AddEmployeesAsync/
    /// RemoveEmployeeAsync below are for adjusting an already-saved run's membership
    /// afterward, not for building the initial set.</summary>
    Task<PayrollRun> CreateAsync(PayrollRun run, CancellationToken cancellationToken = default);

    /// <summary>One run with its membership list populated -- what "Load Payroll
    /// Group…" resolves the moment a person picks a row from ListAsync's list.
    /// Null if id doesn't exist (e.g. it was deleted since the list was
    /// loaded).</summary>
    Task<PayrollRun?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Every saved run, newest first, membership list included -- what
    /// "Load Payroll Group…"'s picker dialog binds to directly. Not paged --
    /// same "single-user desktop app, not a multi-tenant service" reasoning
    /// IPayrollAdjustmentRepository's own doc comments lean on elsewhere; a
    /// payroll run is created at most a handful of times a month, so this list
    /// never grows large enough to need it.</summary>
    Task<List<PayrollRun>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a saved run and its membership rows (cascade, see
    /// ScheduleDbContext's PayrollRunEmployee configuration) -- e.g. one created
    /// by mistake. No-op if the id doesn't exist, same convention as
    /// IPayrollAdjustmentRepository.DeleteAsync. Deleting a run never touches the
    /// underlying PayrollAdjustment rows for its employees/period -- those are
    /// owned by the period itself, not by any one run that happened to group
    /// them (see PayrollRun's own doc comment on this being membership only).
    /// </summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Appends one employee (identified by Pin, same convention as
    /// PayrollRunEmployee.EmployeeId throughout) to an already-saved run's
    /// membership. Inserting someone already in the run is a no-op -- this method
    /// queries for an existing membership row first and returns its Id rather than
    /// staging a second insert, which the composite unique index on (PayrollRunId,
    /// EmployeeId) (see ScheduleDbContext) would otherwise reject outright. The index
    /// is the backstop behind that check-then-act, not a substitute for it -- same
    /// relationship Employee.Pin's own unique index has with
    /// AddEmployeeAsync/UpdateEmployeeAsync's own Pin checks on
    /// IScheduleRepository. No-op if runId doesn't exist (same missing-id tolerance
    /// as DeleteAsync). Returns the new row's assigned Id, or the existing row's Id
    /// when the employee was already a member.
    ///
    /// Prefer <see cref="AddEmployeesAsync"/> for adding more than one employee at a
    /// time -- looping this one costs three round trips per employee.</summary>
    Task<int> AddEmployeeAsync(int runId, int employeePin, CancellationToken cancellationToken = default);

    /// <summary>Bulk counterpart to AddEmployeeAsync -- appends many employees (by Pin)
    /// to an already-saved run's membership in one guard query, one existing-members
    /// query, and one SaveChangesAsync, instead of AddEmployeeAsync's three round trips
    /// per employee. Adding a twelve-person department to a group cost thirty-six round
    /// trips through that loop; it costs three here. Same "already a member is a no-op"
    /// and "no-op if runId doesn't exist" tolerances AddEmployeeAsync has, applied to
    /// the whole set at once: <paramref name="employeePins"/> is de-duplicated and
    /// narrowed to non-members before anything is staged, so nothing collides with the
    /// (PayrollRunId, EmployeeId) unique index (see ScheduleDbContext). Same "batch
    /// caller already holds proof of non-collision" reasoning as
    /// IPayrollAdjustmentRepository.AddRangeAsync, except the proof is established here
    /// rather than by the caller, since a group-membership picker has no equivalent
    /// already-fetched existing-rows list to lean on.
    ///
    /// Returns how many rows were actually inserted -- 0 means either every requested
    /// Pin was already a member or the run no longer exists, which callers are free to
    /// treat identically (both mean "the run's membership already is what you asked
    /// for, or there is no run to change"). Unlike AddEmployeeAsync, callers adding
    /// several employees at once should prefer this: the per-employee loop it replaces
    /// could commit some rows and then be cancelled or throw partway, leaving the
    /// database ahead of the caller's own in-memory list with nothing shown to the
    /// person.</summary>
    Task<int> AddEmployeesAsync(int runId, IReadOnlyCollection<int> employeePins,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one employee from an already-saved run's membership -- the
    /// inverse of AddEmployeeAsync. No-op if the employee isn't in the run, or if
    /// runId doesn't exist (same convention as DeleteAsync/AddEmployeeAsync).</summary>
    Task RemoveEmployeeAsync(int runId, int employeePin, CancellationToken cancellationToken = default);
}
