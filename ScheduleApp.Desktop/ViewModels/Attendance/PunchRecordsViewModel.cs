using System.Globalization;
using System.Reactive.Linq;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

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
public partial class PunchRecordsViewModel : ViewModelBase
{
    private const int MaxSuggestions = 8;

    private readonly IAttendanceLogRepository _attendanceLogRepository;
    private readonly AttendanceBusyState _busy;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly AttendanceEmployeeDirectory _employeeDirectory;
    private readonly Action _saveViewState;
    private readonly AttendanceTabActivationGate _tabActivationGate;

    /// <summary>The date range/DeviceLogsVersion combination StoredLogs was loaded for, as of
    /// the last successful load -- null until the first one (see ShouldAutoReload). Not the
    /// search text: that filters what's already loaded, with no reload.</summary>
    private (DateTime? Start, DateTime? End, int DeviceLogsVersion)? _loadedSnapshot;

    /// <summary>What the grid is actually filtered on -- separate from LogViewSearchText, and
    /// only updated when a suggestion is picked or the box is cleared: re-filtering every
    /// loaded row on every keystroke ("C", "Cr", "Cru"…) was wasted work for a search the person
    /// is about to settle by picking who they meant.</summary>
    private string _appliedSearchValue;

    /// <summary>Guards against an older keystroke's suggestions arriving after a newer one's
    /// and clobbering what should be on screen.</summary>
    private int _logViewSuggestionRequestId;

    private readonly IObservable<bool> _notBusy;
    private readonly IObservable<bool> _canRefreshOrCancel;

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
        : base(statusBarService)
    {
        _attendanceLogRepository = attendanceLogRepository;
        _busy = busy;
        _dataVersion = dataVersion;
        _employeeDirectory = employeeDirectory;
        _saveViewState = saveViewState;
        _tabActivationGate = tabActivationGate;

        LogViewStart = initialLogViewStart;
        LogViewEnd = initialLogViewEnd;
        IsPunchRecordsTabSelected = initialIsPunchRecordsTabSelected;

        // Restored applied, not just typed: a box showing leftover text that isn't actually
        // filtering the grid would be a worse first impression than either.
        LogViewSearchText = initialLogViewSearchText;
        _appliedSearchValue = initialLogViewSearchText;

        // The grid shows StoredLogs narrowed by the applied search value -- without touching
        // StoredLogs itself, which Export always writes whole.
        StoredLogsView = new FilteredCollection<StoredPunchLogRow>(StoredLogs) { Filter = MatchesAppliedSearch };

        // The Period row's labeled button: Cancel while anything's visibly running, Reload
        // otherwise -- same as ReportViewModel's, but with a text label beside its glyph,
        // among this row's other labeled buttons.
        var visiblyRunning = _busy.WhenAnyValue(b => b.IsVisiblyRunning);
        _refreshOrCancelContentHelper = visiblyRunning.Select(running => running ? "Cancel" : "Reload")
            .ToProperty(this, x => x.RefreshOrCancelContent);
        _refreshOrCancelIconHelper = visiblyRunning.Select(running => running ? "" : "")
            .ToProperty(this, x => x.RefreshOrCancelIcon);
        _refreshOrCancelToolTipHelper = visiblyRunning
            .Select(running => running ? "Stop whatever's currently running." : "Reload stored punches for this period.")
            .ToProperty(this, x => x.RefreshOrCancelToolTip);

        _notBusy = _busy.WhenAnyValue(b => b.IsRunning).Select(running => !running);
        _canRefreshOrCancel = Observable.CombineLatest(visiblyRunning, _notBusy, (running, idle) => running || idle);

        ReportFailuresOf(PreviousPeriodCommand, NextPeriodCommand, SelectLogViewSuggestionCommand, LoadStoredLogsCommand,
            RefreshOrCancelStoredLogsCommand, ExportStoredLogsCommand);

        this.WhenAnyValue(x => x.LogViewStart, x => x.LogViewEnd).Skip(1).Subscribe(_ => _saveViewState());
        this.WhenAnyValue(x => x.IsPunchRecordsTabSelected).Skip(1).Subscribe(OnIsPunchRecordsTabSelectedChanged);
        this.WhenAnyValue(x => x.LogViewSearchText).Skip(1).Subscribe(OnLogViewSearchTextChanged);

        // A keystroke that arrived while busy got no suggestion fetch (see
        // OnLogViewSearchTextChanged) -- catch up once the shared ScheduleDbContext is free,
        // rather than leaving the dropdown empty until the next keystroke.
        _busy.WhenAnyValue(b => b.IsRunning)
            .Skip(1)
            .Where(running => !running && LogViewSearchText.Trim().Length > 0)
            .Subscribe(running => _ = UpdateLogViewSuggestionsAsync(LogViewSearchText));
    }

    /// <summary>Whether the Punch Records page is the one showing -- switching to it reloads
    /// the grid, silently, if anything it shows has changed (see ShouldAutoReload).</summary>
    [Reactive]
    public partial bool IsPunchRecordsTabSelected { get; set; }

    private void OnIsPunchRecordsTabSelectedChanged(bool selected)
    {
        if (!_tabActivationGate.IsReady) return;

        if (selected && CanLoad() && ShouldAutoReload())
            _ = LoadStoredLogsCoreAsync(showFeedback: false);

        _saveViewState();
    }

    /// <summary>Whether a reload would show anything new: the range, or the device punches
    /// (this grid is device-only), changed since the last load -- or nothing has loaded yet.
    /// Only the silent revisit reload asks; Load always runs. Skipping the needless reload is
    /// what keeps the grid's scroll position on an ordinary revisit.</summary>
    private bool ShouldAutoReload() =>
        _loadedSnapshot != (LogViewStart, LogViewEnd, _dataVersion.DeviceLogsVersion);

    /// <summary>The range shown -- remembered across sessions.</summary>
    [Reactive]
    public partial DateTime? LogViewStart { get; set; }

    [Reactive]
    public partial DateTime? LogViewEnd { get; set; }

    /// <summary>The Period row's ◀: steps to the previous semi-monthly cut-off and reloads --
    /// a date edit here doesn't load on its own (Load is always an explicit action), so this
    /// does, with feedback like a Load click.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private Task PreviousPeriodAsync() => StepPeriodAsync(forward: false);

    /// <summary>The ▶ button -- see PreviousPeriodAsync.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private Task NextPeriodAsync() => StepPeriodAsync(forward: true);

    private Task StepPeriodAsync(bool forward)
    {
        var (start, end) = AttendancePeriodNavigation.AdjacentCutoffPeriod(LogViewStart ?? LogViewEnd ?? DateTime.Today, forward);
        LogViewStart = start;
        LogViewEnd = end;
        return LoadStoredLogsCoreAsync(showFeedback: true);
    }

    /// <summary>The search box -- one value at a time (an Employee ID, a name, or a
    /// department), not a comma-separated list. Recomputes LogViewSuggestions on every
    /// keystroke, but the grid only re-filters on a picked suggestion or a cleared box (see
    /// _appliedSearchValue). A display filter only: never re-queries, never changes what
    /// Load/Export fetch.</summary>
    [Reactive]
    public partial string LogViewSearchText { get; set; } = string.Empty;

    private void OnLogViewSearchTextChanged(string value)
    {
        // Not while busy: the suggestion fetch can fall through to a real query when the cache
        // is cold, and every other operation already treats busy as "the shared
        // ScheduleDbContext is in use". Caught up once busy clears (see the constructor).
        if (!_busy.IsRunning)
            _ = UpdateLogViewSuggestionsAsync(value);

        // Clearing the box is an unambiguous "show everyone again" -- applied at once rather
        // than left filtering on a term that's no longer in the box.
        if (value.Trim().Length == 0)
            ApplySearchValue(string.Empty);
    }

    private void ApplySearchValue(string value)
    {
        _appliedSearchValue = value;
        StoredLogsView.Refresh();
    }

    /// <summary>Autosuggestion candidates for the search box's current text.</summary>
    public RangeObservableCollection<PunchSearchSuggestion> LogViewSuggestions { get; } = [];

    [Reactive]
    public partial bool IsLogViewSuggestionsOpen { get; set; }

    /// <summary>Recomputes LogViewSuggestions for <paramref name="text"/>, trimmed -- closed
    /// while the box is empty rather than dumping the whole roster on screen.</summary>
    private async Task UpdateLogViewSuggestionsAsync(string text)
    {
        var term = text.Trim();
        if (term.Length == 0)
        {
            LogViewSuggestions.Clear();
            IsLogViewSuggestionsOpen = false;
            return;
        }

        var requestId = ++_logViewSuggestionRequestId;

        List<Employee> employees;
        try
        {
            employees = await _employeeDirectory.GetForSuggestionsAsync();
        }
        catch (Exception)
        {
            // Suggestions are a convenience; Load/Export surface the real error.
            return;
        }

        if (requestId != _logViewSuggestionRequestId)
            return;

        LogViewSuggestions.ReplaceAll(BuildSuggestionMatches(term, employees));
        IsLogViewSuggestionsOpen = LogViewSuggestions.Count > 0;
    }

    /// <summary>One suggestion per matching department, plus one per matching employee -- not
    /// one per matching field. A department's shown and inserted text are its name; an
    /// employee's shown text adds their department for context, but inserts just their name
    /// (see PunchSearchSuggestion.InsertValue). Employees without a department are only
    /// matched as employees. Capped and alphabetized, departments and employees
    /// together.</summary>
    private static List<PunchSearchSuggestion> BuildSuggestionMatches(string term, IReadOnlyList<Employee> employees)
    {
        var departments = employees
            .Select(e => e.Department?.Name)
            .OfType<string>()
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new PunchSearchSuggestion($"{name} (Department)", name));

        var people = employees
            .Where(e => e.Pin.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.LastName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(e => new PunchSearchSuggestion($"{e.DisplayName} — {e.Department?.Name ?? "(Unassigned)"}", e.DisplayName));

        return [.. departments.Concat(people)
            .OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)];
    }

    /// <summary>A picked suggestion: the moment the grid actually filters. The box shows what's
    /// now applied, and the dropdown closes.</summary>
    [ReactiveCommand]
    private void SelectLogViewSuggestion(PunchSearchSuggestion? suggestion)
    {
        if (suggestion is null) return;

        LogViewSearchText = suggestion.InsertValue;
        ApplySearchValue(suggestion.InsertValue);
        IsLogViewSuggestionsOpen = false;
    }

    [Reactive]
    public partial int StoredLogsCount { get; private set; }

    [Reactive]
    public partial bool HasLoadedStoredLogs { get; private set; }

    /// <summary>Replaced in one change per load.</summary>
    public RangeObservableCollection<StoredPunchLogRow> StoredLogs { get; } = [];

    /// <summary>What the grid binds to -- see the constructor.</summary>
    public FilteredCollection<StoredPunchLogRow> StoredLogsView { get; }

    /// <summary>The applied search value: blank shows everyone, a number is an exact Employee
    /// ID, anything else a substring of the name or department.</summary>
    private bool MatchesAppliedSearch(object item)
    {
        if (item is not StoredPunchLogRow row) return false;

        var term = _appliedSearchValue;
        if (term.Length == 0) return true;

        return int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? row.EmployeeId == id
            : row.EmployeeName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.DepartmentName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private Task LoadStoredLogsAsync() => LoadStoredLogsCoreAsync(showFeedback: true);

    /// <summary>The Period row's Reload button, toggling in place to Cancel while anything on
    /// the Attendance page is visibly running -- this page's Cancel for Import…/Fetch too.
    /// Synchronous, so it stays enabled, as Cancel, while the load it starts runs.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRefreshOrCancel))]
    private void RefreshOrCancelStoredLogs()
    {
        if (_busy.IsVisiblyRunning)
            _busy.Cancel();
        else
            _ = LoadStoredLogsCoreAsync(showFeedback: true);
    }

    [ObservableAsProperty(InitialValue = "Reload")]
    public partial string RefreshOrCancelContent { get; }

    /// <summary>Segoe Fluent Icons Cancel/Refresh.</summary>
    [ObservableAsProperty(InitialValue = "")]
    public partial string RefreshOrCancelIcon { get; }

    /// <summary>Generic on purpose: IsVisiblyRunning can be true because of anything on the
    /// Attendance page, not only this button.</summary>
    [ObservableAsProperty]
    public partial string RefreshOrCancelToolTip { get; }

    /// <summary>The load; feedback (validation, success and error messages) for a click only,
    /// not for the silent reload on a revisit, where the refreshed grid is feedback
    /// enough.</summary>
    private async Task LoadStoredLogsCoreAsync(bool showFeedback)
    {
        if (ValidateLogViewRange() is { } validationError)
        {
            if (showFeedback)
                StatusBar.ShowCaution(validationError);
            return;
        }

        await _busy.RunAsync(visibly: showFeedback, async cancellationToken =>
        {
            // Blacklisted employees included -- these are existing punches, and a blacklisted
            // employee's own history still needs a name.
            var employees = await _employeeDirectory.GetAllIncludingBlacklistedAsync(cancellationToken);
            var logs = await QueryStoredLogsInRangeAsync(cancellationToken);
            var employeeInfo = StoredPunchLogRowFactory.BuildEmployeeInfoByPin(employees);

            StoredLogs.ReplaceAll(logs.Select(log => StoredPunchLogRowFactory.BuildRow(log, employeeInfo)));
            StoredLogsCount = StoredLogs.Count;
            HasLoadedStoredLogs = true;
            _loadedSnapshot = (LogViewStart, LogViewEnd, _dataVersion.DeviceLogsVersion);
            _saveViewState();

            if (showFeedback)
                StatusBar.ShowSuccess($"Found {StoredLogsCount} punch(es) in range.");
        },
        onError: ex =>
        {
            if (showFeedback)
                ShowFailure(ex);
        });
    }

    /// <summary>Whether a load could start now -- AttendanceViewModel's initial-page check
    /// reads it too.</summary>
    internal bool CanLoad() => !_busy.IsRunning;

    /// <summary>Saves every punch in range to Excel -- regardless of the search box, which finds
    /// things on screen rather than scoping what's saved -- then opens it.</summary>
    [ReactiveCommand(CanExecute = nameof(_notBusy))]
    private async Task ExportStoredLogsAsync()
    {
        if (ValidateLogViewRange() is { } validationError)
        {
            StatusBar.ShowCaution(validationError);
            return;
        }

        if (await PickFileToSaveAsync("Excel Workbook (*.xlsx)|*.xlsx",
                $"Punch_Logs_{LogViewStart!.Value:MMddyy}_{LogViewEnd!.Value:MMddyy}.xlsx", "Save Punch Logs") is not { } path)
        {
            return;
        }

        await _busy.RunAsync(visibly: true, async cancellationToken =>
        {
            var employees = await _employeeDirectory.GetAllIncludingBlacklistedAsync(cancellationToken);
            var logs = await QueryStoredLogsInRangeAsync(cancellationToken);

            // Everything above is cancellable; writing the file isn't, so a Cancel always lands
            // before any file is written, never partway through one.
            AttendanceExcelExporter.ExportLogsToExcel(path, logs, employees);
            await OpenFileAsync(path);
            _saveViewState();
            StatusBar.ShowSuccess($"Saved {logs.Count} punch(es) to {path}.");
        },
        onError: ex => ShowFailure(ex));
    }

    /// <summary>Cheap and synchronous -- checked before going busy.</summary>
    private string? ValidateLogViewRange()
    {
        if (LogViewStart is null || LogViewEnd is null)
            return "⚠ Select both a start and end date.";

        if (LogViewStart > LogViewEnd)
            return "⚠ Start date must not be after end date.";

        return null;
    }

    /// <summary>Every device punch in [LogViewStart, LogViewEnd], the whole end day included --
    /// shared by Load and Export so the two stay in step. Never merges in manual entries (see
    /// this class's own doc comment).</summary>
    private async Task<List<AttendanceLog>> QueryStoredLogsInRangeAsync(CancellationToken cancellationToken = default) =>
        await _attendanceLogRepository.GetLogsAsync(
            LogViewStart!.Value.Date, LogViewEnd!.Value.Date.AddDays(1).AddTicks(-1), cancellationToken: cancellationToken);
}
