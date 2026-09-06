namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>One row in the Punch Records search box's autosuggestion dropdown -- either
/// a whole department or one specific employee. PunchRecordsViewModel.
/// BuildSuggestionMatches builds these; SelectLogViewSuggestion is what runs when one is
/// picked.
///
/// Display and InsertValue are deliberately not always the same text -- see each
/// property's own remarks -- which is the whole reason this is its own type instead of
/// LogViewSuggestions just staying an ObservableCollection&lt;string&gt; the way it was
/// before. A dropdown that only ever offered disconnected fragments ("Cruz", "Juan", or
/// "Kitchen" as separate entries) gave no way to tell two different employees named Cruz
/// apart, or to pick one of them specifically rather than just adding another loose
/// fragment to the filter.</summary>
public sealed class PunchSearchSuggestion
{
    public PunchSearchSuggestion(string display, string insertValue)
    {
        Display = display;
        InsertValue = insertValue;
    }

    /// <summary>What's shown in the dropdown. ListBox has no DataTemplate/
    /// DisplayMemberPath set for this list (see AttendanceView.xaml) -- WPF falls back to
    /// ToString() below for a plain CLR item like this one, so this is also, in effect,
    /// what actually renders.</summary>
    public string Display { get; }

    /// <summary>What SelectLogViewSuggestion actually writes into LogViewSearchText --
    /// for a department, the department name itself; for an employee, their DisplayName
    /// ("LastName; FirstName") alone, without the department shown alongside it in
    /// Display. Department is left out of the inserted value deliberately: since
    /// LogViewSearchText only ever holds one value at a time (see its own doc comment),
    /// writing "Cruz; Juan -- Kitchen" in as that one value would look for literal text
    /// no row could ever contain -- DisplayName alone is what FilterStoredLogRow actually
    /// matches one employee's rows against.</summary>
    public string InsertValue { get; }

    public override string ToString() => Display;
}
