using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Data.Attendance;

/// <summary>
/// Reads and writes ScheduleApp's own ManualAttendanceLogs table -- the
/// separate-from-AttendanceLogs home for hand-typed corrections to a
/// forgotten punch (see ManualAttendanceLog's doc comment for why it's a
/// different table). Registered in App.xaml.cs alongside
/// SqlAttendanceLogRepository.
/// </summary>
public class SqlManualAttendanceLogRepository(ScheduleDbContext db) : IManualAttendanceLogRepository
{
    public Task<List<ManualAttendanceLog>> GetLogsAsync(DateTime rangeStart, DateTime rangeEnd,
        IReadOnlyCollection<int>? pins = null, CancellationToken cancellationToken = default) =>
        db.ManualAttendanceLogs
            .Where(m => m.Timestamp >= rangeStart && m.Timestamp <= rangeEnd)
            .Where(m => pins == null || pins.Contains(m.EmployeeId))
            .OrderBy(m => m.Timestamp)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<List<ManualAttendanceLog>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.ManualAttendanceLogs
            .OrderByDescending(m => m.Timestamp)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<ManualAttendanceLog> AddAsync(ManualAttendanceLog log, CancellationToken cancellationToken = default)
    {
        var entry = new ManualAttendanceLog
        {
            EmployeeId = log.EmployeeId,
            Timestamp = log.Timestamp,
            PunchType = log.PunchType,
            Reason = log.Reason,
            EnteredBy = log.EnteredBy,
            CreatedAt = DateTime.UtcNow
        };

        db.ManualAttendanceLogs.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        return entry;
    }

    public async Task<ManualEntryImportResult> AddRangeAsync(IReadOnlyList<ManualAttendanceLog> logs,
        CancellationToken cancellationToken = default)
    {
        if (logs.Count == 0)
            return new ManualEntryImportResult { TotalInFile = 0, NewRecords = 0, DuplicateRecords = 0 };

        // Same "load the existing keys in the batch's own timestamp span once, then
        // check in memory" shape as SqlAttendanceLogRepository.AddLogsAsync -- see
        // that method's own comment for why this beats one round trip per row.
        // seenKeys does the same double duty there: pre-loaded with what's already
        // in ManualAttendanceLogs, then grown as rows are staged below, so a row
        // repeated twice within the same imported workbook (e.g. someone pasted the
        // same block twice while building it) is caught too, not just one that
        // collides with an earlier import.
        //
        // Unlike AttendanceLogs, ManualAttendanceLogs has no unique index backing
        // this (see ScheduleDbContext) -- so, also unlike AddLogsAsync, there's no
        // DbUpdateException retry path here for a genuinely concurrent insert
        // landing between this SELECT and SaveChangesAsync below. That gap is fine
        // for this table specifically: unlike AttendanceLogs (which PushListener can
        // also be writing to at the same moment as a Desktop-triggered device
        // fetch), nothing else in this app writes to ManualAttendanceLogs except
        // this same UI, one explicit Import click at a time.
        var minTimestamp = logs.Min(l => l.Timestamp);
        var maxTimestamp = logs.Max(l => l.Timestamp);

        var existingKeys = await db.ManualAttendanceLogs
            .Where(m => m.Timestamp >= minTimestamp && m.Timestamp <= maxTimestamp)
            .Select(m => new { m.EmployeeId, m.Timestamp, m.PunchType })
            .ToListAsync(cancellationToken);

        var seenKeys = existingKeys
            .Select(k => (k.EmployeeId, k.Timestamp, k.PunchType))
            .ToHashSet();

        // One shared timestamp for the whole batch, same as AddAsync's own single
        // DateTime.UtcNow call -- every row in one Import click was, as far as
        // CreatedAt cares, written at the same moment.
        var createdAt = DateTime.UtcNow;

        var duplicates = 0;
        var entries = new List<ManualAttendanceLog>(logs.Count);

        foreach (var log in logs)
        {
            var key = (log.EmployeeId, log.Timestamp, log.PunchType);

            // HashSet<T>.Add returns false when the key was already present --
            // either loaded from the database above, or added earlier in this same
            // loop.
            if (!seenKeys.Add(key))
            {
                duplicates++;
                continue;
            }

            entries.Add(new ManualAttendanceLog
            {
                EmployeeId = log.EmployeeId,
                Timestamp = log.Timestamp,
                PunchType = log.PunchType,
                Reason = log.Reason,
                EnteredBy = log.EnteredBy,
                CreatedAt = createdAt
            });
        }

        if (entries.Count > 0)
        {
            db.ManualAttendanceLogs.AddRange(entries);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ManualEntryImportResult
        {
            TotalInFile = logs.Count,
            NewRecords = entries.Count,
            DuplicateRecords = duplicates
        };
    }

    public async Task UpdateAsync(ManualAttendanceLog log, CancellationToken cancellationToken = default)
    {
        // ExecuteUpdateAsync rather than Find-then-modify-then-SaveChanges --
        // same one-round-trip approach as DeleteAsync below, and a no-op
        // (rather than a NullReferenceException) if the id is already gone.
        // CreatedAt is deliberately not in the SetProperty list -- see the
        // interface doc comment.
        await db.ManualAttendanceLogs
            .Where(m => m.Id == log.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.EmployeeId, log.EmployeeId)
                .SetProperty(m => m.Timestamp, log.Timestamp)
                .SetProperty(m => m.PunchType, log.PunchType)
                .SetProperty(m => m.Reason, log.Reason)
                .SetProperty(m => m.EnteredBy, log.EnteredBy), cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync rather than Find-then-Remove -- one round trip, and a
        // no-op (rather than a NullReferenceException) if the id is already gone,
        // e.g. a double click on the grid's Delete button.
        await db.ManualAttendanceLogs
            .Where(m => m.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
