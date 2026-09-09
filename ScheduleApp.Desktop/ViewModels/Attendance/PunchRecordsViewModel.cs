using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Microsoft.Win32;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs the Punch Records sub-tab -- unrelated to Generate Reports, this just
/// shows (and, via Export, writes to Excel) whatever's currently in AttendanceLogs for a
/// date range, regardless of whether a report has ever been run for it. Mainly for
/// confirming an import/device fetch actually landed, spot-checking what the clock itself
/// reported for someone, or handing someone a punch-log export without also generating
/// (or waiting on) an attendance summary for the same period.
///
/// Deliberately device-only -- ManualAttendanceLogs live entirely on the Manual Entries
/// tab (see ManualEntriesViewModel) instead of being merged in here, so this grid stays a
/// literal, untouched record of what the punch clock reported. Attendance summary
/// generation is the one place the two sources are still combined (see
/// AttendanceWorkflowService), since a manual entry should still be able to fill a gap in
/// the calculated result even though it's never shown alongside device punches here.</summary>
public partial class PunchRecordsViewModel : ObservableObject
{
    private readonly IAttendanceLogRepository _attendanceLogRepository;
    private readonly IStatusBarService _statusBarService;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;
    private readonly Action _saveViewState;
    private readonly AttendanceTabActivationGate _tabActivationGate;

    /// <summary>The date range/DeviceLogsVersion combination StoredLogs was actually
    /// loaded for, as of the last successful load -- null until the first one. See
    /// ShouldAutoReload, the only reader. Deliberately doesn't include LogViewSearchText
    /// -- unlike the date range, the search box no longer needs a database round trip to
    /// take effect at all (see StoredLogsView/FilterStoredLogRow), so it has nothing to
    /// do with whether a *reload* is needed.</summary>
    private (DateTime? Start, DateTime? End, int DeviceLogsVersion)? _loadedSnapshot;

    public PunchRecordsViewModel(
        IAttendanceLogRepository attendanceLogRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        AttendanceEmployeeDirectory employeeDirectory,
        DateTime? initialLogViewStart,
        DateTime? initialLogViewEnd,
        string initialLogViewSearchText,
        bool initialIsPunchRecordsTabSelected,
        Action saveViewState,
        AttendanceTabActivationGate tabActivationGate)
    {
        _attendanceLogRepository = attendanceLogRepository;
        _statusBarService = statusBarService;
        _busy = busy;
        _dataVersion = dataVersion;
        _employeeDirectory = employeeDirectory;
        _saveViewState = saveViewState;
        _tabActivationGate = tabActivationGate;

        logViewStart = initialLogViewStart;
        logViewEnd = initialLogViewEnd;
        logViewSearchText = initialLogViewSearchText;
        isPunchRecordsTabSelected = initialIsPunchRecordsTabSelected;

        // Assigned directly (bypassing ApplySearchValue) for the same reason
        // logViewSearchText above is assigned to its backing field rather than through
        // the property -- StoredLogsView doesn't exist yet at this point in the
        // constructor, so there'd be nothing for a Refresh() to even run against. Set to
        // match initialLogViewSearchText rather than left blank, though: restoring a
        // search box with leftover text that isn't actually applied to the grid would be
        // a worse first impression on relaunch than either fully restoring or fully not
        // restoring.
        _appliedSearchValue = initialLogViewSearchText;

        _busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsRunning))
            {
                LoadStoredLogsCommand.NotifyCanExecuteChanged();
                ExportStoredLogsCommand.NotifyCanExecuteChanged();
                PreviousPeriodCommand.NotifyCanExecuteChanged();
                NextPeriodCommand.NotifyCanExecuteChanged();
                RefreshOrCancelStoredLogsCommand.NotifyCanExecuteChanged();

                // A keystroke that arrived while busy never got a suggestion fetch (see
                // OnLogViewSearchTextChanged below) -- catch up now that the shared
                // ScheduleDbContext is free again, rather than leaving the dropdown
                // empty until the next keystroke.
                if (!_busy.IsRunning && LogViewSearchText.Trim().Length > 0)
                    _ = UpdateLogViewSuggestionsAsync(LogViewSearchText);
            }
            else if (e.PropertyName == nameof(AttendanceBusyState.IsVisiblyRunning))
            {
                // See RefreshOrCancelGlyph/RefreshOrCancelToolTip's own doc comment --
                // this is what flips the toolbar's "Load" button between Load and Cancel,
                // same mechanism ReportViewModel's own analogous handler uses for the
                // Attendance Summary tab's Refresh/Cancel button.
                OnPropertyChanged(nameof(RefreshOrCancelContent));
                OnPropertyChanged(nameof(RefreshOrCancelIcon));
                OnPropertyChanged(nameof(RefreshOrCancelToolTip));
                RefreshOrCancelStoredLogsCommand.NotifyCanExecuteChanged();
            }
        };

        // Mirrors ReportViewModel's SummaryRowsView setup -- see StoredLogsView's own
        // doc comment.
        StoredLogsView = CollectionViewSource.GetDefaultView(StoredLogs);
        StoredLogsView.Filter = FilterStoredLogRow;
    }

    /// <summary>Bound to the Punch Records TabItem's IsSelected -- OnIsPunchRecordsTabSelectedChanged
    /// below auto-runs Load whenever this flips to true, so switching to the tab shows
    /// current data without having to press Load by hand first. Flips back to false when
    /// the person leaves the tab, same as any other TabItem. Set via the backing field in
    /// the constructor above (not the property) so the initial value from saved view
    /// state doesn't trigger the auto-load/save logic below before the tab-activation
    /// gate is even open.</summary>
    [ObservableProperty]
    private bool isPunchRecordsTabSelected;

    partial void OnIsPunchRecordsTabSelectedChanged(bool value)
    {
        if (!_tabActivationGate.IsReady) return;

        if (value && CanLoad() && ShouldAutoReload())
            _ = LoadStoredLogsCoreAsync(showFeedback: false);

        _saveViewState();
    }

    /// <summary>True when nothing this tab's own silent auto-reload cares about has
    /// changed since StoredLogs was last successfully loaded (see _loadedSnapshot) --
    /// the date range, or AttendanceDataVersion.DeviceLogsVersion (this grid is
    /// device-only, so ManualLogsVersion changing is irrelevant here -- see this class's
    /// own doc comment). Deliberately does not consider LogViewSearchText -- see
    /// _loadedSnapshot's own doc comment for why a search-text change alone never needs
    /// a reload. Checked only by OnIsPunchRecordsTabSelectedChanged's silent auto-reload
    /// above -- LoadStoredLogsAsync (the explicit Load/↻ Refresh click) always runs
    /// regardless. Mirrors ReportViewModel.ShouldAutoReload; see that method's doc
    /// comment for why this is what actually stops the grid's scroll position resetting
    /// on an ordinary tab revisit, not just the Clear/rebuild shape inside
    /// LoadStoredLogsCoreAsync.
    ///
    /// True (i.e. "go ahead and reload") whenever nothing has successfully loaded yet, or
    /// LogViewStart/LogViewEnd aren't validly set -- ValidateLogViewRange's own check
    /// inside LoadStoredLogsCoreAsync handles an invalid range correctly either way.</summary>
    private bool ShouldAutoReload() =>
        _loadedSnapshot != (LogViewStart, LogViewEnd, _dataVersion.DeviceLogsVersion);

    [ObservableProperty]
    private DateTime? logViewStart;

    partial void OnLogViewStartChanged(DateTime? value) => _saveViewState();

    [ObservableProperty]
    private DateTime? logViewEnd;

    partial void OnLogViewEndChanged(DateTime? value) => _saveViewState();

    /// <summary>Backs the Punch Records tab's own "◀"/"▶" period-nav buttons -- same
    /// AttendancePeriodNavigation.AdjacentCutoffPeriod step ReportViewModel's Summary tab
    /// buttons use, just against LogViewStart/LogViewEnd instead of PeriodStart/
    /// PeriodEnd. Unlike the Summary tab, a date edit here doesn't auto-run anything on
    /// its own (see this class's doc comment -- Load is always an explicit action), so
    /// this command reloads directly afterward rather than relying on a property-changed
    /// handler to notice; showFeedback: true, same as a direct Load click, since stepping
    /// the period is just as much an explicit action as pressing Load.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task PreviousPeriodAsync()
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(LogViewStart ?? LogViewEnd ?? DateTime.Today, forward: false);
        LogViewStart = start;
        LogViewEnd = end;
        return LoadStoredLogsCoreAsync(showFeedback: true);
    }

    /// <summary>See PreviousPeriodCommand's doc comment -- same step, the other
    /// direction.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task NextPeriodAsync()
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(LogViewStart ?? LogViewEnd ?? DateTime.Today, forward: true);
        LogViewStart = start;
        LogViewEnd = end;
        return LoadStoredLogsCoreAsync(showFeedback: true);
    }

    /// <summary>Free-text filter for the grid below -- one value at a time (an Employee
    /// ID, an employee name, or a department; see FilterStoredLogRow), not a
    /// comma-separated list. Purely a *display* filter, the same role TreeSearchText
    /// plays over on the Report Scope tree (see ReportScopeViewModel/
    /// EmployeeTreeSearchFilter, which does still support comma-separated terms -- this
    /// box deliberately doesn't). Live only in the sense that LogViewSuggestions
    /// recomputes on every keystroke -- it does *not* re-filter StoredLogsView itself on
    /// every keystroke; see _appliedSearchValue's own doc comment for why that's a
    /// separate, more deliberately-triggered step. Never re-queries the database either
    /// way, and never touches what Load/Export actually fetch (see
    /// QueryStoredLogsInRangeAsync).</summary>
    [ObservableProperty]
    private string logViewSearchText = string.Empty;

    /// <summary>What FilterStoredLogRow actually filters StoredLogsView against --
    /// deliberately a separate field from LogViewSearchText above, updated only by
    /// ApplySearchValue below, rather than the grid re-filtering itself on every single
    /// keystroke the way it briefly did. Re-running the filter across every loaded row
    /// on every keystroke was wasted work for most of what's typed -- a person narrowing
    /// down to one person by typing "Cruz" character by character doesn't need (or want)
    /// four separate re-filters against partial fragments "C", "Cr", "Cru" along the way,
    /// only the one search they're actually about to run once they've picked who they
    /// meant from LogViewSuggestions. Clearing the box back to empty is the one exception
    /// -- see OnLogViewSearchTextChanged.</summary>
    private string _appliedSearchValue = string.Empty;

    partial void OnLogViewSearchTextChanged(string value)
    {
        // Skipped while _busy.IsRunning -- UpdateLogViewSuggestionsAsync falls through to
        // AttendanceEmployeeDirectory.GetAllAsync() (a real query) whenever the
        // suggestion cache hasn't been warmed yet, and every other command in Attendance
        // already treats _busy.IsRunning as "something is using the shared, app-lifetime-
        // scoped ScheduleDbContext right now, don't start another operation" (see
        // AttendanceBusyState's doc comment and AttendanceViewModel.InitializeAsync).
        // This fires from a keystroke rather than a command, so it used to be the one
        // path that ignored that guard. Caught up automatically once busy clears -- see
        // the _busy.PropertyChanged handler above -- so typing during, say, a Generate
        // Reports run just delays the dropdown rather than silently dropping it.
        if (!_busy.IsRunning)
            _ = UpdateLogViewSuggestionsAsync(value);

        // Deliberately does *not* call ApplySearchValue for most edits anymore -- see
        // _appliedSearchValue's own doc comment. Clearing the box back to empty is the
        // one exception: that's an unambiguous "show everyone again" action in its own
        // right, not a still-narrowing-down-to-one-person keystroke, so it takes effect
        // immediately rather than sitting there filtered on a search term that's no
        // longer even in the box, waiting for a suggestion pick that will never come.
        if (value.Trim().Length == 0)
            ApplySearchValue(string.Empty);
    }

    /// <summary>The one place that actually changes what StoredLogsView shows -- called
    /// from SelectLogViewSuggestion when a suggestion is picked, and from
    /// OnLogViewSearchTextChanged for the one case (clearing the box) that applies
    /// itself without waiting for a pick.</summary>
    private void ApplySearchValue(string value)
    {
        _appliedSearchValue = value;
        StoredLogsView.Refresh();
    }

    /// <summary>Autosuggestion candidates for whatever's currently typed in
    /// LogViewSearchText -- see UpdateLogViewSuggestionsAsync. Bound to a Popup's
    /// ListBox in AttendanceView.xaml; SelectLogViewSuggestion below is what runs when
    /// one is chosen.</summary>
    public ObservableCollection<PunchSearchSuggestion> LogViewSuggestions { get; } = new();

    [ObservableProperty]
    private bool isLogViewSuggestionsOpen;

    /// <summary>Guards against an older keystroke's suggestions arriving after a newer
    /// one's (both awaiting the same employee fetch) and clobbering what should be on
    /// screen.</summary>
    private int _logViewSuggestionRequestId;

    /// <summary>Recomputes LogViewSuggestions for whatever's currently in the search box
    /// (trimmed), the same term FilterStoredLogRow will match against live. Closes the
    /// suggestion list while the box is empty rather than dumping the entire employee
    /// roster on screen.</summary>
    private async Task UpdateLogViewSuggestionsAsync(string text)
    {
        var term = text.Trim();
        if (term.Length == 0)
        {
            LogViewSuggestions.Clear();
            IsLogViewSuggestionsOpen = false;
            return;
        }

        int requestId = ++_logViewSuggestionRequestId;

        List<Employee> employees;
        try
        {
            employees = await _employeeDirectory.GetForSuggestionsAsync();
        }
        catch
        {
            // Swallow -- suggestions are a convenience, not the source of truth.
            // Load/Export will surface the real error against the same failure.
            return;
        }

        if (requestId != _logViewSuggestionRequestId)
            return; // A newer keystroke already superseded this request.

        LogViewSuggestions.Clear();
        foreach (var match in BuildSuggestionMatches(term, employees))
            LogViewSuggestions.Add(match);

        IsLogViewSuggestionsOpen = LogViewSuggestions.Count > 0;
    }

    /// <summary>One suggestion per matching department, plus one per matching employee --
    /// deliberately not one per matching *field* the way this used to work (a separate
    /// entry for a first name, a last name, and a department, all as bare fragments). A
    /// department candidate's Display/InsertValue are the same (its name is already the
    /// one, complete, unambiguous thing to search for); an employee candidate's Display
    /// adds their department for context/disambiguation, but InsertValue is just their
    /// DisplayName alone -- see PunchSearchSuggestion.InsertValue's own remarks for why
    /// picking one specific person needs a different inserted term than what's shown.
    ///
    /// Employees without an Employee ID (Pin) are skipped entirely, not just from
    /// the ID-match check -- unlike ReportScopeViewModel's tree search, which
    /// deliberately does still surface them (see EmployeeTreeSearchFilter's own
    /// remarks), a punch log can never contain a row for someone who has no ID to punch
    /// in under, so suggesting them here would just be a dead end.
    ///
    /// Capped and alphabetized (departments and employees together) for a manageable,
    /// predictable dropdown.</summary>
    private static List<PunchSearchSuggestion> BuildSuggestionMatches(string term, IReadOnlyList<Employee> employees)
    {
        var suggestions = new List<PunchSearchSuggestion>();
        var seenDepartments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in employees)
        {
            var departmentName = e.Department?.Name;
            if (string.IsNullOrWhiteSpace(departmentName)) continue;
            if (!departmentName.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seenDepartments.Add(departmentName)) continue;

            suggestions.Add(new PunchSearchSuggestion($"{departmentName} (Department)", departmentName));
        }

        foreach (var e in employees)
        {
            var pin = e.Pin;

            bool matches = pin.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.LastName.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;

            var departmentLabel = e.Department?.Name ?? "(Unassigned)";
            suggestions.Add(new PunchSearchSuggestion($"{e.DisplayName} \u2014 {departmentLabel}", e.DisplayName));
        }

        return suggestions
            .OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>Runs when the person picks a suggestion from the dropdown -- this is the
    /// moment the grid actually updates (see ApplySearchValue/_appliedSearchValue's own
    /// doc comments for why that's deferred to here rather than happening on every
    /// keystroke). Also writes the same value into LogViewSearchText, so the box itself
    /// shows what's now applied rather than whatever fragment was last typed, and closes
    /// the dropdown -- there's nothing left to pick once the one thing being searched
    /// for has just been chosen.</summary>
    [RelayCommand]
    private void SelectLogViewSuggestion(PunchSearchSuggestion? suggestion)
    {
        if (suggestion is null) return;

        LogViewSearchText = suggestion.InsertValue;
        ApplySearchValue(suggestion.InsertValue);
        IsLogViewSuggestionsOpen = false;
    }

    [ObservableProperty]
    private int storedLogsCount;

    [ObservableProperty]
    private bool hasLoadedStoredLogs;

    public ObservableCollection<StoredPunchLogRow> StoredLogs { get; } = new();

    /// <summary>The DataGrid binds to this instead of StoredLogs directly, so picking a
    /// search suggestion (or clearing the box -- see ApplySearchValue) hides non-matching
    /// rows without touching the underlying data (needed intact for Export…, which --
    /// like ReportViewModel's Export Summary… -- always exports every punch actually in
    /// range regardless of what's currently filtered on-screen; see
    /// QueryStoredLogsInRangeAsync). Set up once in the constructor via
    /// CollectionViewSource.GetDefaultView(StoredLogs), which returns the *same* view for
    /// that source collection every time, so rows Load adds/clears show up here
    /// automatically with whatever filter is currently active already applied.</summary>
    public ICollectionView StoredLogsView { get; }

    /// <summary>_appliedSearchValue's blank-means-everyone / numeric-means-exact-
    /// Employee-ID / otherwise-substring-against-name-or-department rules, applied to one
    /// already-loaded row. Mirrors EmployeeTreeSearchFilter.EmployeeMatchesSearchTerm's
    /// per-term shape, just for exactly one term instead of several OR'd together -- see
    /// LogViewSearchText's own doc comment for why this box only ever searches one value
    /// at a time. Reads _appliedSearchValue, not LogViewSearchText directly -- see the
    /// former's own doc comment for why those two can briefly disagree while someone's
    /// still typing.</summary>
    private bool FilterStoredLogRow(object obj)
    {
        if (obj is not StoredPunchLogRow row) return false;

        var term = _appliedSearchValue;
        if (term.Length == 0) return true;

        if (int.TryParse(term, out var id))
            return row.EmployeeId == id;

        return row.EmployeeName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.DepartmentName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadStoredLogsAsync() => LoadStoredLogsCoreAsync(showFeedback: true);

    /// <summary>What PunchRecordsView's "Load" button is actually wired to now -- see
    /// ReportViewModel.RefreshOrCancelSummary's own doc comment for the fuller reasoning.
    /// This replaces the separate "Cancel" button that used to sit in this page's own
    /// CardBorder toolbar row (alongside Import…/Fetch from Device), visible only while
    /// _busy.IsVisiblyRunning -- inline in that same row rather than its own, so it never
    /// pushed anything down the way Attendance Summary's old Cancel bar did, but it was
    /// still a second button appearing and disappearing next to Import…/Fetch. One button
    /// per row, toggling in place, is simpler still -- and this one now doubles as the
    /// page's Cancel for Import…/Fetch too, not just its own Load.
    ///
    /// CanExecute is IsVisiblyRunning (always fine to try to cancel) OR CanLoad() -- needed
    /// because CanLoad() alone would leave the button disabled during a run it didn't
    /// itself start (an Import…/Fetch from Device here, or an Import/Fetch/manual entry
    /// action started from another Attendance page) at exactly the moment IsVisiblyRunning
    /// makes it look like a live Cancel button.</summary>
    private bool CanRefreshOrCancelStoredLogs() => _busy.IsVisiblyRunning || CanLoad();

    [RelayCommand(CanExecute = nameof(CanRefreshOrCancelStoredLogs))]
    private void RefreshOrCancelStoredLogs()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            _ = LoadStoredLogsCoreAsync(showFeedback: true);
    }

    /// <summary>Split into a text Content and a separate Icon -- unlike
    /// ReportViewModel.RefreshOrCancelGlyph/ManualEntriesViewModel.RefreshOrCancelGlyph,
    /// each a single Segoe Fluent Icons glyph that *is* an icon-only Button's whole
    /// Content -- because this button is a controls:IconButton (see that control's own
    /// doc comment) showing a short label and an icon side by side, sitting among the
    /// other labeled buttons (◀/▶/Export/Import/Fetch from Device) in this same Period
    /// row rather than standing alone the way the other two pages' buttons do.</summary>
    public string RefreshOrCancelContent => _busy.IsVisiblyRunning ? "Cancel" : "Reload";

    public string RefreshOrCancelIcon => _busy.IsVisiblyRunning ? "\uE711" : "\uE72C"; // Segoe Fluent Icons: Cancel / Refresh

    /// <summary>Generic on purpose, not "Stop this load" -- same reasoning as the old
    /// Cancel button's own ToolTip, which this replaces: IsVisiblyRunning can be true
    /// because of literally anything on the Attendance page (Import…/Fetch from Device
    /// here, or an Import/Fetch/manual entry action started from another Attendance
    /// page), not only a click on this same button.</summary>
    public string RefreshOrCancelToolTip => _busy.IsVisiblyRunning
        ? "Stop whatever's currently running."
        : "Reload stored punches for this period.";

    /// <summary>Does the actual load; showFeedback controls whether the
    /// validation-error/success/error status bar messages fire. The Load button always wants that
    /// feedback -- it's an explicit action the person just took. The auto-load on
    /// switching to this tab (see OnIsPunchRecordsTabSelectedChanged) passes false
    /// instead, since a status bar message popping up on every tab switch is just noise -- the
    /// refreshed grid and "N punch(es) found in range" line are feedback enough.</summary>
    private async Task LoadStoredLogsCoreAsync(bool showFeedback)
    {
        var validationError = ValidateLogViewRange();
        if (validationError is not null)
        {
            if (showFeedback)
                _statusBarService.ShowCaution(validationError);
            return;
        }

        // See AttendanceBusyState.IsVisiblyRunning's doc comment -- only the explicit
        // Load click should read as "busy" to the person; the silent re-run that fires
        // every time this tab is (re)selected shouldn't visibly flicker anything.
        await _busy.RunAsync(visibly: showFeedback, async cancellationToken =>
        {
            // Blacklisted employees included -- these are pre-existing punches, not a
            // picker, so a blacklisted employee's own history still needs a resolvable
            // name here (see AttendanceEmployeeDirectory.GetAllIncludingBlacklistedAsync's
            // own doc comment).
            var employees = await _employeeDirectory.GetAllIncludingBlacklistedAsync(cancellationToken);
            var logs = await QueryStoredLogsInRangeAsync(cancellationToken);
            var employeeInfo = StoredPunchLogRowFactory.BuildEmployeeInfoByPin(employees);

            StoredLogs.Clear();
            foreach (var log in logs)
                StoredLogs.Add(StoredPunchLogRowFactory.BuildRow(log, employeeInfo));

            StoredLogsCount = StoredLogs.Count;
            HasLoadedStoredLogs = true;
            _loadedSnapshot = (LogViewStart, LogViewEnd, _dataVersion.DeviceLogsVersion);
            _saveViewState();

            if (showFeedback)
                _statusBarService.ShowSuccess($"Found {StoredLogsCount} punch(es) in range.");
        },
        onError: ex =>
        {
            if (showFeedback)
                _statusBarService.ShowError(ex.Message);
        });
    }

    /// <summary>Internal (not private) so both LoadStoredLogsCommand's CanExecute wiring
    /// and AttendanceViewModel.ActivateInitialTabAsync's initial-tab check can share the
    /// one implementation.</summary>
    internal bool CanLoad() => !_busy.IsRunning;

    [RelayCommand(CanExecute = nameof(CanExportStoredLogs))]
    private async Task ExportStoredLogsAsync()
    {
        var validationError = ValidateLogViewRange();
        if (validationError is not null)
        {
            _statusBarService.ShowCaution(validationError);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Punch Logs",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"Punch_Logs_{LogViewStart!.Value:MMddyy}_{LogViewEnd!.Value:MMddyy}.xlsx",
        };

        if (dialog.ShowDialog() != true)
            return;

        // visibly: true -- always an explicit click, never a silent auto-load.
        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            // Blacklisted employees included -- same reasoning as LoadStoredLogsCoreAsync's
            // own GetAllIncludingBlacklistedAsync call above: an exported workbook should
            // still show who a blacklisted employee's own punches belong to.
            var employees = await _employeeDirectory.GetAllIncludingBlacklistedAsync(cancellationToken);
            var logs = await QueryStoredLogsInRangeAsync(cancellationToken);

            // Everything above is cancellable; ExportLogsToExcel itself is not, so a
            // Cancel click always lands before any file is written, never partway
            // through one.
            AttendanceExcelExporter.ExportLogsToExcel(dialog.FileName, logs, employees);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            _saveViewState();
            _statusBarService.ShowSuccess($"Saved {logs.Count} punch(es) to {dialog.FileName}.");
        },
        onError: ex => _statusBarService.ShowError(ex.Message));
    }

    private bool CanExportStoredLogs() => !_busy.IsRunning;

    /// <summary>Cheap, synchronous checks only -- deliberately doesn't touch the
    /// database, so both Load and Export can call it before setting IsRunning, the same
    /// way Validate() works for Generate Reports.</summary>
    private string? ValidateLogViewRange()
    {
        if (LogViewStart is null || LogViewEnd is null)
            return "⚠ Select both a start and end date.";

        if (LogViewStart > LogViewEnd)
            return "⚠ Start date must not be after end date.";

        return null;
    }

    /// <summary>Shared by Load and Export -- both need exactly the same "every punch in
    /// [LogViewStart, LogViewEnd]" query, so there's one place that can go stale rather
    /// than two copies drifting apart. Callers must check ValidateLogViewRange() first --
    /// this assumes LogViewStart/LogViewEnd are already known non-null.
    ///
    /// Deliberately unfiltered by LogViewSearchText -- like ReportViewModel's Export
    /// Summary… always exporting every row regardless of the on-screen status/scope
    /// filter, Export… here always saves every punch actually in range regardless of
    /// whatever's currently narrowing the grid (see StoredLogsView/FilterStoredLogRow);
    /// the search box is a way to *find* something on screen, not a way to scope what
    /// gets saved to disk. Load reads from here too, but then applies StoredLogsView's
    /// live filter on top for display, same as it always could.
    ///
    /// Device punches only -- deliberately does not merge in ManualAttendanceLogs (see
    /// this class's doc comment). Both the grid and Export… read from this one method, so
    /// they naturally stay in sync with each other.</summary>
    private async Task<List<AttendanceLog>> QueryStoredLogsInRangeAsync(CancellationToken cancellationToken = default)
    {
        var rangeStart = LogViewStart!.Value.Date;
        var rangeEnd = LogViewEnd!.Value.Date.AddDays(1).AddTicks(-1); // inclusive of the whole end day

        return await _attendanceLogRepository.GetLogsAsync(rangeStart, rangeEnd, cancellationToken: cancellationToken);
    }
}
