using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Data.Attendance;

/// <summary>
/// Reads and writes punches against ScheduleApp's own database instead of
/// re-parsing a .dat file on every report run. AttendanceLogReader (unchanged)
/// still does the actual file parsing, and ZkTecoAttendanceLogReader does the
/// live-network equivalent -- see AttendanceViewModel's import commands, both
/// of which just call AddLogsAsync once they have a batch of AttendanceLog
/// objects, regardless of source.
/// </summary>
public class SqlAttendanceLogRepository(ScheduleDbContext db) : IAttendanceLogRepository
{
    public Task<List<AttendanceLog>> GetLogsAsync(DateTime rangeStart, DateTime rangeEnd,
        IReadOnlyCollection<int>? pins = null, CancellationToken cancellationToken = default) =>
        db.AttendanceLogs
            .Where(a => a.Timestamp >= rangeStart && a.Timestamp <= rangeEnd)
            .Where(a => pins == null || pins.Contains(a.EmployeeId))
            .OrderBy(a => a.Timestamp)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<PunchRecordImportResult> AddLogsAsync(IReadOnlyList<AttendanceLog> logs,
        CancellationToken cancellationToken = default)
    {
        if (logs.Count == 0)
            return new PunchRecordImportResult { TotalInFile = 0, NewRecords = 0, DuplicateRecords = 0 };

        // One query for existing keys in the batch's own timestamp span, checked in
        // memory from here on, instead of one AnyAsync round trip per punch (the
        // original approach here, which was fine for a manual .dat import of a few
        // hundred rows). ZkTecoAttendanceLogReader's device fetch has no date filter
        // to narrow what it returns -- every "Fetch from Device" pulls the terminal's
        // *entire* stored history, and the real capture that verified this protocol's
        // record layout came back with over 15,000 records. Per-punch round trips at
        // that scale is exactly the "worth revisiting" case this method's comment
        // used to flag.
        //
        // seenKeys does double duty: pre-loaded with what's already in the database,
        // then added to as new rows are staged below, so it also catches a punch
        // repeated twice within the same incoming batch (ZKTeco exports do sometimes
        // repeat a line, e.g. after a device communication retry) -- without that,
        // both occurrences would look "new" against the DB-only set, both get Added,
        // and the unique index would fail the whole SaveChangesAsync, not just the
        // repeat.
        var minTimestamp = logs.Min(l => l.Timestamp);
        var maxTimestamp = logs.Max(l => l.Timestamp);

        var existingKeys = await db.AttendanceLogs
            .Where(a => a.Timestamp >= minTimestamp && a.Timestamp <= maxTimestamp)
            .Select(a => new { a.EmployeeId, a.Timestamp, a.PunchType })
            .ToListAsync(cancellationToken);

        var seenKeys = existingKeys
            .Select(k => (k.EmployeeId, k.Timestamp, k.PunchType))
            .ToHashSet();

        int duplicates = 0;
        var staged = new List<AttendanceLog>(logs.Count);

        foreach (var log in logs)
        {
            var key = (log.EmployeeId, log.Timestamp, log.PunchType);

            // HashSet<T>.Add returns false when the key was already present -- either
            // loaded from the database above, or added earlier in this same loop.
            if (!seenKeys.Add(key))
            {
                duplicates++;
                continue;
            }

            var entity = new AttendanceLog
            {
                EmployeeId = log.EmployeeId,
                Timestamp = log.Timestamp,
                PunchType = log.PunchType,
                Source = log.Source,
                DeviceSerialNumber = log.DeviceSerialNumber,
                ImportedAt = DateTime.UtcNow
            };
            db.AttendanceLogs.Add(entity);
            staged.Add(entity);
        }

        int added;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            added = staged.Count;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // seenKeys above only sees what had already committed at the moment of the
            // SELECT -- it can't see a row a second, genuinely concurrent writer (e.g.
            // ScheduleApp.PushListener writing a punch at the same instant as a
            // Desktop-triggered "Fetch from Device") inserts *after* that SELECT but
            // *before* this SaveChangesAsync commits. SaveChangesAsync wraps the whole
            // batch in one transaction, so a single such collision rolls back every row
            // in `staged`, not just the one that collided -- including punches nobody
            // else was racing to insert. Detach everything and retry one row at a time
            // so only the row(s) that actually lost the race end up counted as
            // duplicates; every other row in the batch still gets inserted.
            foreach (var entity in staged)
            {
                db.Entry(entity).State = EntityState.Detached;
            }

            (added, duplicates) = await RetryOneAtATimeAsync(staged, duplicates, cancellationToken);
        }

        return new PunchRecordImportResult
        {
            TotalInFile = logs.Count,
            NewRecords = added,
            DuplicateRecords = duplicates
        };
    }

    private async Task<(int Added, int Duplicates)> RetryOneAtATimeAsync(
        List<AttendanceLog> staged, int duplicatesSoFar, CancellationToken cancellationToken)
    {
        var added = 0;
        var duplicates = duplicatesSoFar;

        foreach (var entity in staged)
        {
            db.AttendanceLogs.Add(entity);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                added++;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                db.Entry(entity).State = EntityState.Detached;
                duplicates++;
            }
        }

        return (added, duplicates);
    }

    /// <summary>
    /// SQL Server error 2601 ("Cannot insert duplicate key row" -- the unique index
    /// case used here) or 2627 (UNIQUE/PK constraint) -- the same check
    /// AdmsServer's AttendanceStore.TryRecordAsync used against its own,
    /// now-retired table.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sqlEx && sqlEx.Number is 2601 or 2627;
}
