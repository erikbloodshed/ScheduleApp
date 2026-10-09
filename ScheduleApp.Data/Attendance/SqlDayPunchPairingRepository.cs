using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Queries;

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
        GetWhereAsync(
            $"WHERE p.Date >= @start AND p.Date <= @end{(pins is null ? "" : $" AND p.EmployeeId IN ({DapperReads.IdsTable})")}",
            new { start, end, ids = pins is null ? null : DapperReads.IdList(pins) },
            cancellationToken);

    public async Task<DayPunchPairing?> GetAsync(int employeePin, DateOnly date,
        CancellationToken cancellationToken = default) =>
        (await GetWhereAsync("WHERE p.EmployeeId = @employeePin AND p.Date = @date",
            new { employeePin, date }, cancellationToken)).FirstOrDefault();

    /// <summary>The pairings <paramref name="filter"/> (a WHERE over <c>p</c>) selects, in Id
    /// order, each with its slots (in Id order) stitched on by a second query.</summary>
    private Task<List<DayPunchPairing>> GetWhereAsync(string filter, object parameters,
        CancellationToken cancellationToken) =>
        db.ReadAsync(async () =>
        {
            var pairings = await db.QueryAsync<DayPunchPairing>(
                $"SELECT {Columns.Of<DayPunchPairing>("p")} FROM DayPunchPairings p {filter} ORDER BY p.Id",
                parameters, cancellationToken);
            if (pairings.Count == 0) return pairings;

            var slots = await db.QueryAsync<DayPunchPairingSlot>(
                $"""
                SELECT {Columns.Of<DayPunchPairingSlot>("s")} FROM DayPunchPairingSlots s
                WHERE s.DayPunchPairingId IN ({DapperReads.IdsTable})
                ORDER BY s.Id
                """,
                new { ids = DapperReads.IdList(pairings.Select(p => p.Id)) }, cancellationToken);

            var byPairing = slots.ToLookup(s => s.DayPunchPairingId);
            foreach (var pairing in pairings)
            {
                pairing.Slots = [.. byPairing[pairing.Id]];
                foreach (var slot in pairing.Slots)
                    slot.DayPunchPairing = pairing;
            }

            return pairings;
        }, cancellationToken);

    /// <summary>
    /// Upsert keyed on the (EmployeeId, Date) unique index (see ScheduleDbContext).
    /// Slots are replaced wholesale rather than diffed: the editor always hands
    /// over the complete intended layout for the day, so working out which
    /// individual slots moved would be strictly more code for the same result.
    /// Tracked, through EF, unlike the two Dapper reads above, since this one
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
                Slots = [.. pairing.Slots.Select(CopySlot)],
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
            existing.Slots = [.. pairing.Slots.Select(CopySlot)];
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
