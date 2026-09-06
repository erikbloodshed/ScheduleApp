using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Abstraction over ScheduleApp's PayrollAdjustments table (see
/// PayrollAdjustmentRepository). Interface and implementation live together
/// here in Data/Repositories -- same co-location as IScheduleRepository/
/// ScheduleRepository -- rather than split across Core/Data the way
/// IManualAttendanceLogRepository is, per the Payroll Feature plan's own file
/// manifest.
/// </summary>
public interface IPayrollAdjustmentRepository
{
    /// <summary>Every adjustment row for one employee's one payroll period,
    /// across all types, ordered by Type then CreatedAt -- what
    /// PayrollSummaryView's itemized lists and PayrollCalculator's
    /// per-category subtotals both read from. PeriodStart/PeriodEnd must
    /// match a row's stored period exactly (no overlap/containment check) --
    /// a row only ever belongs to the one period it was entered for, see
    /// PayrollAdjustment's own doc comment.</summary>
    Task<List<PayrollAdjustment>> GetForEmployeePeriodAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken cancellationToken = default);

    /// <summary>Same rows as GetForEmployeePeriodAsync, but for a whole batch
    /// of employees in one round trip instead of one call per employee --
    /// what a batch seed/checklist over a department needs instead of N
    /// separate GetForEmployeePeriodAsync calls. Same exact-match semantics
    /// on PeriodStart/PeriodEnd (no overlap/containment check). Ordered by
    /// EmployeeId, then Type, then CreatedAt so callers can group by
    /// EmployeeId without re-sorting. employeeIds with no rows simply
    /// contribute nothing to the result -- not an error.</summary>
    Task<List<PayrollAdjustment>> GetForEmployeesPeriodAsync(IReadOnlyCollection<int> employeeIds,
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default);

    /// <summary>Persists one adjustment line (Id/CreatedAt are assigned here,
    /// same as IManualAttendanceLogRepository.AddAsync does for Id/CreatedAt)
    /// and returns it back with those set. Throws InvalidOperationException
    /// if adjustment.Type.IsSingleValue() and this employee/period already
    /// has a row of that type -- see PayrollViewModel.SetSingleValueAsync,
    /// which is what actually keeps a normal person from calling this in that
    /// state in the first place (it updates the existing row instead of
    /// adding a second one); this is the backstop, not the primary
    /// guard.</summary>
    Task<PayrollAdjustment> AddAsync(PayrollAdjustment adjustment, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing adjustment's editable fields (Type,
    /// Amount, Description, EnteredBy) in place -- EmployeeId/PeriodStart/
    /// PeriodEnd/CreatedAt are left untouched, same reasoning as
    /// IManualAttendanceLogRepository.UpdateAsync leaving CreatedAt alone.
    /// No-op if the id doesn't exist, same as DeleteAsync. Same
    /// InvalidOperationException as AddAsync if the new Type.IsSingleValue()
    /// and a *different* row (by Id) of that type already exists for this
    /// employee/period -- catches retyping this row into collision with
    /// another single-value row that AddAsync's own guard, being Add-only,
    /// wouldn't.</summary>
    Task UpdateAsync(PayrollAdjustment adjustment, CancellationToken cancellationToken = default);

    /// <summary>Removes an adjustment line, e.g. one entered by mistake.
    /// No-op if the id doesn't exist.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Bulk counterpart to AddAsync for a batch seed/reseed pass (see
    /// IPayrollComputationService.SeedContributionsForBatchAsync) that already knows, from its
    /// own already-fetched existing-rows list covering every employee in the batch, that none
    /// of <paramref name="adjustments"/> collides with a row that's already there -- so unlike
    /// AddAsync there's no per-row EnsureNoExistingRowAsync guard query here to pay for, and
    /// the whole set is persisted in one SaveChangesAsync call instead of one per row. Assigns
    /// Id/CreatedAt on each entry the same way AddAsync does and returns them back with those
    /// set, in the same order as <paramref name="adjustments"/>, so a caller can still match a
    /// returned row back to the employee it belongs to. A caller reached from direct user
    /// action (an Add/Edit dialog) should keep using AddAsync -- this overload is only safe
    /// when the caller already has proof-of-non-collision in hand the way a batch seed pass
    /// does.</summary>
    Task<IReadOnlyList<PayrollAdjustment>> AddRangeAsync(
        IReadOnlyCollection<PayrollAdjustment> adjustments, CancellationToken cancellationToken = default);

    /// <summary>Bulk counterpart to UpdateAsync, same "already has proof, skip the guard"
    /// reasoning as AddRangeAsync above -- groups <paramref name="adjustments"/> by the exact
    /// (Type, Amount, Description, EnteredBy) tuple each is being corrected to and runs one
    /// ExecuteUpdateAsync per group instead of one per row, which collapses to a single round
    /// trip whenever every row in the batch is landing on the same new values (the common
    /// case -- see the implementation's own doc comment for why) and degrades no worse than
    /// one round trip per row otherwise. Same no-op-on-a-missing-id convention UpdateAsync/
    /// DeleteAsync already follow individually -- a row another writer deleted between the
    /// batch's own existing-rows fetch and this call just doesn't match any group's WHERE
    /// clause, rather than failing every other correction in the same batch.</summary>
    Task UpdateRangeAsync(IReadOnlyCollection<PayrollAdjustment> adjustments, CancellationToken cancellationToken = default);
}