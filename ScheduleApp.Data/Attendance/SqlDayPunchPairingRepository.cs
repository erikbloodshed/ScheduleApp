using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Data.Attendance;

/// <summary>
/// Reads and writes ScheduleApp's own DayPunchPairings/DayPunchPairingSlots tables
/// -- the hand-edited per-day punch pairings saved from the Day Punch Pairing
/// editor (see <see cref="DayPunchPairing"/>). Registered in App.xaml.cs alongside
/// SqlManualAttendanceLogRepository.
/// </summary>
public class SqlDayPunchPairingRepository(ScheduleDbContext db) : IDayPunchPairingRepository
{
    public Task<List<DayPunchPairing>> GetForRangeAsync(DateOnly start, DateOnly end,
        IReadOnlyCollection<int>? pins = null, CancellationToken cancellationToken = default) =>
        db.DayPunchPairings
            .Where(p => p.Date >= start && p.Date <= end)
            .Where(p => pins == null || pins.Contains(p.EmployeeId))
            .Include(p => p.Slots)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<DayPunchPairing?> GetAsync(int employeePin, DateOnly date,
        CancellationToken cancellationToken = default) =>
        db.DayPunchPairings
            .Where(p => p.EmployeeId == employeePin && p.Date == date)
            .Include(p => p.Slots)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Upsert keyed on the (EmployeeId, Date) unique index (see ScheduleDbContext).
    /// Slots are replaced wholesale rather than diffed: the editor always hands
    /// over the complete intended layout for the day, so working out which
    /// individual slots moved would be strictly more code for the same result.
    /// Tracked (no AsNoTracking) unlike the two reads above, since this one
    /// actually writes the loaded graph back.
    /// </summary>
    public async Task SaveAsync(DayPunchPairing pairing, CancellationToken cancellationToken = default)
    {
        var existing = await db.DayPunchPairings
            .Include(p => p.Slots)
            .FirstOrDefaultAsync(
                p => p.EmployeeId == pairing.EmployeeId && p.Date == pairing.Date, cancellationToken);

        var editedAt = DateTime.UtcNow;

        if (existing is null)
        {
            db.DayPunchPairings.Add(new DayPunchPairing
            {
                EmployeeId = pairing.EmployeeId,
                Date = pairing.Date,
                EditedBy = pairing.EditedBy,
                EditedAt = editedAt,
                Slots = pairing.Slots.Select(CopySlot).ToList(),
            });
        }
        else
        {
            existing.EditedBy = pairing.EditedBy;
            existing.EditedAt = editedAt;

            // RemoveRange on the loaded children (rather than ExecuteDelete)
            // keeps this inside the same SaveChangesAsync as the inserts below,
            // so a save is all-or-nothing -- a day never ends up with its old
            // slots gone and its new ones not yet written.
            db.DayPunchPairingSlots.RemoveRange(existing.Slots);
            existing.Slots = pairing.Slots.Select(CopySlot).ToList();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int employeePin, DateOnly date, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync rather than Find-then-Remove -- one round trip, and a
        // no-op (rather than a NullReferenceException) if there was never an
        // override for this day, which is the common case for "Reset to
        // Automatic". Slots go with it via the cascade (see ScheduleDbContext).
        await db.DayPunchPairings
            .Where(p => p.EmployeeId == employeePin && p.Date == date)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // Id/DayPunchPairingId deliberately left at 0 -- these are always new rows,
    // whether the parent is new or being re-slotted, and EF assigns both.
    private static DayPunchPairingSlot CopySlot(DayPunchPairingSlot slot) => new()
    {
        PunchId = slot.PunchId,
        IsManualPunch = slot.IsManualPunch,
        SegmentIndex = slot.SegmentIndex,
        Role = slot.Role,
    };
}
