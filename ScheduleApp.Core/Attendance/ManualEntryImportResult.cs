namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Outcome of one IManualAttendanceLogRepository.AddRangeAsync call -- mirrors
/// PunchRecordImportResult's role for IAttendanceLogRepository.AddLogsAsync, so
/// re-importing a manual-entries workbook that's (wholly or partly) already on
/// file reports back as visibly different from an import that actually added
/// something, instead of both looking identical to whoever clicked Import.
/// </summary>
public class ManualEntryImportResult
{
    public int TotalInFile { get; init; }
    public int NewRecords { get; init; }
    public int DuplicateRecords { get; init; }
}
