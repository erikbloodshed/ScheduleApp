namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Abstraction over ScheduleApp's own ManualAttendanceLogs table (see
/// SqlManualAttendanceLogRepository in ScheduleApp.Data), kept separate from
/// IAttendanceLogRepository since manual entries are a different table with a
/// different shape (Reason/EnteredBy) -- see ManualAttendanceLog's doc comment
/// for why. AttendanceWorkflowService and AttendanceViewModel's Punch Records
/// query both depend on this alongside IAttendanceLogRepository and merge the
/// two (via ManualAttendanceLog.ToAttendanceLog()) rather than either one
/// replacing the other.
/// </summary>
public interface IManualAttendanceLogRepository
{
    /// <summary>Manual entries with Timestamp in [rangeStart, rangeEnd],
    /// ordered by Timestamp -- same range-query shape as
    /// IAttendanceLogRepository.GetLogsAsync, so both can be fetched over the
    /// same padded period and merged.</summary>
    /// <param name="pins">Same meaning and default as
    /// IAttendanceLogRepository.GetLogsAsync's own pins parameter -- restricts to
    /// entries whose EmployeeId (matching Employee.Pin) is in this set.</param>
    Task<List<ManualAttendanceLog>> GetLogsAsync(DateTime rangeStart, DateTime rangeEnd,
        IReadOnlyCollection<int>? pins = null, CancellationToken cancellationToken = default);

    /// <summary>Every manual entry ever recorded, most recent first. Not currently
    /// wired to any tab -- the Manual Entries tab is date-scoped now, the same as Punch
    /// Records, and uses GetLogsAsync instead (see ManualEntriesViewModel). Kept as a
    /// general repository capability rather than removed, since "give me everything"
    /// is still a reasonable thing a future caller (a full-database export, a data
    /// migration, a test) might need without re-deriving it from GetLogsAsync with an
    /// arbitrarily wide range.</summary>
    Task<List<ManualAttendanceLog>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists one manual entry (Id/CreatedAt are assigned here,
    /// same as IAttendanceLogRepository.AddLogsAsync does for Id/ImportedAt)
    /// and returns it back with those set.</summary>
    Task<ManualAttendanceLog> AddAsync(ManualAttendanceLog log, CancellationToken cancellationToken = default);

    /// <summary>Persists every one of the given manual entries in one batch -- the
    /// bulk counterpart to AddAsync above, used by the Manual Entries tab's
    /// "Import…" command (see ManualEntriesViewModel.ImportManualEntriesAsync) to
    /// add every row read from an Excel workbook at once rather than one
    /// AddAsync round trip per row. Id/CreatedAt are assigned here, same as
    /// AddAsync.
    ///
    /// Dedups against what's already on file by (EmployeeId, Timestamp,
    /// PunchType) -- same identity AttendanceLogs' own unique index enforces for
    /// a device punch (see IAttendanceLogRepository.AddLogsAsync) -- so
    /// re-importing the same workbook, or one that overlaps an earlier import,
    /// doesn't pile up a second row for a punch already recorded. Unlike
    /// AddLogsAsync this is an in-app check only: ManualAttendanceLogs still has
    /// no database-level uniqueness constraint (see ScheduleDbContext's own
    /// remark on its mapping), and AddAsync above is deliberately left out of
    /// this -- a single entry typed by hand through the dialog is still never
    /// rejected as a duplicate, only caught and deleted by the person afterward
    /// if it turns out to be one (see DeleteAsync below). Returns how many of
    /// the given rows were actually new vs. already present, so the caller can
    /// tell the person "N new, M already on file" rather than both cases looking
    /// like the same silent success.</summary>
    Task<ManualEntryImportResult> AddRangeAsync(IReadOnlyList<ManualAttendanceLog> logs,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing manual entry's editable fields
    /// (Employee, Timestamp, PunchType, Reason, EnteredBy) in place --
    /// CreatedAt is left untouched, since that's when the row was originally
    /// written, not when it was last corrected. No-op if the id doesn't
    /// exist, same as DeleteAsync.</summary>
    Task UpdateAsync(ManualAttendanceLog log, CancellationToken cancellationToken = default);

    /// <summary>Removes a manual entry, e.g. one entered by mistake. There's
    /// no equivalent for a real device punch -- AttendanceLogs is meant to
    /// stay an untouched record of what the clock reported -- but a manual
    /// entry is just this app's own typed data, so correcting it by deleting
    /// and re-adding is fine. No-op if the id doesn't exist.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
