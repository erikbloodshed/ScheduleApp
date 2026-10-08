using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Queries;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Reads and writes ScheduleApp's own PayrollUndertimeWaivers table -- see
/// IPayrollUndertimeWaiverRepository. Registered in App.xaml.cs alongside
/// PayrollAdjustmentRepository.
/// </summary>
public class PayrollUndertimeWaiverRepository(ScheduleDbContext db) : IPayrollUndertimeWaiverRepository
{
    public Task<bool> IsWaivedAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd,
        CancellationToken cancellationToken = default) =>
        db.QuerySingleAsync<bool>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM PayrollUndertimeWaivers w
                WHERE w.EmployeeId = @employeeId AND w.PeriodStart = @periodStart AND w.PeriodEnd = @periodEnd
            ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
            """,
            new { employeeId, periodStart, periodEnd }, cancellationToken);

    public async Task<HashSet<int>> GetWaivedPinsAsync(IReadOnlyCollection<int> employeeIds,
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        [.. await db.QueryAsync<int>(
            $"""
            SELECT w.EmployeeId FROM PayrollUndertimeWaivers w
            WHERE w.EmployeeId IN ({DapperReads.IdsTable}) AND w.PeriodStart = @periodStart AND w.PeriodEnd = @periodEnd
            """,
            new { ids = DapperReads.IdList(employeeIds), periodStart, periodEnd }, cancellationToken)];

    public async Task SetWaivedAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd, bool waived,
        CancellationToken cancellationToken = default)
    {
        if (waived)
        {
            // Check-then-insert, same non-race-proof-but-fine-for-a-single-user-desktop-
            // app reasoning as PayrollAdjustmentRepository.AddAsync's own guard -- avoids
            // a second row for this employee/period if the checkbox somehow fires Checked
            // twice in a row without an Unchecked in between.
            bool alreadyWaived = await db.PayrollUndertimeWaivers.AnyAsync(w =>
                w.EmployeeId == employeeId && w.PeriodStart == periodStart && w.PeriodEnd == periodEnd,
                cancellationToken);
            if (alreadyWaived) return;

            db.PayrollUndertimeWaivers.Add(new PayrollUndertimeWaiver
            {
                EmployeeId = employeeId,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // ExecuteDeleteAsync rather than Find-then-Remove -- one round trip, and a
            // no-op (rather than an error) if there's no row to begin with, same
            // reasoning as PayrollAdjustmentRepository.DeleteAsync.
            await db.PayrollUndertimeWaivers
                .Where(w => w.EmployeeId == employeeId && w.PeriodStart == periodStart && w.PeriodEnd == periodEnd)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }
}
