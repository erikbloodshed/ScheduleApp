using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs Add/Edit/Delete on a manual attendance entry -- a third way to get a
/// punch on record, alongside AttendanceImportViewModel's Import… and
/// DeviceFetchViewModel's Fetch from Device…, for the case neither of those has anything
/// to import, because the employee simply never punched at all. Writes to
/// ManualAttendanceLogs (via ManualLogEntryDialog + IManualAttendanceLogRepository), not
/// AttendanceLogs -- see ManualAttendanceLog's doc comment for why that's a separate
/// table rather than a third AddLogsAsync source.
///
/// A successful Add/Edit/Delete needs to be reflected in the Manual Entries grid if it's
/// currently showing something, so ManualEntriesViewModel is injected directly here rather
/// than through an event -- this is a small, fixed, tightly-coupled collaborator (not a
/// general pub/sub need), and AttendanceViewModel already constructs both in a fixed
/// order. Punch Records is deliberately not notified here: it never shows manual entries
/// (see PunchRecordsViewModel's doc comment), so an Add/Edit/Delete here never affects
/// it.</summary>
public partial class ManualEntryEditorViewModel : ObservableObject
{
    private readonly IManualAttendanceLogRepository _manualAttendanceLogRepository;

    /// <summary>Passed straight through to ManualLogEntryDialog -- see its own
    /// RefreshMachinePunchesAsync -- so the dialog can show the day's device
    /// punches alongside the manual entry being added/edited. Never read
    /// directly here; this class's own job is Add/Edit/Delete against
    /// IManualAttendanceLogRepository, not device punches.</summary>
    private readonly IAttendanceLogRepository _attendanceLogRepository;

    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;
    private readonly ManualEntriesViewModel _manualEntries;

    public ManualEntryEditorViewModel(
        IManualAttendanceLogRepository manualAttendanceLogRepository,
        IAttendanceLogRepository attendanceLogRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        AttendanceEmployeeDirectory employeeDirectory,
        ManualEntriesViewModel manualEntries)
    {
        _manualAttendanceLogRepository = manualAttendanceLogRepository;
        _attendanceLogRepository = attendanceLogRepository;
        _statusBarService = statusBarService;
        _busy = busy;
        _dataVersion = dataVersion;
        _employeeDirectory = employeeDirectory;
        _manualEntries = manualEntries;

        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AttendanceBusyState.IsRunning)) return;
            AddManualEntryCommand.NotifyCanExecuteChanged();
            EditManualEntryCommand.NotifyCanExecuteChanged();
            DeleteManualEntryCommand.NotifyCanExecuteChanged();

            // Also lets MainViewModel.CanAddManualEntryForDay react -- see IsAttendanceBusy's
            // own doc comment for why that's a read, not a merge, of this instance's busy
            // state.
            OnPropertyChanged(nameof(IsAttendanceBusy));
        };
    }

    /// <summary>Read-only window onto this class's own (AttendanceViewModel-owned)
    /// AttendanceBusyState.IsRunning -- exists so MainViewModel.CanAddManualEntryForDay
    /// can fold it in (see that CanExecute's own doc comment) without MainViewModel ever
    /// holding a reference to AttendanceViewModel's AttendanceBusyState instance itself,
    /// and without the two pages' separate instances being merged into one -- they stay
    /// exactly as separate, for exactly the same reasons, as App.xaml.cs's registration
    /// of both already explains. This is deliberately the one property MainViewModel
    /// reads off this class for that purpose (rather than, say, exposing _busy itself
    /// publicly): a bool is the smallest surface that closes the gap, and it can't be
    /// used to accidentally start or cancel an operation on AttendanceViewModel's busy
    /// state from over here.
    ///
    /// PropertyChanged for this fires from the same _busy.PropertyChanged subscription
    /// above, right alongside the three command re-evaluations already there, so a
    /// subscriber never sees IsRunning change without also seeing this change.</summary>
    public bool IsAttendanceBusy => _busy.IsRunning;

    [RelayCommand(CanExecute = nameof(CanAddManualEntry))]
    private Task AddManualEntryAsync() => AddOrEditManualEntryAsync(existingLog: null);

    private bool CanAddManualEntry() => !_busy.IsRunning;

    /// <summary>Bound to the Edit button on a manual row in the Manual Entries grid (see
    /// AttendanceView.xaml; Punch Records has no such button, since it never shows manual
    /// rows -- see PunchRecordsViewModel's doc comment). Reopens ManualLogEntryDialog
    /// prefilled via its Edit-mode constructor, via the shared AddOrEditManualEntryAsync
    /// below. row.IsManual is checked here regardless, since a CommandParameter binding
    /// can't itself guarantee the button that produced it was actually enabled.</summary>
    [RelayCommand(CanExecute = nameof(CanEditManualEntry))]
    private Task EditManualEntryAsync(StoredPunchLogRow row)
    {
        if (!row.IsManual)
            return Task.CompletedTask;

        return AddOrEditManualEntryAsync(new ManualAttendanceLog
        {
            Id = row.Id,
            EmployeeId = row.EmployeeId,
            Timestamp = row.Timestamp,
            PunchType = row.PunchTypeText == "Clock In" ? 0 : 1,
            Reason = row.Reason ?? string.Empty,
            EnteredBy = row.EnteredBy ?? string.Empty,
        });
    }

    private bool CanEditManualEntry(StoredPunchLogRow row) => !_busy.IsRunning;

    /// <summary>Entry point for the calendar's right-click "Add Manual Entry…" command --
    /// see MainViewModel.AddManualEntryForDayCommand, the only caller. Opens
    /// ManualLogEntryDialog via its calendar-tile constructor (full roster, prefilled
    /// from -- but no longer locked to -- the employee/date the tile was right-clicked
    /// for; see that constructor's own doc comment) instead of the blank/Today-defaulted
    /// one AddManualEntryAsync above uses, then runs through the exact same AddAsync ->
    /// BumpManualLogs -> status message -> RefreshIfLoadedAsync path via
    /// AddOrEditManualEntryAsync below -- so a manual entry added this way also refreshes
    /// the Attendance tab's Manual Entries grid if it's open, for free, without a second,
    /// separately-maintained save path to keep in sync.
    ///
    /// Not [RelayCommand]-attributed like AddManualEntryAsync/EditManualEntryAsync above
    /// -- this isn't bound to anything on this class's own consumer (the Attendance tab).
    /// MainViewModel.AddManualEntryForDayCommand is the actual ICommand the calendar's
    /// context menu binds to, and that command needs its own CanExecute/busy wrapping
    /// against MainViewModel's own _busy (see that command's own doc comment for why,
    /// and for how the two busy states end up doubly-gating this one call) -- this is
    /// just the plain async method it calls into.</summary>
    public Task AddManualEntryForDayAsync(Employee employee, DateOnly date) =>
        AddOrEditManualEntryAsync(existingLog: null, contextual: (employee, date));

    /// <summary>Shared by AddManualEntryAsync (existingLog: null), EditManualEntryAsync
    /// (existingLog: the row being edited, converted to a ManualAttendanceLog), and now
    /// AddManualEntryForDayAsync above (existingLog: null, contextual: the employee/date
    /// a calendar tile was right-clicked for) -- the first two used to be full,
    /// separately-maintained copies of each other differing only in which dialog
    /// constructor overload was used, AddAsync vs. UpdateAsync, and the "Added"/"Updated"
    /// wording in the resulting message; contextual adds a third dialog constructor
    /// (full roster, prefilled from the clicked tile) to that same branch without
    /// touching anything past the point the dialog closes. existingLog is null for both Add and the contextual case;
    /// everything below branches on that, not on which command called in, so contextual
    /// only ever needs to be consulted once, right where the dialog itself gets picked.</summary>
    private async Task AddOrEditManualEntryAsync(ManualAttendanceLog? existingLog, (Employee Employee, DateOnly Date)? contextual = null)
    {
        // visibly: false -- IsVisiblyRunning is deliberately not set until after the
        // dialog closes (see below): the person could sit on that dialog for a while,
        // and none of that time is actually "busy" in the sense the progress bar means.
        await _busy.RunAsync(visibly: false, async cancellationToken =>
        {
            var employees = await _employeeDirectory.GetAllAsync(cancellationToken);

            var dialog = contextual is { } ctx
                ? new ManualLogEntryDialog(employees, ctx.Employee, ctx.Date, _attendanceLogRepository) { Owner = Application.Current.MainWindow }
                : existingLog is null
                    ? new ManualLogEntryDialog(employees, _attendanceLogRepository) { Owner = Application.Current.MainWindow }
                    : new ManualLogEntryDialog(employees, existingLog, _attendanceLogRepository) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true)
                return;

            // Waits for whichever machine-punches fetch the dialog itself most recently
            // kicked off (see ManualLogEntryDialog.WaitForMachinePunchesFetchAsync's own
            // doc comment) to actually finish before the write below touches the same
            // shared, app-lifetime-scoped ScheduleDbContext. Without this, clicking
            // Add/Save quickly enough -- easiest to hit via the calendar-tile
            // constructor, whose one-shot fetch starts on construction, before the
            // dialog is even shown -- let AddAsync/UpdateAsync below start a second,
            // genuinely concurrent operation on that same DbContext while the fetch's
            // own GetLogsAsync call was still in flight, which EF Core doesn't support
            // and throws on. A no-op in the overwhelmingly common case where the fetch
            // (reference-only, and usually fast) has already finished by the time the
            // person gets through the rest of the form.
            await dialog.WaitForMachinePunchesFetchAsync();

            _busy.IsVisiblyRunning = true; // always an explicit click, never a silent auto-load

            var log = new ManualAttendanceLog
            {
                Id = existingLog?.Id ?? 0, // ignored by AddAsync; only UpdateAsync keys off it
                EmployeeId = dialog.EmployeeId,
                Timestamp = dialog.Timestamp,
                PunchType = dialog.PunchType,
                Reason = dialog.Reason,
                EnteredBy = dialog.EnteredBy,
            };

            if (existingLog is null)
                await _manualAttendanceLogRepository.AddAsync(log, cancellationToken);
            else
                await _manualAttendanceLogRepository.UpdateAsync(log, cancellationToken);

            // See AttendanceDataVersion's doc comment -- lets ReportViewModel know a
            // manual entry it may have already merged into a report is now stale.
            _dataVersion.BumpManualLogs();

            var employeeName = employees.FirstOrDefault(e => e.Pin == dialog.EmployeeId)?.DisplayName
                ?? $"Employee {dialog.EmployeeId}";
            var punchTypeText = dialog.PunchType == 0 ? "Clock In" : "Clock Out";
            var verb = existingLog is null ? "Added" : "Updated";
            var message = $"{verb} manual {punchTypeText} for {employeeName} at {dialog.Timestamp:MM/dd/yyyy h:mm tt}.";
            _statusBarService.ShowSuccess(message);

            // Refresh Manual Entries if it's currently showing something, so the
            // added/edited entry is visible immediately without a separate Load click --
            // mirrors AttendanceImportViewModel/DeviceFetchViewModel's own effect on
            // AttendanceLogs, just for the parallel manual table. Punch Records never
            // shows manual entries, so there's nothing to refresh there.
            //
            // This is the one place in Attendance where a RunAsync-wrapped operation
            // calls into another one (RefreshIfLoadedAsync -> LoadManualEntriesCoreAsync
            // -> _busy.RunAsync again, nested inside this method's own RunAsync call
            // above) -- see AttendanceBusyState.RunAsync's own doc comment for why that
            // used to tear down IsRunning/IsVisiblyRunning/the CancellationTokenSource
            // out from under this still-running call the moment the inner one finished,
            // and why RunAsync itself (not this call site) is what now guards against it.
            await _manualEntries.RefreshIfLoadedAsync();
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    /// <summary>Bound to the Delete button on a manual row in the Manual Entries grid
    /// (see AttendanceView.xaml; Punch Records has no such button, since it never shows
    /// manual rows -- see PunchRecordsViewModel's doc comment; disabled/absent for a real
    /// device punch there too, since only ScheduleApp's own typed data can be removed
    /// this way; see ManualAttendanceLog's doc comment). row.IsManual is checked again
    /// here regardless, since a CommandParameter binding can't itself guarantee the
    /// button that produced it was actually enabled.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteManualEntry))]
    private async Task DeleteManualEntryAsync(StoredPunchLogRow row)
    {
        if (!row.IsManual)
            return;

        var confirm = MessageBox.Show(
            $"Delete this manual entry for {row.EmployeeName} at {row.Timestamp:MM/dd/yyyy h:mm tt}?",
            "Delete manual entry", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        // Previously missing entirely -- unlike Add/Edit/Load/Export, this method never
        // set _busy.IsRunning, so its DeleteAsync call could run against the shared,
        // app-lifetime-scoped ScheduleDbContext at the same time as any other command
        // (see AttendanceBusyState's doc comment). Same class of bug as the Punch Records
        // autosuggest race (see PunchRecordsViewModel.OnLogViewSearchTextChanged) -- just
        // on a path a keystroke doesn't trigger every time, so less likely to be hit, but
        // no less real. visibly: true -- always an explicit click, never a silent
        // auto-load.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            await _manualAttendanceLogRepository.DeleteAsync(row.Id, cancellationToken);

            // See AttendanceDataVersion's doc comment.
            _dataVersion.BumpManualLogs();

            _manualEntries.RemoveById(row.Id);

            _statusBarService.ShowSuccess("Manual entry deleted.");
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    private bool CanDeleteManualEntry(StoredPunchLogRow row) => !_busy.IsRunning;
}
