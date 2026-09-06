using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Reads and writes ScheduleApp's own Holidays table -- see IHolidayRepository.
/// Registered in App.xaml.cs alongside PayrollRunRepository.
/// </summary>
public class HolidayRepository(ScheduleDbContext db) : IHolidayRepository
{
    public Task<List<Holiday>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Holidays
            .OrderBy(h => h.Date)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<List<DateOnly>> ListDatesForPeriodAsync(
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        db.Holidays
            .Where(h => h.Date >= periodStart && h.Date <= periodEnd)
            .OrderBy(h => h.Date)
            .AsNoTracking()
            .Select(h => h.Date)
            .ToListAsync(cancellationToken);

    public async Task<Holiday> AddAsync(Holiday holiday, CancellationToken cancellationToken = default)
    {
        await EnsureDateIsFreeAsync(holiday, excludingId: null, cancellationToken);

        var entry = new Holiday { Date = holiday.Date, Name = holiday.Name };
        db.Holidays.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        return entry;
    }

    public async Task UpdateAsync(Holiday holiday, CancellationToken cancellationToken = default)
    {
        await EnsureDateIsFreeAsync(holiday, excludingId: holiday.Id, cancellationToken);

        // ExecuteUpdateAsync rather than Find-then-modify-then-SaveChanges -- same
        // one-round-trip approach PayrollAdjustmentRepository.UpdateAsync uses, and a
        // no-op (rather than a NullReferenceException) if the id is already gone.
        await db.Holidays
            .Where(h => h.Id == holiday.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(h => h.Date, holiday.Date)
                .SetProperty(h => h.Name, holiday.Name), cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync rather than Find-then-Remove -- one round trip, and a
        // no-op (rather than a NullReferenceException) if the id is already gone,
        // e.g. a double click on ManageHolidaysDialog's Delete button.
        await db.Holidays
            .Where(h => h.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Shared by AddAsync/UpdateAsync's guards above -- throws
    /// DuplicateHolidayDateException if another holiday already occupies
    /// holiday.Date (excludingId, null from AddAsync since there's nothing to
    /// exclude when nothing's been inserted yet). Checked here rather than left
    /// to the database's own unique index (see ScheduleDbContext's Holiday
    /// configuration) so HolidayDialog/ManageHolidaysDialog can show the message
    /// directly instead of a raw DbUpdateException -- same "check-then-insert,
    /// not race-proof but there isn't a second writer" reasoning
    /// PayrollAdjustmentRepository.EnsureNoExistingRowAsync documents for
    /// itself.</summary>
    private async Task EnsureDateIsFreeAsync(Holiday holiday, int? excludingId, CancellationToken cancellationToken)
    {
        var conflict = await db.Holidays
            .Where(h => h.Id != excludingId && h.Date == holiday.Date)
            .Select(h => h.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (conflict is not null)
        {
            throw new DuplicateHolidayDateException(holiday.Date, conflict);
        }
    }
}
