using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs the Manual Entries sub-tab -- a dedicated view of ManualAttendanceLogs
/// alone. Scoped by date range the same way PunchRecordsViewModel is (see LogViewStart/
/// LogViewEnd there and ManualEntriesStart/ManualEntriesEnd here) -- kept as its own
/// ViewModel rather than folded into PunchRecordsViewModel because it queries a
/// different repository and table (ManualAttendanceLogs, not AttendanceLogs) and is
/// deliberately never merged with device punches for display (see
/// PunchRecordsViewModel's doc comment for why the two grids are split).
///
/// Also owns its own Export…, mirroring PunchRecordsViewModel's -- now that the two
/// grids are split, someone who wants a spreadsheet of hand-typed corrections
/// specifically (with Reason/EnteredBy) has this rather than needing Punch Records to
/// carry those columns for a case that's rare and no longer shown there.
///
/// Owns Import… too -- the bulk counterpart to ManualEntryEditorViewModel's
/// single-row Add, for someone with a whole batch of forgotten punches to log at
/// once (e.g. after a device outage) rather than one ManualLogEntryDialog at a
/// time. Reads the same column layout ExportManualLogsToExcel writes (see
/// ManualEntryImporter/ManualEntryImportRow), so exporting this grid and reusing
/// that file as a template is a reasonable way to build one. Lives here rather
/// than alongside AttendanceImportViewModel's device-log Import… because that one
/// reads a different source (a .dat file) into a different table (AttendanceLogs)
/// entirely -- this Import… is paired with this class's own Export…, the same way
/// ScheduleImportExportViewModel pairs Import Schedule…/Export Schedule… together
/// in one class rather than splitting them.</summary>
public partial class ManualEntriesViewModel : ObservableObject
{
    private readonly IManualAttendanceLogRepository _manualAttendanceLogRepository;
    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;
    private readonly Action _saveViewState;
    private readonly AttendanceTabActivationGate _tabActivationGate;

    /// <summary>The date range/ManualLogsVersion combination ManualEntries was actually
    /// loaded for, as of the last successful load -- null until the first one. See
    /// ShouldAutoReload, the only reader.</summary>
    private (DateTime? Start, DateTime? End, int ManualLogsVersion)? _loadedSnapshot;

    public ManualEntriesViewModel(
        IManualAttendanceLogRepository manualAttendanceLogRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        AttendanceEmployeeDirectory employeeDirectory,
        DateTime? initialManualEntriesStart,
        DateTime? initialManualEntriesEnd,
        bool initialIsManualEntriesTabSelected,
        Action saveViewState,
        AttendanceTabActivationGate tabActivationGate)
    {
        _manualAttendanceLogRepository = manualAttendanceLogRepository;
        _statusBarService = statusBarService;
        _busy = busy;
        _dataVersion = dataVersion;
        _employeeDirectory = employeeDirectory;
        _saveViewState = saveViewState;
        _tabActivationGate = tabActivationGate;

        manualEntriesStart = initialManualEntriesStart;
        manualEntriesEnd = initialManualEntriesEnd;
        isManualEntriesTabSelected = initialIsManualEntriesTabSelected;

        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsRunning))
            {
                LoadManualEntriesCommand.NotifyCanExecuteChanged();
                ExportManualEntriesCommand.NotifyCanExecuteChanged();
                ImportManualEntriesCommand.NotifyCanExecuteChanged();
                PreviousPeriodCommand.NotifyCanExecuteChanged();
                NextPeriodCommand.NotifyCanExecuteChanged();
                RefreshOrCancelManualEntriesCommand.NotifyCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(AttendanceBusyState.IsVisiblyRunning))
            {
                // See RefreshOrCancelGlyph/RefreshOrCancelToolTip's own doc comment --
                // this is what flips the toolbar's icon Refresh button between Refresh
                // and Cancel, same mechanism ReportViewModel's own analogous handler
                // uses for the Attendance Summary tab's Refresh/Cancel button.
                OnPropertyChanged(nameof(RefreshOrCancelGlyph));
                OnPropertyChanged(nameof(RefreshOrCancelToolTip));
                RefreshOrCancelManualEntriesCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>Bound to the Manual Entries TabItem's IsSelected -- mirrors
    /// PunchRecordsViewModel.IsPunchRecordsTabSelected's role, just for
    /// LoadManualEntriesCoreAsync.</summary>
    [ObservableProperty]
    private bool isManualEntriesTabSelected;

    partial void OnIsManualEntriesTabSelectedChanged(bool value)
    {
        if (!_tabActivationGate.IsReady) return;

        if (value && CanLoad() && ShouldAutoReload())
            _ = LoadManualEntriesCoreAsync(showFeedback: false);

        _saveViewState();
    }

    /// <summary>True when nothing this tab's own silent auto-reload cares about has
    /// changed since ManualEntries was last successfully loaded (see _loadedSnapshot) --
    /// the date range, or AttendanceDataVersion.ManualLogsVersion (this grid only ever
    /// shows ManualAttendanceLogs, so DeviceLogsVersion changing is irrelevant here).
    /// Checked only by OnIsManualEntriesTabSelectedChanged's silent auto-reload above --
    /// LoadManualEntriesAsync (the explicit Load/Refresh click) always runs regardless.
    /// Mirrors ReportViewModel.ShouldAutoReload/PunchRecordsViewModel.ShouldAutoReload;
    /// see the former's doc comment for why this is what actually stops the grid's
    /// scroll position resetting on an ordinary tab revisit.
    ///
    /// True (i.e. "go ahead and reload") whenever nothing has successfully loaded yet, or
    /// ManualEntriesStart/ManualEntriesEnd aren't validly set -- ValidateManualEntriesRange's
    /// own check inside LoadManualEntriesCoreAsync handles an invalid range correctly
    /// either way.</summary>
    private bool ShouldAutoReload() =>
        _loadedSnapshot != (ManualEntriesStart, ManualEntriesEnd, _dataVersion.ManualLogsVersion);

    [ObservableProperty]
    private int manualEntriesCount;

    [ObservableProperty]
    private bool hasLoadedManualEntries;

    public ObservableCollection<StoredPunchLogRow> ManualEntries { get; } = new();

    [ObservableProperty]
    private DateTime? manualEntriesStart;

    partial void OnManualEntriesStartChanged(DateTime? value) => _saveViewState();

    [ObservableProperty]
    private DateTime? manualEntriesEnd;

    partial void OnManualEntriesEndChanged(DateTime? value) => _saveViewState();

    /// <summary>Backs the Manual Entries tab's own "◀"/"▶" period-nav buttons -- same
    /// AttendancePeriodNavigation.AdjacentCutoffPeriod step ReportViewModel/
    /// PunchRecordsViewModel's own period-nav buttons use, just against
    /// ManualEntriesStart/ManualEntriesEnd. A date edit here doesn't auto-run anything on
    /// its own (see this class's doc comment -- Load/↻ Refresh is always an explicit
    /// action), so this command reloads directly afterward rather than relying on a
    /// property-changed handler to notice; showFeedback: true, same as a direct ↻ Refresh
    /// click, since stepping the period is just as much an explicit action as pressing
    /// it.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task PreviousPeriodAsync()
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(ManualEntriesStart ?? ManualEntriesEnd ?? DateTime.Today, forward: false);
        ManualEntriesStart = start;
        ManualEntriesEnd = end;
        return LoadManualEntriesCoreAsync(showFeedback: true);
    }

    /// <summary>See PreviousPeriodCommand's doc comment -- same step, the other
    /// direction.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task NextPeriodAsync()
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(ManualEntriesStart ?? ManualEntriesEnd ?? DateTime.Today, forward: true);
        ManualEntriesStart = start;
        ManualEntriesEnd = end;
        return LoadManualEntriesCoreAsync(showFeedback: true);
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadManualEntriesAsync() => LoadManualEntriesCoreAsync(showFeedback: true);

    /// <summary>What ManualEntriesView's toolbar button is actually wired to now -- see
    /// ReportViewModel.RefreshOrCancelSummary's own doc comment for the fuller reasoning.
    /// This replaces a separate "Cancel" button that used to sit docked to the right of
    /// this same toolbar row, visible only while _busy.IsVisiblyRunning, with one button
    /// that toggles in place instead of two buttons appearing/disappearing next to each
    /// other -- see RefreshOrCancelGlyph's own doc comment for its current look/placement.
    ///
    /// CanExecute is IsVisiblyRunning (always fine to try to cancel) OR CanLoad() -- needed
    /// because CanLoad() alone would leave the button disabled during a run it didn't
    /// itself start (an Import/Export here, or an Import/Fetch started from Punch Records)
    /// at exactly the moment IsVisiblyRunning makes it look like a live Cancel
    /// button.</summary>
    private bool CanRefreshOrCancelManualEntries() => _busy.IsVisiblyRunning || CanLoad();

    [RelayCommand(CanExecute = nameof(CanRefreshOrCancelManualEntries))]
    private void RefreshOrCancelManualEntries()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            _ = LoadManualEntriesCoreAsync(showFeedback: true);
    }

    /// <summary>Segoe Fluent Icons glyphs for RefreshOrCancelManualEntriesCommand's
    /// button -- see ReportViewModel.RefreshOrCancelGlyph's own doc comment for why these
    /// two specific codepoints (Refresh/Cancel). Icon-only, same IconHeaderActionButton
    /// look as AttendanceSummaryView's own Period-row button, now that this button is
    /// docked to this row's own right edge rather than sitting inline among the other
    /// (text) buttons in the toolbar's left-docked StackPanel.</summary>
    public string RefreshOrCancelGlyph => _busy.IsVisiblyRunning ? "" : "";

    /// <summary>Generic on purpose, not "Stop this load" -- same reasoning as the old
    /// Cancel button's own ToolTip, which this replaces: IsVisiblyRunning can be true
    /// because of literally anything on the Attendance page (an Import/Fetch started from
    /// Punch Records, a manual entry save/delete, or this tab's own Load), not only a
    /// click on this same button.</summary>
    public string RefreshOrCancelToolTip => _busy.IsVisiblyRunning
        ? "Stop whatever's currently running."
        : "Reload manual entries for this period.";

    /// <summary>Exports manual entries in [ManualEntriesStart, ManualEntriesEnd] to Excel
    /// -- same range PunchRecordsViewModel.ExportStoredLogsAsync validates, just against
    /// this tab's own date pickers. Uses AttendanceExcelExporter.ExportManualLogsToExcel
    /// rather than ExportLogsToExcel, so Reason and EnteredBy -- the two columns that
    /// only exist on a manual entry, and the actual point of exporting this grid
    /// separately from Punch Records -- make it into the file.</summary>
    [RelayCommand(CanExecute = nameof(CanExportManualEntries))]
    private async Task ExportManualEntriesAsync()
    {
        var validationError = ValidateManualEntriesRange();
        if (validationError is not null)
        {
            _statusBarService.ShowCaution(validationError);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Manual Entries",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"Manual_Entries_{ManualEntriesStart!.Value:MMddyy}_{ManualEntriesEnd!.Value:MMddyy}.xlsx",
        };

        if (dialog.ShowDialog() != true)
            return;

        // visibly: true -- always an explicit click, never a silent auto-load.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var employees = await _employeeDirectory.GetAllAsync(cancellationToken);
            var entries = await QueryFilteredManualEntriesAsync(cancellationToken);

            // Everything above is cancellable; ExportManualLogsToExcel itself is not, so
            // a Cancel click always lands before any file is written, never partway
            // through one.
            AttendanceExcelExporter.ExportManualLogsToExcel(dialog.FileName, entries, employees);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            _saveViewState();
            _statusBarService.ShowSuccess($"Saved {entries.Count} manual entry(ies) to {dialog.FileName}.");
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    private bool CanExportManualEntries() => !_busy.IsRunning;

    /// <summary>Bulk-adds every row in an Excel workbook to ManualAttendanceLogs in
    /// one go -- see this class's own doc comment for how this relates to
    /// ManualEntryEditorViewModel's single-row Add/Edit and to
    /// AttendanceImportViewModel's unrelated device-log Import…. ManualEntryImporter
    /// validates the whole sheet before this ever touches the repository (see its own
    /// doc comment), so a validation problem (a bad cell, an unknown Id, etc.) either
    /// lands every row in the file or none of them -- there's no partial-import state
    /// to reconcile from a problem in the file itself. A row that's individually
    /// well-formed but already on file is a separate, ordinary case handled by
    /// AddRangeAsync's own dedup rather than treated as a problem here -- see
    /// ManualEntryImportResult's own doc comment for that split.</summary>
    [RelayCommand(CanExecute = nameof(CanImportManualEntries))]
    private async Task ImportManualEntriesAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog() != true)
            return;

        // visibly: true -- always an explicit click, never a silent auto-load.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var employees = await _employeeDirectory.GetAllAsync(cancellationToken);
            var rows = ManualEntryImporter.Import(dialog.FileName, employees);

            var logs = rows.Select(r => new ManualAttendanceLog
            {
                EmployeeId = r.EmployeeId,
                Timestamp = r.Timestamp,
                PunchType = r.PunchType,
                Reason = r.Reason,
                EnteredBy = r.EnteredBy,
            }).ToList();

            var result = await _manualAttendanceLogRepository.AddRangeAsync(logs, cancellationToken);

            // See AttendanceDataVersion's doc comment -- lets ReportViewModel know a
            // manual entry it may have already merged into a report is now stale.
            // Only when something actually landed -- re-importing a file whose rows
            // are all already on file (see ManualEntryImportResult's own doc
            // comment) genuinely changed nothing, so there's nothing to go stale
            // over.
            if (result.NewRecords > 0)
                _dataVersion.BumpManualLogs();

            _statusBarService.ShowSuccess(
                $"Imported {result.NewRecords} new manual entry(ies) from {Path.GetFileName(dialog.FileName)} " +
                $"({result.DuplicateRecords} already on file, {result.TotalInFile} total in the file).");

            // Refresh the grid if it's currently showing something, same effect
            // ManualEntryEditorViewModel's own Add/Edit has via this same method --
            // see RefreshIfLoadedAsync's own doc comment.
            await RefreshIfLoadedAsync();
        },
        onError: ex =>
        {
            if (ex is ManualEntryImportException importEx)
            {
                // Can carry many lines -- one workbook can fail several rows for
                // several different reasons at once -- so unlike the single-line
                // status bar notification below, this uses the same MessageBox
                // surface ScheduleImportExportViewModel.ImportEmployeesAsync already
                // reserves for the same shape of problem (see
                // StatusBarNotificationExtensions' own doc comment for why
                // confirmations/multi-line problem lists stay off the status bar).
                MessageBox.Show(importEx.Message, "Import problems found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Most likely cause: a column layout that doesn't match, or the file is
            // open in Excel (sharing violation).
            _statusBarService.ShowError(
                $"Could not import this file. Check that it matches the expected column layout. {ex.Message}",
                "Import failed");
        });
    }

    private bool CanImportManualEntries() => !_busy.IsRunning;

    /// <summary>Called by ManualEntryEditorViewModel after adding or editing a manual
    /// entry, so a just-added/edited entry shows up immediately if this grid is already
    /// showing something, without forcing a reload of a grid the person hasn't opened
    /// yet.</summary>
    internal Task RefreshIfLoadedAsync() => HasLoadedManualEntries ? LoadManualEntriesCoreAsync(showFeedback: false) : Task.CompletedTask;

    /// <summary>Called by ManualEntryEditorViewModel after deleting a manual entry -- by
    /// Id rather than by row-object identity, since the deleted row object passed to
    /// DeleteManualEntryAsync is whichever StoredPunchLogRow instance the grid built, not
    /// necessarily object-equal to the one held here.</summary>
    internal void RemoveById(int id)
    {
        var match = ManualEntries.FirstOrDefault(r => r.Id == id);
        if (match is null) return;

        ManualEntries.Remove(match);
        ManualEntriesCount = ManualEntries.Count;

        // ManualEntryEditorViewModel already bumped AttendanceDataVersion.ManualLogsVersion
        // before calling this -- fold that bump into _loadedSnapshot here too (rather than
        // leaving it stale until the next full LoadManualEntriesCoreAsync), so
        // ShouldAutoReload correctly sees "nothing left to catch up on" the next time this
        // tab is revisited instead of triggering a redundant re-query for a deletion this
        // in-memory removal has already fully accounted for. Only when something was
        // actually loaded to begin with -- a null snapshot already means "reload for any
        // reason", which a version bump shouldn't change.
        if (_loadedSnapshot is { } snapshot)
            _loadedSnapshot = (snapshot.Start, snapshot.End, _dataVersion.ManualLogsVersion);
    }

    /// <summary>Mirrors PunchRecordsViewModel's showFeedback split -- the Refresh button
    /// always wants a status bar message, the auto-load on switching to this tab (see
    /// OnIsManualEntriesTabSelectedChanged) doesn't.</summary>
    private async Task LoadManualEntriesCoreAsync(bool showFeedback)
    {
        var validationError = ValidateManualEntriesRange();
        if (validationError is not null)
        {
            if (showFeedback)
                _statusBarService.ShowCaution(validationError);
            return;
        }

        // See AttendanceBusyState.IsVisiblyRunning's doc comment -- only the explicit
        // Refresh click should read as "busy" to the person; the silent re-run that fires
        // every time this tab is (re)selected shouldn't visibly flicker anything.
        await _busy.RunAsync(visibly: showFeedback, async cancellationToken =>
        {
            var employees = await _employeeDirectory.GetAllAsync(cancellationToken);
            var entries = await QueryFilteredManualEntriesAsync(cancellationToken);
            var employeeInfo = StoredPunchLogRowFactory.BuildEmployeeInfoByPin(employees);

            ManualEntries.Clear();
            foreach (var entry in entries)
                ManualEntries.Add(StoredPunchLogRowFactory.BuildRow(entry.ToAttendanceLog(), employeeInfo));

            ManualEntriesCount = ManualEntries.Count;
            HasLoadedManualEntries = true;
            _loadedSnapshot = (ManualEntriesStart, ManualEntriesEnd, _dataVersion.ManualLogsVersion);
            _saveViewState();

            if (showFeedback)
                _statusBarService.ShowSuccess($"Found {ManualEntriesCount} manual entry(ies) in range.");
        },
        onError: ex =>
        {
            if (showFeedback)
                _statusBarService.ShowError(ex.Message);
        });
    }

    /// <summary>Shared by Load and Export -- both need exactly the same "manual entries
    /// in [ManualEntriesStart, ManualEntriesEnd]" query, so there's one place that can go
    /// stale rather than two copies drifting apart. Callers must check
    /// ValidateManualEntriesRange() first -- this assumes ManualEntriesStart/
    /// ManualEntriesEnd are already known non-null. Mirrors
    /// PunchRecordsViewModel.QueryFilteredStoredLogsAsync's shape, just against
    /// IManualAttendanceLogRepository.GetLogsAsync instead of GetAllAsync.</summary>
    private Task<List<ManualAttendanceLog>> QueryFilteredManualEntriesAsync(CancellationToken cancellationToken = default)
    {
        var rangeStart = ManualEntriesStart!.Value.Date;
        var rangeEnd = ManualEntriesEnd!.Value.Date.AddDays(1).AddTicks(-1); // inclusive of the whole end day

        return _manualAttendanceLogRepository.GetLogsAsync(rangeStart, rangeEnd, cancellationToken: cancellationToken);
    }

    /// <summary>Cheap, synchronous checks only -- deliberately doesn't touch the
    /// database, so both Load and Export can call it before setting IsRunning, the same
    /// way PunchRecordsViewModel.ValidateLogViewRange works.</summary>
    private string? ValidateManualEntriesRange()
    {
        if (ManualEntriesStart is null || ManualEntriesEnd is null)
            return "⚠ Select both a start and end date.";

        if (ManualEntriesStart > ManualEntriesEnd)
            return "⚠ Start date must not be after end date.";

        return null;
    }

    /// <summary>Internal (not private) so both LoadManualEntriesCommand's CanExecute
    /// wiring and AttendanceViewModel.ActivateInitialTabAsync's initial-tab check can
    /// share the one implementation.</summary>
    internal bool CanLoad() => !_busy.IsRunning;
}
