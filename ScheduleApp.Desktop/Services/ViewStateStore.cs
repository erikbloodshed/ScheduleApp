namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Holds <see cref="PersistedViewState"/> for the Schedule and Attendance tabs --
/// in memory, for the current run only. Registered as a singleton (see App.xaml.cs), so
/// the one instance is shared for as long as the process lives: whatever
/// EmployeeTreeViewModel/ScheduleCalendarViewModel (Schedule) or ReportScopeViewModel/
/// PunchRecordsViewModel/ManualEntriesViewModel (Attendance) write into it via Save()
/// stays visible to every one of them for the rest of the session, the same "one shared
/// copy" reasoning AttendanceBusyState's own doc comment gives for itself.
///
/// Deliberately goes no further than that. This used to also round-trip to a JSON file at
/// %LocalAppData%\ScheduleApp\viewstate.json, so the same state survived a close-and-reopen
/// too -- that's been removed. Every launch now starts from a blank PersistedViewState, so
/// Schedule/Attendance fall through to their own built-in defaults every time (no employee
/// or department selected, current month for Schedule; current cutoff period for
/// Attendance's Report, Punch Records, and Manual Entries sub-tabs alike, Summary as the
/// open sub-tab) -- the same "always recompute from DateTime.Today, never prefer a stale
/// saved value" behavior Payroll's own period default already had (see PayrollScopeState's
/// doc comment). A saved period surviving a relaunch was a real correctness risk
/// specifically for date-range state: reopening the app after the real-world date has
/// crossed into the next cutoff should show *that* cutoff, not whichever half-month
/// happened to be on screen when the app last closed.
///
/// Any leftover viewstate.json from a build before this change is simply never read again
/// -- an orphaned file, harmless, not worth adding cleanup code to delete.
/// </summary>
public class ViewStateStore : IDisposable
{
    private readonly PersistedViewState _state = new();

    public ScheduleViewState Schedule => _state.Schedule;
    public AttendanceViewState Attendance => _state.Attendance;

    /// <summary>No-op -- see class doc comment. Kept as a real method, rather than removed,
    /// so none of Schedule's/Attendance's own call sites (a tab switch, a date-picker drag,
    /// a search-box keystroke -- each of PeriodStart/PeriodEnd/LogViewStart/LogViewEnd/
    /// LogViewSearchText/tab-selected has its own OnChanged handler calling this) needed to
    /// change: they still mutate Schedule/Attendance and call Save() exactly as before, it
    /// simply no longer does anything.</summary>
    public void Save()
    {
    }

    /// <summary>No-op -- nothing to flush now that Save() never schedules a write. Kept,
    /// like Save() above, so the DI container's ServiceProvider.Dispose() at app exit (this
    /// is a singleton -- see App.xaml.cs's registration) still has a real IDisposable to
    /// call rather than needing App.xaml.cs to change.</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
