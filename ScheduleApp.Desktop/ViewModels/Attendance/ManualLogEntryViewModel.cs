using System.Globalization;
using System.Reactive.Linq;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Utilities;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>
/// Collects the details for one ManualAttendanceLog entry (see ManualEntryEditorViewModel, which
/// opens it to add one, edit one, or add one for a calendar tile). Self-validating on Save:
/// nothing is settled into <see cref="AcceptedLog"/> until every check passes, so a bad edit
/// can't hand its opener a half-valid result.
///
/// Also shows a reference-only list of the day's device punches for whoever's picked (see
/// <see cref="MachinePunches"/>), so the person can see what the device already has without
/// leaving the dialog -- purely a decoration: it never affects Save, and a failed or
/// still-running fetch never blocks it.
/// </summary>
public partial class ManualLogEntryViewModel : ReactiveViewModel
{
    private const int MaxSuggestions = 15;

    private readonly IReadOnlyList<Employee> _employees;
    private readonly IAttendanceLogRepository _attendanceLogRepository;

    /// <summary>Null when adding; the entry being corrected when editing -- its Id goes back out
    /// on <see cref="AcceptedLog"/> for UpdateAsync.</summary>
    private readonly ManualAttendanceLog? _existingLog;

    /// <summary>Set while this ViewModel itself writes EmployeeText (prefilling it, or a picked
    /// suggestion), so that doesn't reopen the suggestions it just closed.</summary>
    private bool _settingEmployeeText;

    /// <summary>The machine-punch fetches, one after another -- see RequestMachinePunches.</summary>
    private Task _machinePunchesFetch = Task.CompletedTask;
    private int _machinePunchesRequest;

    /// <param name="employees">The full roster -- every employee has a Pin.</param>
    /// <param name="existingLog">The entry to edit, or null to add one.</param>
    /// <param name="day">The employee and date a calendar tile was right-clicked for: prefilled
    /// and locked, since the tile already decided them -- a wrong tile means Cancel and reopen
    /// from the right one.</param>
    public ManualLogEntryViewModel(
        IReadOnlyList<Employee> employees,
        IAttendanceLogRepository attendanceLogRepository,
        ManualAttendanceLog? existingLog = null,
        (Employee Employee, DateOnly Date)? day = null)
    {
        _employees = employees;
        _attendanceLogRepository = attendanceLogRepository;
        _existingLog = existingLog;

        Title = existingLog is null ? "Add Manual Entry" : "Edit Manual Entry";
        SaveText = existingLog is null ? "Add" : "Save";
        IsEmployeeAndDateLocked = day is not null;

        if (existingLog is not null)
        {
            SetEmployeeText(_employees.FirstOrDefault(e => e.Pin == existingLog.EmployeeId) is { } employee
                ? Format(employee)
                : existingLog.EmployeeId.ToString(CultureInfo.InvariantCulture));
            Date = existingLog.Timestamp.Date;
            Time = TimeOnly.FromDateTime(existingLog.Timestamp);
            PunchType = existingLog.PunchType;
            EnteredBy = existingLog.EnteredBy;
        }
        else
        {
            if (day is { } tile)
                SetEmployeeText(Format(tile.Employee));
            Date = (day?.Date ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
            EnteredBy = Environment.UserName;
        }

        // After PunchType is prefilled, so an edited entry's default reads its own punch type.
        Reason = new DefaultedText(() => PunchTypeLabel.ToText(PunchType), existingLog?.Reason);
        CommittedEmployeeId = ResolveEmployeeId(EmployeeText);

        _resolvedEmployeeIdHelper = this.WhenAnyValue(x => x.EmployeeText)
            .Select(ResolveEmployeeId)
            .ToProperty(this, x => x.ResolvedEmployeeId);

        // Matching employees as the person types: by Employee ID or name, anywhere in the
        // string, not just a prefix.
        this.WhenAnyValue(x => x.EmployeeText)
            .Skip(1)
            .Where(_ => !_settingEmployeeText)
            .Subscribe(text =>
            {
                Suggestions = Match(text.Trim());
                IsSuggestionsOpen = Suggestions.Count > 0;
            });

        this.WhenAnyValue(x => x.PunchType).Skip(1).Subscribe(_ => Reason.RefreshDefault());
        this.WhenAnyValue(x => x.CommittedEmployeeId, x => x.Date).Subscribe(_ => RequestMachinePunches());
    }

    public string Title { get; }

    public string SaveText { get; }

    public bool IsEmployeeAndDateLocked { get; }

    /// <summary>An Employee ID, a name, or a picked "id — name" suggestion.</summary>
    [Reactive]
    public partial string EmployeeText { get; set; } = string.Empty;

    /// <summary>The Pin <see cref="EmployeeText"/> resolves to -- only its leading number, and
    /// only if it belongs to one of the employees given; null otherwise rather than a
    /// guess.</summary>
    [ObservableAsProperty]
    public partial int? ResolvedEmployeeId { get; }

    /// <summary>The employee the machine punches are shown for: settled when the person leaves
    /// the employee box or picks a suggestion, not on every keystroke.</summary>
    [Reactive]
    public partial int? CommittedEmployeeId { get; private set; }

    [Reactive]
    public partial IReadOnlyList<string> Suggestions { get; private set; } = [];

    [Reactive]
    public partial bool IsSuggestionsOpen { get; set; }

    [Reactive]
    public partial DateTime? Date { get; set; }

    [Reactive]
    public partial TimeOnly? Time { get; set; }

    /// <summary>0 = Clock In, 1 = Clock Out -- AttendanceLog/ManualAttendanceLog's
    /// convention.</summary>
    [Reactive]
    public partial int PunchType { get; set; }

    /// <summary>Optional: defaults to the punch type's own label ("Clock In"), which reads fine
    /// as a reason for the common case of logging a missed punch.</summary>
    public DefaultedText Reason { get; }

    [Reactive]
    public partial string EnteredBy { get; set; } = string.Empty;

    /// <summary>The day's device punches for <see cref="CommittedEmployeeId"/>, as "8:02 AM —
    /// Clock In".</summary>
    [Reactive]
    public partial IReadOnlyList<string> MachinePunches { get; private set; } = [];

    /// <summary>Shown in place of the list: nothing to fetch for yet, a fetch that found
    /// nothing, or one that failed -- null when the list has punches to show.</summary>
    [Reactive]
    public partial string? MachinePunchesMessage { get; private set; }

    /// <summary>What Save settled on -- null until it succeeds.</summary>
    public ManualAttendanceLog? AcceptedLog { get; private set; }

    /// <summary>Puts a picked suggestion in the employee box and shows its machine
    /// punches.</summary>
    public void PickSuggestion(string suggestion)
    {
        SetEmployeeText(suggestion);
        IsSuggestionsOpen = false;
        CommitEmployee();
    }

    /// <summary>The person left the employee box: shows the machine punches for whoever it
    /// resolves to. Text that resolves to nobody leaves the list as it was rather than clearing
    /// it out from under a still-mid-edit field.</summary>
    public void CommitEmployee()
    {
        if (ResolvedEmployeeId is { } id)
            CommittedEmployeeId = id;
    }

    /// <summary>Waits for the machine-punch fetch to finish -- for the opener to call before it
    /// touches the shared, app-lifetime ScheduleDbContext itself after Save, since EF Core
    /// can't run two operations on one context at once. Never faults: the fetch is
    /// fail-soft.</summary>
    public Task WaitForMachinePunchesAsync() => _machinePunchesFetch;

    /// <summary>Save: checks there's an employee, a date and a time, then settles
    /// <see cref="AcceptedLog"/>; false, after saying what's missing, otherwise.</summary>
    [ReactiveCommand]
    private async Task<bool> AcceptAsync()
    {
        if (ResolvedEmployeeId is not { } employeeId)
            return await RejectAsync("Pick an employee from the list (start typing an Employee ID or name).");
        if (Date is not { } date)
            return await RejectAsync("Select a date.");
        if (Time is not { } time)
            return await RejectAsync("Select a time.");

        AcceptedLog = new ManualAttendanceLog
        {
            Id = _existingLog?.Id ?? 0,
            EmployeeId = employeeId,
            Timestamp = date.Date + time.ToTimeSpan(),
            PunchType = PunchType,
            Reason = Reason.Value,
            EnteredBy = string.IsNullOrWhiteSpace(EnteredBy) ? Environment.UserName : EnteredBy.Trim(),
        };
        return true;
    }

    private async Task<bool> RejectAsync(string message)
    {
        await NotifyAsync(message, "Check your entry", NoticeKind.Warning);
        return false;
    }

    private void SetEmployeeText(string text)
    {
        _settingEmployeeText = true;
        EmployeeText = text;
        _settingEmployeeText = false;
    }

    private List<string> Match(string term) => term.Length == 0
        ? []
        : [.. _employees
            .Where(e => e.Pin.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Take(MaxSuggestions)
            .Select(Format)];

    private int? ResolveEmployeeId(string text)
    {
        var idPart = text.Split('—', 2)[0].Trim();
        return int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && _employees.Any(e => e.Pin == id)
                ? id
                : null;
    }

    /// <summary>Pin, the Employee ID that's shown and typed, not the database Id.</summary>
    private static string Format(Employee employee) => $"{employee.Pin} — {employee.DisplayName}";

    /// <summary>
    /// Fetches the machine punches for the committed employee and date, after whatever fetch is
    /// still running -- never two at once against the shared ScheduleDbContext -- and skipping
    /// one a newer request has already superseded, before or after its query.
    ///
    /// The query itself is never cancelled: aborting a command mid-read on the shared
    /// connection can surface as a raw SqlException and leave that connection unusable for every
    /// page sharing it. Dropping a stale result is just as effective for a reference-only list.
    /// </summary>
    private void RequestMachinePunches()
    {
        var request = ++_machinePunchesRequest;
        if (CommittedEmployeeId is not { } employeeId || Date is not { } date)
        {
            MachinePunches = [];
            MachinePunchesMessage = "Pick an employee to see that day's machine punches.";
            return;
        }

        _machinePunchesFetch = FetchAfterAsync(_machinePunchesFetch, request, employeeId, DateOnly.FromDateTime(date));
    }

    private async Task FetchAfterAsync(Task previous, int request, int employeeId, DateOnly date)
    {
        await previous;
        if (request != _machinePunchesRequest) return;

        try
        {
            // The whole day: GetLogsAsync's upper bound is inclusive, so midnight would drop
            // every punch after 12:00 AM. Timestamps are local time end to end, so these line
            // up with the calendar's own day boundaries.
            var logs = await _attendanceLogRepository.GetLogsAsync(
                date.ToDateTime(TimeOnly.MinValue), date.ToDateTime(TimeOnly.MaxValue), cancellationToken: CancellationToken.None);
            if (request != _machinePunchesRequest) return;

            // GetLogsAsync filters by time only, so the employee is filtered here.
            MachinePunches = [.. logs
                .Where(log => log.EmployeeId == employeeId)
                .OrderBy(log => log.Timestamp)
                .Select(log => $"{TimeDisplayFormat.Format(TimeOnly.FromDateTime(log.Timestamp))} — {PunchTypeLabel.ToText(log.PunchType)}")];
            MachinePunchesMessage = MachinePunches.Count == 0 ? "No machine punches for this day." : null;
        }
        catch (Exception) when (request != _machinePunchesRequest)
        {
        }
        catch (Exception)
        {
            MachinePunches = [];
            MachinePunchesMessage = "Couldn't load machine punches for this day.";
        }
    }
}
