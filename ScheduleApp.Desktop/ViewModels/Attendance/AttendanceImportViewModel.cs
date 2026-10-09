using System.IO;
using System.Reactive.Linq;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs the "Import Punch Log" action -- reads a .dat file exported from the
/// ZKTeco terminal to USB and upserts its punches into ScheduleApp's own database
/// (idempotent -- re-importing an overlapping export is safe, see
/// IAttendanceLogRepository.AddLogsAsync). One of three ways to get a punch on record,
/// alongside DeviceFetchViewModel (pulls the same data over the network instead) and
/// ManualEntryEditorViewModel (typed in by hand when neither has anything to import) --
/// all three end up calling AddLogsAsync/AddAsync, so re-running any one after another is
/// safe; a punch already on file from one path is just a duplicate from another's
/// perspective.</summary>
public partial class AttendanceImportViewModel : ViewModelBase
{
    private readonly IAttendanceLogRepository _attendanceLogRepository;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;

    private readonly IObservable<bool> _canImportPunchLog;

    public AttendanceImportViewModel(
        IAttendanceLogRepository attendanceLogRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        string? initialLogDatFile)
        : base(statusBarService)
    {
        _attendanceLogRepository = attendanceLogRepository;
        _busy = busy;
        _dataVersion = dataVersion;

        LogDatFilePath = initialLogDatFile ?? string.Empty;

        _canImportPunchLog = _busy.WhenAnyValue(b => b.IsRunning).Select(isRunning => !isRunning);

        ReportFailuresOf(ImportPunchLogCommand);
    }

    [Reactive]
    public partial string LogDatFilePath { get; set; } = string.Empty;

    /// <summary>Asks for the .dat file (starting from the last one), then imports it. Enabled
    /// while nothing else on the shared busy state is running.</summary>
    [ReactiveCommand(CanExecute = nameof(_canImportPunchLog))]
    private async Task ImportPunchLogAsync()
    {
        if (await PickFileToOpenAsync("Punch log (*.dat)|*.dat|All files (*.*)|*.*", LogDatFilePath) is not { } path)
            return;

        LogDatFilePath = path;

        // visibly: true -- always an explicit click, never a silent auto-load.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // Source defaults to File on AttendanceLog itself, so nothing to set here --
            // see AttendanceLog's doc comment for what that default is standing in for.
            // Reading the file isn't itself cancellable, so a Cancel click here only
            // ever lands before AddLogsAsync persists anything, never partway through
            // the read.
            var logs = AttendanceLogReader.ReadAttendanceLogs(LogDatFilePath);
            var result = await _attendanceLogRepository.AddLogsAsync(logs, cancellationToken);

            // See AttendanceDataVersion's doc comment -- lets PunchRecordsViewModel/
            // ReportViewModel know their own currently-displayed data may now be stale,
            // so their auto-reload-on-tab-select actually re-queries instead of
            // silently trusting a grid that no longer reflects what's in AttendanceLogs.
            // Only when something actually landed -- re-importing a file whose punches
            // are all already on file (see PunchRecordImportResult's own doc comment)
            // genuinely changed nothing, so there's nothing for either tab to go stale
            // over.
            if (result.NewRecords > 0)
                _dataVersion.BumpDeviceLogs();

            var message =
                $"Imported {result.NewRecords} new punch(es) from {Path.GetFileName(LogDatFilePath)} " +
                $"({result.DuplicateRecords} already on file, {result.TotalInFile} total in the file).";
            StatusBar.ShowSuccess(message);
        },
        onError: ex => ShowFailure(ex));
    }
}
