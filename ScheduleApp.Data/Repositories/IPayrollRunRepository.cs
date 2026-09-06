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
    /// PayrollRunEmployee's own Id set too). There's no update/append -- a
    /// payroll run's membership is fixed at creation (see PayrollRun's own doc
    /// comment: re-running the wizard makes a new run rather than editing an old
    /// one), so this is the only write this interface exposes besides
    /// DeleteAsync.</summary>
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
    /// membership. The composite unique index on (PayrollRunId, EmployeeId) means
    /// inserting someone already in the run is a no-op at the database level --
    /// callers do not need to pre-check. No-op if runId doesn't exist (same
    /// missing-id tolerance as DeleteAsync). Returns the new row's assigned Id,
    /// or the existing row's Id when the employee was already a member.</summary>
    Task<int> AddEmployeeAsync(int runId, int employeePin, CancellationToken cancellationToken = default);

    /// <summary>Removes one employee from an already-saved run's membership -- the
    /// inverse of AddEmployeeAsync. No-op if the employee isn't in the run, or if
    /// runId doesn't exist (same convention as DeleteAsync/AddEmployeeAsync).</summary>
    Task RemoveEmployeeAsync(int runId, int employeePin, CancellationToken cancellationToken = default);
}
