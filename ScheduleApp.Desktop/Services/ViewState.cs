namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Everything the Schedule and Attendance tabs remember about where the person left
/// off -- but only *within* the current run, not across app restarts (see
/// ViewStateStore's own doc comment for that decision and why). Switching tabs within
/// one run keeps its state on its own regardless, since NavigationView reuses the same
/// cached page/ViewModel instance every time a tab is revisited in a single session (see
/// SchedulePage's and AttendancePage's own _loaded guards) -- that part doesn't depend on
/// this class at all. What this class actually provides is a *shared* copy of that state
/// (e.g. so Report/PunchRecords/ManualEntries can each see the same Attendance period),
/// not persistence.
/// </summary>
public class PersistedViewState
{
    public ScheduleViewState Schedule { get; set; } = new();
    public AttendanceViewState Attendance { get; set; } = new();
}

/// <summary>Last-used state for the Schedule tab.</summary>
public class ScheduleViewState
{
    /// <summary>Employee.Id (the database primary key, not Pin) of
    /// whoever was selected in the tree -- null if nobody was, or if that
    /// employee no longer exists come the next launch.</summary>
    public int? SelectedEmployeeId { get; set; }

    /// <summary>Department.Id of whichever department node was selected, if a
    /// department (rather than an employee) was the last thing highlighted.</summary>
    public int? SelectedDepartmentId { get; set; }

    /// <summary>First-of-month for whichever month the calendar was showing --
    /// reopens here rather than always jumping back to the current month.</summary>
    public DateTime? DisplayedMonth { get; set; }

    // Deliberately no IsMultiSelectMode here -- that's a transient "I'm about
    // to bulk-assign" mode, not a view worth reopening into. Restarting the
    // app already blank in that mode (with nothing checked, since checkboxes
    // aren't persisted either) would just be confusing.
}

/// <summary>Last-used state for the Attendance tab.</summary>
public class AttendanceViewState
{
    /// <summary>Generate Reports period.</summary>
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }

    /// <summary>Employee.Pin values checked in the report-scope tree.
    /// Null (or empty) means "everyone" -- mirrors
    /// ReportScopeViewModel.GetSelectedPins()'s own null-means-everyone
    /// convention, so a narrowed scope is only restored when the person
    /// actually narrowed it away from the whole-company default.</summary>
    public List<int>? SelectedPins { get; set; }

    /// <summary>Punch Records date range and search box.</summary>
    public DateTime? LogViewStart { get; set; }
    public DateTime? LogViewEnd { get; set; }
    public string? LogViewSearchText { get; set; }

    /// <summary>Manual Entries date range -- same shape as LogViewStart/LogViewEnd
    /// above, now that Manual Entries is scoped by date too instead of always showing
    /// every entry on file.</summary>
    public DateTime? ManualEntriesStart { get; set; }
    public DateTime? ManualEntriesEnd { get; set; }

    /// <summary>Which Attendance sub-tab (Summary, Punch Records, or Manual
    /// Entries) was open last. Only one of IsPunchRecordsTabSelected/
    /// IsManualEntriesTabSelected is ever true at once -- both false means
    /// Summary, mirroring the tab order in AttendanceView.xaml.</summary>
    public bool IsPunchRecordsTabSelected { get; set; }

    /// <summary>See IsPunchRecordsTabSelected's doc comment.</summary>
    public bool IsManualEntriesTabSelected { get; set; }
}
