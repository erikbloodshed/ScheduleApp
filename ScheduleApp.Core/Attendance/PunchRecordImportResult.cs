namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Outcome of one IAttendanceLogRepository.AddLogsAsync call -- reported back
/// to the UI so "nothing changed" (e.g. re-importing the same file twice) is
/// visibly different from an actual import of new punches, rather than both
/// silently succeeding the same way.
/// </summary>
public class PunchRecordImportResult
{
    public int TotalInFile { get; init; }
    public int NewRecords { get; init; }
    public int DuplicateRecords { get; init; }
}
