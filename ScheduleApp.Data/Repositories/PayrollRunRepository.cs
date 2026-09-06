using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Reads and writes ScheduleApp's own PayrollRuns/PayrollRunEmployees tables --
/// see IPayrollRunRepository. Registered in App.xaml.cs alongside
/// PayrollAdjustmentRepository.
/// </summary>
public class PayrollRunRepository(ScheduleDbContext db) : IPayrollRunRepository
{
    public async Task<PayrollRun> CreateAsync(PayrollRun run, CancellationToken cancellationToken = default)
    {
        var entry = new PayrollRun
        {
            Label = run.Label,
            PeriodStart = run.PeriodStart,
            PeriodEnd = run.PeriodEnd,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = run.CreatedBy,
            // Only EmployeeId is meaningful on an incoming row -- Id/PayrollRunId
            // are assigned by EF when this whole graph is saved below, same as
            // the parent's own Id is.
            Employees = run.Employees.Select(e => new PayrollRunEmployee { EmployeeId = e.EmployeeId }).ToList(),
        };

        db.PayrollRuns.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        return entry;
    }

    public Task<PayrollRun?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        db.PayrollRuns
            .Include(r => r.Employees)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<List<PayrollRun>> ListAsync(CancellationToken cancellationToken = default) =>
        db.PayrollRuns
            .Include(r => r.Employees)
            .OrderByDescending(r => r.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync rather than Find-then-Remove -- one round trip, and a
        // no-op (rather than a NullReferenceException) if the id is already gone,
        // same convention as PayrollAdjustmentRepository.DeleteAsync. The database-
        // level cascade (see ScheduleDbContext's PayrollRunEmployee configuration)
        // takes this run's PayrollRunEmployee rows with it -- no separate delete
        // needed for those here.
        await db.PayrollRuns
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> AddEmployeeAsync(int runId, int employeePin, CancellationToken cancellationToken = default)
    {
        // Return the existing row's Id if this Pin is already a member -- the unique
        // index on (PayrollRunId, EmployeeId) would reject a duplicate insert anyway,
        // so checking first is cheaper than catching a DbUpdateException and retrying.
        var existing = await db.PayrollRunEmployees
            .Where(e => e.PayrollRunId == runId && e.EmployeeId == employeePin)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing.HasValue) return existing.Value;

        // No-op if the run itself is gone (same missing-id tolerance as DeleteAsync):
        // inserting a child row against a non-existent parent would throw a FK
        // violation, so guard before adding.
        var runExists = await db.PayrollRuns.AnyAsync(r => r.Id == runId, cancellationToken);
        if (!runExists) return 0;

        var row = new PayrollRunEmployee { PayrollRunId = runId, EmployeeId = employeePin };
        db.PayrollRunEmployees.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task RemoveEmployeeAsync(int runId, int employeePin, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync mirrors DeleteAsync's own one-round-trip, no-op-if-missing
        // pattern -- filtering on both columns hits the composite unique index directly.
        await db.PayrollRunEmployees
            .Where(e => e.PayrollRunId == runId && e.EmployeeId == employeePin)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
