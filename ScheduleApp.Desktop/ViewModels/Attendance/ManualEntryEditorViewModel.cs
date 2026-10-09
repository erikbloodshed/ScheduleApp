using System.Reactive.Linq;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs Add/Edit/Delete on a manual attendance entry -- a third way to get a
/// punch on record, alongside AttendanceImportViewModel's Import… and
/// DeviceFetchViewModel's Fetch from Device…, for the case neither of those has anything
/// to import, because the employee simply never punched at all. Writes to
/// ManualAttendanceLogs (via ManualLogEntryViewModel + IManualAttendanceLogRepository), not
/// AttendanceLogs -- see ManualAttendanceLog's doc comment for why that's a separate
/// table rather than a third AddLogsAsync source.
///
/// A successful Add/Edit/Delete needs to be reflected in the Manual Entries grid if it's
/// currently showing something, so ManualEntriesViewModel is injected directly here rather
/// than through an event -- a small, fixed, tightly-coupled collaborator, not a general
/// pub/sub need. Punch Records is deliberately not notified: it never shows manual entries
/// (see PunchRecordsViewModel's doc comment).
///
/// Reached from the Manual Entries page and from the Schedule calendar's "Add Manual Entry…",
/// so both pages answer its interactions.</summary>
public partial class ManualEntryEditorViewModel : ViewModelBase
{
    private readonly IManualAttendanceLogRepository _manualAttendanceLogRepository;

    /// <summary>Handed to ManualLogEntryViewModel, for its reference-only list of the day's
    /// device punches -- never read here.</summary>
    private readonly IAttendanceLogRepository _attendanceLogRepository;

    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;
    private readonly ManualEntriesViewModel _manualEntries;
    private readonly IObservable<bool> _notBusy;

    public ManualEntryEditorViewModel(
        IManualAttendanceLogRepository manualAttendanceLogRepository,
        IAttendanceLogRepository attendanceLogRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        AttendanceEmployeeDirectory employeeDirectory,
        ManualEntriesViewModel manualEntries)
        : base(statusBarService)
    {
        _manualAttendanceLogRepository = manualAttendanceLogRepository;
        _attendanceLogRepository = attendanceLogRepository;
        _busy = busy;
        _dataVersion = dataVersion;
        _employeeDirectory = employeeDirectory;
        _manualEntries = manualEntries;

        _isAttendanceBusyHelper = _busy.WhenAnyValue(b => b.IsRunning).ToProperty(this, x => x.IsAttendanceBusy);
        _notBusy = this.WhenAnyValue(x => x.IsAttendanceBusy).Select(busy => !busy);

        ReportFailuresOf(AddManualEntryCommand, EditManualEntryCommand, DeleteManualEntryCommand);
    }

    /// <summary>Whether the shared busy state is running -- what ScheduleAssignmentViewModel's
    /// "Add Manual Entry…" folds into its own CanExecute, without holding the busy state
    /// itself (a bool can't be used to start or cancel anything by accident).</summary>
    [ObservableAsProperty]
    public partial bool IsAttendanceBusy { get; }

    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private Task AddManualEntryAsync() => AddOrEditManualEntryAsync(existingLog: null);

    /// <summary>The Edit button on a manual row in the Manual Entries grid. row.IsManual is
    /// checked regardless, since a CommandParameter can't guarantee the button that produced
    /// it was actually enabled.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private Task EditManualEntryAsync(StoredPunchLogRow row) => !row.IsManual
        ? Task.CompletedTask
        : AddOrEditManualEntryAsync(new ManualAttendanceLog
        {
            Id = row.Id,
            EmployeeId = row.EmployeeId,
            Timestamp = row.Timestamp,
            PunchType = row.PunchTypeText == "Clock In" ? 0 : 1,
            Reason = row.Reason ?? string.Empty,
            EnteredBy = row.EnteredBy ?? string.Empty,
        });

    /// <summary>The calendar's right-click "Add Manual Entry…" (see ScheduleAssignmentViewModel,
    /// which wraps it in its own command and CanExecute): the dialog prefilled with, and locked
    /// to, the tile's employee and date, then the same save path as Add -- so an entry added
    /// this way refreshes the Manual Entries grid too.</summary>
    public Task AddManualEntryForDayAsync(Employee employee, DateOnly date) =>
        AddOrEditManualEntryAsync(existingLog: null, day: (employee, date));

    /// <summary>Add (existingLog null), Edit, and the calendar tile's add (day set) share
    /// everything past picking how the dialog opens.</summary>
    private async Task AddOrEditManualEntryAsync(ManualAttendanceLog? existingLog, (Employee Employee, DateOnly Date)? day = null)
    {
        // visibly: false until the dialog closes -- the person could sit on it for a while,
        // and none of that time is "busy" in the sense the progress bar means.
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            var employees = await _employeeDirectory.GetAllAsync(cancellationToken);
            var entry = new ManualLogEntryViewModel(employees, _attendanceLogRepository, existingLog, day);
            if (!await ShowDialogAsync(entry) || entry.AcceptedLog is not { } log)
                return;

            // The dialog's own machine-punch fetch may still be reading the shared,
            // app-lifetime ScheduleDbContext (easiest to hit from a calendar tile, whose fetch
            // starts the moment the dialog opens) -- EF Core can't run the write below
            // alongside it. Usually already finished.
            await entry.WaitForMachinePunchesAsync();

            _busy.IsVisiblyRunning = true; // always an explicit click, never a silent auto-load

            if (existingLog is null)
                await _manualAttendanceLogRepository.AddAsync(log, cancellationToken);
            else
                await _manualAttendanceLogRepository.UpdateAsync(log, cancellationToken);

            // Lets ReportViewModel know a manual entry it may have merged into a report is stale.
            _dataVersion.BumpManualLogs();

            var employeeName = employees.FirstOrDefault(e => e.Pin == log.EmployeeId)?.DisplayName
                ?? $"Employee {log.EmployeeId}";
            var verb = existingLog is null ? "Added" : "Updated";
            StatusBar.ShowSuccess(
                $"{verb} manual {PunchTypeLabel.ToText(log.PunchType)} for {employeeName} at {log.Timestamp:MM/dd/yyyy h:mm tt}.");

            // Shows the entry in Manual Entries right away if it's showing something. A RunAsync
            // nested in this one -- RunAsync itself handles riding along on the outer call.
            await _manualEntries.RefreshIfLoadedAsync();
        },
        onError: ex => ShowFailure(ex));
    }

    /// <summary>The Delete button on a manual row in the Manual Entries grid -- only
    /// ScheduleApp's own typed entries can be removed this way, never a device punch (see
    /// ManualAttendanceLog's doc comment).</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task DeleteManualEntryAsync(StoredPunchLogRow row)
    {
        if (!row.IsManual)
            return;

        if (!await ConfirmAsync(
                $"Delete this manual entry for {row.EmployeeName} at {row.Timestamp:MM/dd/yyyy h:mm tt}?",
                "Delete manual entry"))
            return;

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _manualAttendanceLogRepository.DeleteAsync(row.Id, cancellationToken);
            _dataVersion.BumpManualLogs();
            _manualEntries.RemoveById(row.Id);

            StatusBar.ShowSuccess("Manual entry deleted.");
        },
        onError: ex => ShowFailure(ex));
    }
}
