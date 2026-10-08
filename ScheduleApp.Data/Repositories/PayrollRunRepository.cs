using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Queries;

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

    public async Task<PayrollRun?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        (await ListWhereAsync("WHERE r.Id = @id", new { id }, cancellationToken)).FirstOrDefault();

    public Task<List<PayrollRun>> ListAsync(CancellationToken cancellationToken = default) =>
        ListWhereAsync("ORDER BY r.CreatedAt DESC", null, cancellationToken);

    /// <summary>The runs <paramref name="filterAndOrder"/> (a WHERE and/or ORDER BY over
    /// <c>r</c>) selects, each with its membership rows (in Id order) stitched on by a second
    /// query.</summary>
    private Task<List<PayrollRun>> ListWhereAsync(string filterAndOrder, object? parameters,
        CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var runs = await db.QueryAsync<PayrollRun>(
                $"SELECT {Columns.Of<PayrollRun>("r")} FROM PayrollRuns r {filterAndOrder}",
                parameters, cancellationToken);
            if (runs.Count == 0) return runs;

            var members = await db.QueryAsync<PayrollRunEmployee>(
                $"""
                SELECT {Columns.Of<PayrollRunEmployee>("m")} FROM PayrollRunEmployees m
                WHERE m.PayrollRunId IN ({DapperReads.IdsTable})
                ORDER BY m.Id
                """,
                new { ids = DapperReads.IdList(runs.Select(r => r.Id)) }, cancellationToken);

            var byRun = members.ToLookup(m => m.PayrollRunId);
            foreach (var run in runs)
            {
                run.Employees = [.. byRun[run.Id]];
                foreach (var member in run.Employees)
                    member.PayrollRun = run;
            }

            return runs;
        }, cancellationToken);

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

    public async Task<int> AddEmployeesAsync(int runId, IReadOnlyCollection<int> employeePins,
        CancellationToken cancellationToken = default)
    {
        if (employeePins.Count == 0) return 0;

        var distinctPins = employeePins.Distinct().ToList();

        // Run-exists checked first here, unlike AddEmployeeAsync above (which checks
        // membership first, since the common single-employee case is a hit and that
        // saves the guard query) -- a batch's own membership query can't short-circuit
        // the FK risk the way a single hit does, so the guard has to come first.
        if (!await db.PayrollRuns.AnyAsync(r => r.Id == runId, cancellationToken)) return 0;

        // Narrowed to the requested set, not every member of the run -- same "derive
        // the bound from the batch itself" shape SqlAttendanceLogRepository.
        // AddLogsAsync applies to its own existing-keys query.
        var existingPins = await db.PayrollRunEmployees
            .Where(e => e.PayrollRunId == runId && distinctPins.Contains(e.EmployeeId))
            .Select(e => e.EmployeeId)
            .ToListAsync(cancellationToken);

        var toInsert = distinctPins
            .Except(existingPins)
            .Select(pin => new PayrollRunEmployee { PayrollRunId = runId, EmployeeId = pin })
            .ToList();

        if (toInsert.Count == 0) return 0;

        db.PayrollRunEmployees.AddRange(toInsert);
        await db.SaveChangesAsync(cancellationToken);
        return toInsert.Count;
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
