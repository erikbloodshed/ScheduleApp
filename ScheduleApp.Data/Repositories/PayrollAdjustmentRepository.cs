using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Reads and writes ScheduleApp's own PayrollAdjustments table -- see
/// IPayrollAdjustmentRepository. Registered in App.xaml.cs alongside
/// SqlManualAttendanceLogRepository.
/// </summary>
public class PayrollAdjustmentRepository(ScheduleDbContext db) : IPayrollAdjustmentRepository
{
    public Task<List<PayrollAdjustment>> GetForEmployeePeriodAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken cancellationToken = default) =>
        db.PayrollAdjustments
            .Where(a => a.EmployeeId == employeeId && a.PeriodStart == periodStart && a.PeriodEnd == periodEnd)
            .OrderBy(a => a.Type)
            .ThenBy(a => a.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<List<PayrollAdjustment>> GetForEmployeesPeriodAsync(IReadOnlyCollection<int> employeeIds,
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        db.PayrollAdjustments
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.PeriodStart == periodStart && a.PeriodEnd == periodEnd)
            .OrderBy(a => a.EmployeeId)
            .ThenBy(a => a.Type)
            .ThenBy(a => a.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<PayrollAdjustment> AddAsync(PayrollAdjustment adjustment, CancellationToken cancellationToken = default)
    {
        // Server-side backstop for PayrollViewModel.SetSingleValueAsync, which is
        // what actually keeps a normal person from reaching this for a
        // single-value type that already has its one row (that method updates
        // the existing row instead of adding a second one) -- this is only
        // ever meant to catch a caller that bypasses that UI, not something a
        // person should ever see in practice. No unique index backing this at the database
        // level (would need a filtered index scoped to just these six types,
        // and this is a single-user desktop app talking to its own DbContext,
        // not a multi-writer service) -- this check-then-insert isn't
        // race-proof against a second concurrent caller, but there isn't one.
        if (adjustment.Type.IsSingleValue())
            await EnsureNoExistingRowAsync(adjustment, excludingId: null, cancellationToken);

        var entry = new PayrollAdjustment
        {
            EmployeeId = adjustment.EmployeeId,
            PeriodStart = adjustment.PeriodStart,
            PeriodEnd = adjustment.PeriodEnd,
            Type = adjustment.Type,
            Amount = adjustment.Amount,
            Description = adjustment.Description,
            EnteredBy = adjustment.EnteredBy,
            CreatedAt = DateTime.UtcNow
        };

        db.PayrollAdjustments.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        return entry;
    }

    public async Task UpdateAsync(PayrollAdjustment adjustment, CancellationToken cancellationToken = default)
    {
        // Same guard as AddAsync, and needed for the same invariant: without
        // it, retyping an existing row from one single-value type to another
        // single-value type that already has its own row here (e.g. SSS ->
        // PhilHealth, when a PhilHealth row already exists for this
        // employee/period) would leave that other type with two rows instead
        // of one, the exact thing AddAsync's guard exists to prevent -- just
        // reached through Edit's Type combo instead of a second Add.
        // Excludes the row being updated itself (by Id) so a no-op edit
        // (saving SSS as SSS again) isn't mistaken for a conflict with
        // itself.
        if (adjustment.Type.IsSingleValue())
            await EnsureNoExistingRowAsync(adjustment, excludingId: adjustment.Id, cancellationToken);

        // ExecuteUpdateAsync rather than Find-then-modify-then-SaveChanges --
        // same one-round-trip approach as DeleteAsync below, and a no-op
        // (rather than a NullReferenceException) if the id is already gone.
        // EmployeeId/PeriodStart/PeriodEnd/CreatedAt are deliberately not in
        // the SetProperty list -- see the interface doc comment.
        await db.PayrollAdjustments
            .Where(a => a.Id == adjustment.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Type, adjustment.Type)
                .SetProperty(a => a.Amount, adjustment.Amount)
                .SetProperty(a => a.Description, adjustment.Description)
                .SetProperty(a => a.EnteredBy, adjustment.EnteredBy), cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync rather than Find-then-Remove -- one round trip, and a
        // no-op (rather than a NullReferenceException) if the id is already gone,
        // e.g. a double click on the grid's Delete button.
        await db.PayrollAdjustments
            .Where(a => a.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PayrollAdjustment>> AddRangeAsync(
        IReadOnlyCollection<PayrollAdjustment> adjustments, CancellationToken cancellationToken = default)
    {
        if (adjustments.Count == 0) return [];

        // No EnsureNoExistingRowAsync here -- see this method's own interface doc comment for
        // why the batch seed caller already has proof none of these collide with an existing
        // row, unlike AddAsync's single-row callers.
        var entries = adjustments.Select(adjustment => new PayrollAdjustment
        {
            EmployeeId = adjustment.EmployeeId,
            PeriodStart = adjustment.PeriodStart,
            PeriodEnd = adjustment.PeriodEnd,
            Type = adjustment.Type,
            Amount = adjustment.Amount,
            Description = adjustment.Description,
            EnteredBy = adjustment.EnteredBy,
            CreatedAt = DateTime.UtcNow,
        }).ToList();

        db.PayrollAdjustments.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken);

        return entries;
    }

    public async Task UpdateRangeAsync(
        IReadOnlyCollection<PayrollAdjustment> adjustments, CancellationToken cancellationToken = default)
    {
        if (adjustments.Count == 0) return;

        // Grouped by the exact (Type, Amount, Description, EnteredBy) tuple being written, then
        // one ExecuteUpdateAsync per group -- NOT Attach()-a-stub-then-SaveChanges, even though
        // that would otherwise be the more obvious way to turn "N rows, each with their own new
        // values" into fewer round trips. This db is the one shared, app-lifetime-scoped
        // ScheduleDbContext (see App.xaml.cs's own registration comment) -- AddAsync/
        // AddRangeAsync leave every row they insert tracked for the rest of the session, so an
        // Id this same session already added earlier is still sitting in the change tracker,
        // and Attach()ing a second, separate instance for that same Id later (exactly what a
        // subsequent reseed correcting a row from an earlier seed would need to do) throws.
        // ExecuteUpdateAsync sidesteps the change tracker entirely instead -- same untracked,
        // no-op-if-the-id-is-already-gone approach UpdateAsync's own single-row ExecuteUpdateAsync
        // already uses -- while the grouping still collapses every row landing on the same new
        // values into one UPDATE statement. That's the common case in practice: a whole cutoff's
        // worth of SSS/PhilHealth/Pag-IBIG rows correcting down to the same 0.00 non-withholding
        // amount together share the exact same (Type, Amount, Description, EnteredBy) tuple (see
        // SeedOrReseedContributionsAsync's own doc comment for why -- Description/EnteredBy are
        // both deterministic per Type once a row is still SeededEnteredBy, and Amount is 0.00 for
        // everyone on a non-withholding period regardless of each employee's own default).
        // Degrades to one statement per distinct group -- worst case, back to one per row -- only
        // when every row in the batch is genuinely being corrected to a different Amount (e.g.
        // distinct per-employee defaults on a withholding cutoff); never worse than the per-row
        // ExecuteUpdateAsync loop this replaces, just no longer paying that cost when the values
        // actually line up.
        var groups = adjustments.GroupBy(a => (a.Type, a.Amount, a.Description, a.EnteredBy));

        foreach (var group in groups)
        {
            var ids = group.Select(a => a.Id).ToList();
            var (type, amount, description, enteredBy) = group.Key;

            await db.PayrollAdjustments
                .Where(a => ids.Contains(a.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Type, type)
                    .SetProperty(a => a.Amount, amount)
                    .SetProperty(a => a.Description, description)
                    .SetProperty(a => a.EnteredBy, enteredBy), cancellationToken);
        }
    }

    /// <summary>Shared by AddAsync/UpdateAsync's guards above -- throws if
    /// this employee/period already has a row of adjustment.Type other than
    /// the one being updated (excludingId, null from AddAsync since there's
    /// nothing to exclude when nothing's been inserted yet).</summary>
    private async Task EnsureNoExistingRowAsync(PayrollAdjustment adjustment, int? excludingId,
        CancellationToken cancellationToken)
    {
        bool alreadySet = await db.PayrollAdjustments.AnyAsync(a =>
            a.Id != excludingId &&
            a.EmployeeId == adjustment.EmployeeId &&
            a.PeriodStart == adjustment.PeriodStart &&
            a.PeriodEnd == adjustment.PeriodEnd &&
            a.Type == adjustment.Type, cancellationToken);

        if (alreadySet)
        {
            throw new InvalidOperationException(
                $"{adjustment.Type.ToText()} already has a value set for this employee and period -- " +
                "edit or delete the existing one instead of adding another.");
        }
    }
}