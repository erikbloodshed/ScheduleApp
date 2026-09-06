using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Abstraction over ScheduleApp's PayrollUndertimeWaivers table (see
/// PayrollUndertimeWaiverRepository). Interface and implementation live
/// together here in Data/Repositories -- same co-location as
/// IPayrollAdjustmentRepository/PayrollAdjustmentRepository. One row per
/// employee+period means "waived" (see PayrollUndertimeWaiver's own doc
/// comment) rather than a bool column, so there's nothing here shaped like
/// PayrollAdjustment's Add/Update/Delete triple -- just "is it waived" and
/// "set whether it's waived".
/// </summary>
public interface IPayrollUndertimeWaiverRepository
{
    /// <summary>True if this employee/period's Undertime deduction is
    /// currently waived -- what IPayrollComputationService.ComputeOneAsync reads
    /// before every PayrollCalculator.Calculate call, same "recomputed live
    /// every time the tab loads" spirit as the rest of Payroll (see
    /// PayrollResult's own doc comment).</summary>
    Task<bool> IsWaivedAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken cancellationToken = default);

    /// <summary>Same rows as IsWaivedAsync, but for a whole batch of employees in one round
    /// trip instead of one call per employee -- same "batch instead of N single-employee
    /// calls" role IPayrollAdjustmentRepository.GetForEmployeesPeriodAsync plays for
    /// adjustments (see that method's own doc comment), added alongside it for
    /// IPayrollComputationService.PrepareBatchAsync. Returns just the employee IDs that ARE
    /// waived, not a bool per id -- a caller checks Contains() against the result, same
    /// "absence means false" shape AttendanceWorkflowService's own targetPins HashSet
    /// already uses elsewhere. employeeIds with no waiver row simply aren't in the
    /// result -- not an error.</summary>
    Task<HashSet<int>> GetWaivedPinsAsync(IReadOnlyCollection<int> employeeIds,
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default);

    /// <summary>Sets whether this employee/period's Undertime is waived --
    /// adds the one row if <paramref name="waived"/> is true and none exists
    /// yet, removes it if false and one does. A no-op either way if the
    /// state already matches, so toggling the checkbox back and forth (or a
    /// duplicate Checked/Unchecked event) doesn't churn the table with
    /// redundant inserts/deletes.</summary>
    Task SetWaivedAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd, bool waived,
        CancellationToken cancellationToken = default);
}
