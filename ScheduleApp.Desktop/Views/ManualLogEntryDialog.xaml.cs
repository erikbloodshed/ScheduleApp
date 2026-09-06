using System.Globalization;
using System.Windows;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Collects the details for one ManualAttendanceLog entry -- see
/// ManualEntryEditorViewModel.AddManualEntryAsync/EditManualEntryAsync, its two
/// callers (one per constructor below). Follows the same
/// self-validating-on-OK shape as ApplyScheduleDialog/InputDialog:
/// nothing is committed to the dialog's public properties until Save's
/// checks all pass, so a bad edit can't produce a half-valid result the
/// caller has to re-check.
///
/// Also shows a reference-only "Machine Punches" list of the day's device
/// punches for whoever's currently selected -- see RefreshMachinePunchesAsync
/// -- so the person can see what the device already has without leaving the
/// dialog. Purely a decoration on top of the Add/Edit flow: it never affects
/// Save, and a failed or still-in-flight fetch never blocks it either.
/// </summary>
public partial class ManualLogEntryDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IReadOnlyList<Employee> _employees;
    private readonly IAttendanceLogRepository _attendanceLogRepository;

    /// <summary>Guards RefreshMachinePunchesAsync -- cancelled and replaced on
    /// every call so a slow fetch for a since-abandoned employee/date can't
    /// land after a newer one already has. Same shape (and same "flag, not an
    /// actual abort" semantics) as MainViewModel._attendanceStatusCts -- see
    /// that field's doc comment.</summary>
    private CancellationTokenSource? _machinePunchesCts;

    /// <summary>Serializes RefreshMachinePunchesAsync's own GetLogsAsync calls against
    /// each other. _machinePunchesCts's own cancel-and-replace (above) only *asks* the
    /// previous call to stop -- it doesn't make it stop immediately. Cancelling a
    /// CancellationToken passed into an EF Core query still has to make a real round
    /// trip (SQL Server processing the cancel signal) before that query's own await
    /// actually unwinds, and until it does, that query is still genuinely running
    /// against the shared, app-lifetime-scoped ScheduleDbContext. A new call fired
    /// quickly enough on top of that -- most easily hit via this dialog's own two
    /// SelectedDateChanged firings during the calendar-tile/Edit-mode constructors' own
    /// EmployeeBox/DateBox prefill, both against the same DbContext instance
    /// IAttendanceLogRepository wraps -- could start a second, genuinely concurrent
    /// GetLogsAsync before the first one actually finished unwinding, which is exactly
    /// EF Core's "A second operation was started on this context instance before a
    /// previous operation completed," caught below and shown as "Couldn't load machine
    /// punches for this day" -- indistinguishable, from the placeholder alone, from a
    /// real fetch failure. This gate makes that impossible: a new call always waits for
    /// whichever previous call is still unwinding (cancelled or not) to actually finish
    /// before its own GetLogsAsync starts, so at most one is ever in flight from this
    /// dialog at a time. Same shape as AttendanceEmployeeDirectory's own _dbGate, just
    /// narrower in scope -- that one serializes its class's calls against every other
    /// repository's too; this one only ever has itself to serialize against, since
    /// RefreshMachinePunchesAsync is the only place this dialog touches
    /// IAttendanceLogRepository.</summary>
    private readonly SemaphoreSlim _machinePunchesGate = new(1, 1);

    /// <summary>The Task for whichever RefreshMachinePunchesAsync call was most
    /// recently kicked off -- every call site fires that method fire-and-forget
    /// (DateBox_SelectedDateChanged, EmployeeBox_LostFocus, and the one-shot call
    /// each constructor's own DateBox assignment triggers before this dialog is
    /// even shown), since nothing in here needs to await it. A caller that's
    /// about to touch the same shared DbContext right after this dialog closes
    /// does need to know it's actually finished first, though -- see
    /// WaitForMachinePunchesFetchAsync below, the only reader of this field.
    /// Starts at Task.CompletedTask so awaiting it is always safe even in the
    /// (never-actually-possible, every constructor sets DateBox) case where no
    /// fetch has been kicked off yet.</summary>
    private Task _machinePunchesFetchTask = Task.CompletedTask;

    /// <summary>Null when adding a new entry; the entry being edited when
    /// opened via the Edit-mode constructor. Carries forward Id/CreatedAt
    /// (neither shown nor editable here) so the caller can round-trip them
    /// straight into UpdateAsync without having to track them separately.</summary>
    private readonly ManualAttendanceLog? _existingLog;

    /// <summary>Guards against EmployeeBox_TextChanged re-running its own
    /// search when we programmatically set EmployeeBox.Text after a
    /// suggestion is picked -- without this, picking a suggestion would
    /// immediately reopen the popup showing that same employee as a
    /// (now redundant) single match.</summary>
    private bool _suppressEmployeeTextChanged;

    public int EmployeeId { get; private set; }
    public DateTime Timestamp { get; private set; }

    /// <summary>0 = Clock In, 1 = Clock Out -- matches PunchTypeCombo's item
    /// order and AttendanceLog/ManualAttendanceLog.PunchType's convention.</summary>
    public int PunchType { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string EnteredBy { get; private set; } = string.Empty;

    /// <summary>ManualAttendanceLog.Id being corrected, for the Edit-mode
    /// caller to pass to UpdateAsync -- null when this dialog was opened via
    /// the Add constructor. CreatedAt isn't surfaced here since it's never
    /// edited (see IManualAttendanceLogRepository.UpdateAsync's doc comment)
    /// and the caller already has the original entry to read it back from if
    /// needed.</summary>
    public int? EditedId => _existingLog?.Id;

    /// <summary>Pass the full roster -- every employee has a Pin now (see Employee.Pin's
    /// own doc comment), so there's no one left to filter out here.</summary>
    public ManualLogEntryDialog(IReadOnlyList<Employee> employees, IAttendanceLogRepository attendanceLogRepository)
    {
        InitializeComponent();

        _employees = employees.ToList();
        _attendanceLogRepository = attendanceLogRepository;

        PopulateTimeItems(TimeCombo);
        DateBox.SelectedDate = DateTime.Today; // fires SelectedDateChanged -> RefreshMachinePunchesAsync, a no-op here since no employee is resolved yet
        EnteredByBox.Text = Environment.UserName;

        Loaded += (_, _) => EmployeeBox.Focus();
    }

    /// <summary>Edit-mode overload -- same employee list as the Add constructor, but
    /// every field prefilled from the entry being corrected, and the dialog relabeled
    /// to "Edit Manual Entry" / "Save".</summary>
    public ManualLogEntryDialog(IReadOnlyList<Employee> employees, ManualAttendanceLog existingLog,
        IAttendanceLogRepository attendanceLogRepository) : this(employees, attendanceLogRepository)
    {
        _existingLog = existingLog;

        Title = "Edit Manual Entry";
        SaveButton.Content = "Save";

        var employee = employees.FirstOrDefault(e => e.Pin == existingLog.EmployeeId);
        _suppressEmployeeTextChanged = true;
        EmployeeBox.Text = employee is not null
            ? FormatEmployee(employee)
            : existingLog.EmployeeId.ToString(CultureInfo.InvariantCulture);
        _suppressEmployeeTextChanged = false;

        DateBox.SelectedDate = existingLog.Timestamp.Date;
        TimeCombo.Text = TimeDisplayFormat.Format(TimeOnly.FromDateTime(existingLog.Timestamp));
        PunchTypeCombo.SelectedIndex = existingLog.PunchType;
        ReasonBox.Text = existingLog.Reason;
        EnteredByBox.Text = existingLog.EnteredBy;
    }

    /// <summary>Convenience constructor for the calendar's right-click "Add Manual
    /// Entry…" command (see ManualEntryEditorViewModel.AddManualEntryForDayAsync) --
    /// chains to the Add constructor above (full roster, same filtering, same initial
    /// no-op DateBox.SelectedDate = DateTime.Today fetch while EmployeeBox is still
    /// blank) and then overwrites EmployeeBox/DateBox with whichever tile was
    /// right-clicked, the same way the Edit-mode constructor above overwrites them with
    /// an existing entry's own values. Same reason for the order too: DateBox gets set
    /// once by the base constructor before EmployeeBox has anything resolvable in it (a
    /// no-op fetch), then EmployeeBox is filled in below, then DateBox is set again --
    /// that second assignment is the one and only real, employee-and-date-resolved
    /// RefreshMachinePunchesAsync call this dialog makes on open.
    ///
    /// Locks EmployeeBox/DateBox (IsEnabled = false) once both are set: opening from a
    /// calendar tile means the employee/date are already decided by whichever tile was
    /// right-clicked, so there's nothing to correct here -- a wrong tile means Cancel
    /// and reopening from the right one, not retyping over what this constructor just
    /// filled in. Still takes the full roster (not just the one clicked employee),
    /// though, not a narrower one scoped to just this employee: ResolveEmployeeId on
    /// Save and RefreshMachinePunchesAsync both still read EmployeeBox.Text back through
    /// the same _employees list every other constructor uses -- disabling the control
    /// only stops the person from changing it, it doesn't change how the rest of this
    /// dialog resolves it.</summary>
    public ManualLogEntryDialog(IReadOnlyList<Employee> employees, Employee employee, DateOnly date,
        IAttendanceLogRepository attendanceLogRepository) : this(employees, attendanceLogRepository)
    {
        _suppressEmployeeTextChanged = true;
        EmployeeBox.Text = FormatEmployee(employee);
        _suppressEmployeeTextChanged = false;

        DateBox.SelectedDate = date.ToDateTime(TimeOnly.MinValue);

        EmployeeBox.IsEnabled = false;
        DateBox.IsEnabled = false;
    }

    private static void PopulateTimeItems(System.Windows.Controls.ComboBox combo)
    {
        for (var hours = 0; hours < 24; hours++)
            combo.Items.Add(TimeDisplayFormat.Format(TimeOnly.FromTimeSpan(TimeSpan.FromHours(hours))));
    }

    /// <summary>Re-filters the suggestion list on every keystroke -- matches
    /// against either the Employee ID or the display name (last/first),
    /// case-insensitively and anywhere in the string, not just a prefix. That
    /// last part matters: WPF's built-in ComboBox text search (what this box
    /// used to be) only prefix-matches, which never actually matched typing a
    /// name here, since every item's text starts with the numeric id.</summary>
    private void EmployeeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressEmployeeTextChanged) return;

        var term = EmployeeBox.Text.Trim();
        if (term.Length == 0)
        {
            EmployeeSuggestionsPopup.IsOpen = false;
            return;
        }

        var matches = _employees
            .Where(emp =>
                (emp.Pin.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                emp.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(emp => emp.LastName).ThenBy(emp => emp.FirstName)
            .Take(15)
            .Select(FormatEmployee)
            .ToList();

        EmployeeSuggestionsList.ItemsSource = matches;
        EmployeeSuggestionsPopup.IsOpen = matches.Count > 0;
    }

    /// <summary>Down/Up move the highlight through the open suggestion list;
    /// Enter commits whichever suggestion is highlighted (and only that --
    /// with nothing highlighted, Enter falls through untouched to the
    /// dialog's default "Add" button, same as before this box had
    /// suggestions at all); Escape closes the popup without closing the
    /// whole dialog (its Cancel button is also bound to Escape).</summary>
    private void EmployeeBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!EmployeeSuggestionsPopup.IsOpen)
            return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                EmployeeSuggestionsList.SelectedIndex =
                    Math.Min(EmployeeSuggestionsList.SelectedIndex + 1, EmployeeSuggestionsList.Items.Count - 1);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Up:
                EmployeeSuggestionsList.SelectedIndex = Math.Max(EmployeeSuggestionsList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Enter:
                if (EmployeeSuggestionsList.SelectedItem is string picked)
                {
                    CommitSuggestion(picked);
                    e.Handled = true;
                }
                break;

            case System.Windows.Input.Key.Escape:
                EmployeeSuggestionsPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void EmployeeSuggestionsList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (EmployeeSuggestionsList.SelectedItem is string picked)
            CommitSuggestion(picked);
    }

    /// <summary>Covers free-typed text that resolves to a valid employee
    /// without ever picking a suggestion (CommitSuggestion's own click/Enter
    /// paths already move focus through EmployeeBox on their way back in,
    /// which fires this same event). Does nothing if the current text
    /// doesn't resolve to anyone -- RefreshMachinePunchesAsync itself has
    /// nothing useful to fetch in that case, and leaving whatever the list
    /// last showed beats clearing it out from under a still-mid-edit
    /// employee field.</summary>
    private void EmployeeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ResolveEmployeeId(EmployeeBox.Text) is not null)
            _machinePunchesFetchTask = RefreshMachinePunchesAsync();
    }

    private void DateBox_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _machinePunchesFetchTask = RefreshMachinePunchesAsync();

    /// <summary>Waits for whichever RefreshMachinePunchesAsync call was most
    /// recently kicked off (see _machinePunchesFetchTask) to actually finish --
    /// called by ManualEntryEditorViewModel.AddOrEditManualEntryAsync right after
    /// this dialog closes with a Save/Add result, before it touches the same
    /// shared, app-lifetime-scoped ScheduleDbContext itself (see that call site's
    /// own doc comment for the full story). Safe to just return the Task as-is,
    /// with no try/catch of its own: RefreshMachinePunchesAsync already catches
    /// everything internally and never faults the Task it returns -- it's a
    /// fail-soft reference decoration, not something that should ever surface as
    /// an error here.</summary>
    public Task WaitForMachinePunchesFetchAsync() => _machinePunchesFetchTask;

    /// <summary>Re-queries the day's device punches for whoever's currently
    /// resolved in EmployeeBox and shows them in MachinePunchesList --
    /// reference-only, per this dialog's own doc comment: nothing here ever
    /// pre-fills Time/Type or blocks Save.
    ///
    /// IAttendanceLogRepository.GetLogsAsync filters by Timestamp only, not
    /// by employee (see that interface's doc comment), so the employee
    /// filter below is applied client-side against whatever comes back for
    /// the day.
    ///
    /// rangeEnd is deliberately built from TimeOnly.MaxValue, not MinValue/
    /// midnight -- GetLogsAsync filters `Timestamp >= rangeStart &&
    /// Timestamp <= rangeEnd`, so a midnight upper bound would silently
    /// exclude every punch after 12:00 AM, leaving the list empty on almost
    /// every real day. Both rangeStart/rangeEnd are naive (Kind-agnostic)
    /// local-calendar-date boundaries built the same way
    /// MainViewModel.RefreshCalendarAttendanceStatusesAsync's own
    /// CalendarDays.Date values feed into AttendanceWorkflowService's
    /// periodStart/periodEnd -- and Timestamp itself is written as
    /// Philippine local time end to end (see
    /// PushListenerAttendanceLogInfo's own doc comment), never converted to
    /// or from UTC anywhere in that chain -- so this query lines up with
    /// the calendar's own day boundaries with no timezone conversion
    /// needed on either side.</summary>
    private async Task RefreshMachinePunchesAsync()
    {
        _machinePunchesCts?.Cancel();
        var cts = new CancellationTokenSource();
        _machinePunchesCts = cts;

        var employeeId = ResolveEmployeeId(EmployeeBox.Text);
        if (employeeId is null || DateBox.SelectedDate is not { } selectedDate)
        {
            ShowMachinePunchesUnresolved();
            return;
        }

        var date = DateOnly.FromDateTime(selectedDate);
        var rangeStart = date.ToDateTime(TimeOnly.MinValue);
        var rangeEnd = date.ToDateTime(TimeOnly.MaxValue);

        try
        {
            // Waits for whichever earlier call (if any) is still unwinding from its own
            // GetLogsAsync before this call starts its own -- see _machinePunchesGate's
            // own doc comment for why cancelling alone can't guarantee that on its own.
            // cts.Token here too: if a newer call has already superseded this one while
            // it was still waiting its turn, there's no point running the query at all.
            await _machinePunchesGate.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // A newer call already cancelled this one (see _machinePunchesCts's own
            // doc comment) while this one was waiting for the gate above -- nothing to
            // fetch for anymore.
            if (cts.IsCancellationRequested) return;

            // Deliberately CancellationToken.None here, NOT cts.Token -- this used to pass
            // cts.Token (see git history / the old comment this replaces), on the reasoning
            // that a prompt cancellation lets a superseded call free _machinePunchesGate
            // sooner. That's true, but it also means asking SqlClient to abort a command
            // that's actively reading against the shared, app-lifetime-scoped
            // ScheduleDbContext -- and a command cancelled mid-read on that connection can
            // come back as a raw SqlException ("Operation cancelled by user" / "A severe
            // error occurred on the current command") rather than a clean
            // OperationCanceledException, and can leave THAT SAME, reused-forever connection
            // unusable for every later query on it -- not just this one, every page sharing
            // it (see AttendanceBusyState's own doc comment on why there's only one
            // ScheduleDbContext for the whole app run). This fetch is a reference-only
            // decoration (see this dialog's own class doc comment) -- discarding a stale
            // result via the IsCancellationRequested checks around this call is exactly as
            // effective as aborting the query outright, without the risk of taking the
            // shared connection down with it. _machinePunchesGate's own WaitAsync(cts.Token)
            // above is unaffected -- that's cancelling a wait on a private, per-dialog
            // semaphore, not a live command on the shared connection, so it stays safe to
            // cancel promptly.
            var logs = await _attendanceLogRepository.GetLogsAsync(rangeStart, rangeEnd, cancellationToken: CancellationToken.None);

            // A newer call already cancelled this one (see _machinePunchesCts's own
            // doc comment) -- its own result, not this now-stale one, is what should
            // end up on screen.
            if (cts.IsCancellationRequested) return;

            var punches = logs
                .Where(log => log.EmployeeId == employeeId.Value)
                .OrderBy(log => log.Timestamp)
                .Select(log => $"{TimeDisplayFormat.Format(TimeOnly.FromDateTime(log.Timestamp))} \u2014 {PunchTypeLabel.ToText(log.PunchType)}")
                .ToList();

            ShowMachinePunches(punches);
        }
        catch
        {
            // Fail-soft, same philosophy as RefreshCalendarAttendanceStatusesAsync's
            // own best-effort catch -- this is a reference decoration, not something
            // that should ever block Save, so a failed fetch just swaps in an
            // explanatory placeholder instead of throwing out of a fire-and-forget
            // call.
            if (cts.IsCancellationRequested) return;
            ShowMachinePunchesError();
        }
        finally
        {
            _machinePunchesGate.Release();
        }
    }

    private void ShowMachinePunches(IReadOnlyList<string> punches)
    {
        MachinePunchesList.ItemsSource = punches;
        MachinePunchesPlaceholder.Text = "No machine punches for this day.";
        MachinePunchesPlaceholder.Visibility = punches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shown when there's no employee/date to fetch for yet (e.g. the
    /// Add-mode constructor's initial DateBox assignment, before anything's
    /// typed into EmployeeBox, or free-typed text that doesn't resolve to
    /// anyone) -- distinct from ShowMachinePunches([]) below, which means the
    /// fetch actually ran and genuinely found nothing. Conflating the two
    /// used to show "No machine punches for this day" before an employee was
    /// even picked, which reads as a checked-and-empty result rather than
    /// the "nothing to check yet" state it actually was.</summary>
    private void ShowMachinePunchesUnresolved()
    {
        MachinePunchesList.ItemsSource = null;
        MachinePunchesPlaceholder.Text = "Pick an employee to see that day's machine punches.";
        MachinePunchesPlaceholder.Visibility = Visibility.Visible;
    }

    private void ShowMachinePunchesError()
    {
        MachinePunchesList.ItemsSource = null;
        MachinePunchesPlaceholder.Text = "Couldn't load machine punches for this day.";
        MachinePunchesPlaceholder.Visibility = Visibility.Visible;
    }

    private void CommitSuggestion(string formattedEmployee)
    {
        _suppressEmployeeTextChanged = true;
        EmployeeBox.Text = formattedEmployee;
        _suppressEmployeeTextChanged = false;

        EmployeeBox.CaretIndex = EmployeeBox.Text.Length;
        EmployeeSuggestionsPopup.IsOpen = false;
        EmployeeBox.Focus();
    }

    /// <summary>Uses Pin (the Employee ID that's actually shown/typed
    /// and matched elsewhere in this dialog -- see ResolveEmployeeId), not
    /// Id (the database primary key). These were previously mismatched here:
    /// suggestions were formatted with Id while ResolveEmployeeId always
    /// checked the leading number against Pin, so picking a suggestion
    /// could fail to resolve on Add whenever the two differed for that
    /// employee.</summary>
    private static string FormatEmployee(Employee employee) => $"{employee.Pin} \u2014 {employee.DisplayName}";

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var employeeId = ResolveEmployeeId(EmployeeBox.Text);
        if (employeeId is null)
        {
            Warn("Pick an employee from the list (start typing an Employee ID or name).");
            return;
        }

        if (DateBox.SelectedDate is not { } date)
        {
            Warn("Select a date.");
            return;
        }

        if (!TimeDisplayFormat.TryParse(TimeCombo.Text, out var time))
        {
            Warn("Enter a valid time, e.g. 5:01 PM.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ReasonBox.Text))
        {
            Warn("Enter a reason -- e.g. \"Forgot to badge in\".");
            return;
        }

        EmployeeId = employeeId.Value;
        Timestamp = date.Date + time.ToTimeSpan();
        PunchType = PunchTypeCombo.SelectedIndex;
        Reason = ReasonBox.Text.Trim();
        EnteredBy = string.IsNullOrWhiteSpace(EnteredByBox.Text) ? Environment.UserName : EnteredByBox.Text.Trim();

        DialogResult = true;
    }

    /// <summary>Reads the Pin back out of whatever's currently in
    /// EmployeeBox -- either a picked "id — name" suggestion, or free-typed
    /// text. Only the leading integer is trusted, and only if it actually
    /// belongs to one of the employees this dialog was given; a typed name
    /// with no matching id, or one that's been edited into nonsense, returns
    /// null rather than guessing.</summary>
    private int? ResolveEmployeeId(string text)
    {
        var idPart = text.Split('\u2014', 2)[0].Trim();
        if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;

        return _employees.Any(emp => emp.Pin == id) ? id : null;
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "Check your entry", MessageBoxButton.OK, MessageBoxImage.Warning);
}
