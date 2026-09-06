namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Abstraction over both reading and persisting punch logs, backed by
/// ScheduleApp's own database (see SqlAttendanceLogRepository in
/// ScheduleApp.Data) rather than re-parsing the .dat file on every report run.
///
/// AddLogsAsync is what a file import calls after parsing a .dat export (see
/// AttendanceViewModel), and is also where a future ADMS live-push listener
/// would land new punches as they arrive -- neither AttendanceWorkflowService
/// nor the WPF app need to change either way, since they only ever depend on
/// this interface, never on how punch logs actually got here.
/// </summary>
public interface IAttendanceLogRepository
{
    /// <summary>Punches with Timestamp in [rangeStart, rangeEnd], ordered by
    /// Timestamp. See AttendanceWorkflowService for how the range is padded
    /// around a report period so shift-matching near the period's edges still
    /// works.</summary>
    /// <param name="pins">When non-null, restricts to punches whose EmployeeId
    /// (the punch clock's own employee code -- see AttendanceLog.EmployeeId's own
    /// doc comment) is in this set. Null (the default) means every employee, same
    /// as before this parameter existed.</param>
    Task<List<AttendanceLog>> GetLogsAsync(DateTime rangeStart, DateTime rangeEnd,
        IReadOnlyCollection<int>? pins = null, CancellationToken cancellationToken = default);

    /// <summary>Persists any of the given logs not already stored, matched by
    /// EmployeeId + Timestamp + PunchType (see ScheduleDbContext's unique
    /// index on AttendanceLog). Safe to call repeatedly with overlapping or
    /// duplicate data -- e.g. re-importing a cumulative .dat export -- since
    /// only genuinely new punches get inserted.</summary>
    Task<PunchRecordImportResult> AddLogsAsync(IReadOnlyList<AttendanceLog> logs,
        CancellationToken cancellationToken = default);
}
