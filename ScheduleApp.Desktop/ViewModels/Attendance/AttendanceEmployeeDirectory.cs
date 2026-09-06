using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Employee-roster lookups shared by every Attendance child ViewModel that needs
/// "everyone, flat" rather than the Department/Employee tree shape ReportScopeViewModel
/// builds (see EmployeeTreeBuilder) -- ManualEntryEditorViewModel's Add/Edit dialogs,
/// PunchRecordsViewModel's Load/Export/autosuggest, and ManualEntriesViewModel's grid all
/// used to call the same two private methods on the old monolithic AttendanceViewModel;
/// this is that code, unchanged, now with one owner instead of five call sites.
///
/// GetAllAsync always re-reads the database fresh -- an employee could have been added or
/// edited since the last call -- and piggybacks a refresh of the suggestion cache as a
/// side effect, since a caller that already has a fresh list in hand shouldn't make the
/// suggestion box do a separate round trip for the same data. GetForSuggestionsAsync is
/// the one exception that's actually cached: it backs the Punch Records search box's
/// autosuggest-while-typing, which fires on every keystroke and can't afford a DB round
/// trip each time. GetAllIncludingBlacklistedAsync is the one exception that isn't
/// Active-only -- see its own doc comment for why a blacklisted employee still needs to
/// be resolvable here even though every *picker* use of this class stays Active-only.
///
/// _dbGate serializes every GetAllAsync/GetAllIncludingBlacklistedAsync call against every
/// other one, regardless of caller. The app relies on AttendanceBusyState.IsRunning to keep
/// any two commands from touching the shared, app-lifetime-scoped ScheduleDbContext at the
/// same time (see AttendanceViewModel.InitializeAsync and
/// PunchRecordsViewModel.OnLogViewSearchTextChanged for two places that guard specifically
/// for this), but that's a convention every caller has to remember to follow, not something
/// this class can enforce on its own -- a future caller that reaches GetAllAsync() or
/// GetAllIncludingBlacklistedAsync() (directly, or indirectly through
/// GetForSuggestionsAsync's own fallback) without checking IsRunning first would silently
/// reintroduce the same race. The gate makes that impossible here specifically: a second
/// concurrent call just waits for the first to finish (and gets its own fresh read
/// afterwards, never the first call's result) rather than firing a second EF Core
/// operation on the same DbContext instance, which EF Core doesn't support and throws on.
/// This doesn't (and can't) protect against this class's DbContext use racing some
/// *other* repository's call -- e.g. IAttendanceLogRepository.GetLogsAsync, or
/// ReportScopeViewModel's own direct IScheduleRepository calls -- that's still on
/// IsRunning to prevent.</summary>
public sealed class AttendanceEmployeeDirectory(IScheduleRepository scheduleRepository)
{
    private readonly IScheduleRepository _scheduleRepository = scheduleRepository;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private List<Employee>? _suggestionCache;
    private Task<List<Employee>>? _suggestionLoadTask;

    public async Task<List<Employee>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var departments = await _scheduleRepository.GetActiveDepartmentsWithEmployeesAsync(cancellationToken);
            var unassigned = await _scheduleRepository.GetActiveUnassignedEmployeesAsync(cancellationToken);
            var employees = departments.SelectMany(d => d.Employees).Concat(unassigned).ToList();

            _suggestionCache = employees; // Piggyback a fresh suggestion universe off this fetch.
            return employees;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>Same shape as GetAllAsync, but via GetDepartmentsWithEmployeesAsync/
    /// GetUnassignedEmployeesAsync (blacklisted employees included) instead of the
    /// Active-only pair -- for the one job GetAllAsync itself isn't right for: resolving
    /// the Employee Name/Department columns on punch/manual-entry rows that already exist
    /// (PunchRecordsViewModel's Load/Export, ManualEntriesViewModel's Load/Export, via
    /// StoredPunchLogRowFactory.BuildEmployeeInfoByPin). Those rows aren't filtered by
    /// employee status to begin with -- QueryStoredLogsInRangeAsync/
    /// QueryFilteredManualEntriesAsync return everything in the date range regardless --
    /// so building their name lookup off GetAllAsync's Active-only list would leave a
    /// blacklisted employee's own pre-existing history on screen (and in the exported
    /// workbook) with a blank name and "(no matching employee)" the moment they're
    /// blacklisted, even though Employee.IsBlacklisted's own doc comment promises that
    /// history stays intact. Every *picker* use of employees stays on GetAllAsync/
    /// GetForSuggestionsAsync above unchanged -- there's no reason to offer a blacklisted
    /// employee as a choice when adding a new manual entry or autosuggesting a search
    /// term, only to keep a name resolvable for a row that already exists. Doesn't
    /// piggyback the suggestion cache the way GetAllAsync does -- GetForSuggestionsAsync
    /// is deliberately Active-only, so a call here shouldn't silently widen it.</summary>
    public async Task<List<Employee>> GetAllIncludingBlacklistedAsync(CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var departments = await _scheduleRepository.GetDepartmentsWithEmployeesAsync(cancellationToken);
            var unassigned = await _scheduleRepository.GetUnassignedEmployeesAsync(cancellationToken);
            return [.. departments.SelectMany(d => d.Employees), .. unassigned];
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>Used only by the autosuggest-while-typing feature (see
    /// PunchRecordsViewModel.UpdateLogViewSuggestionsAsync, the only caller) -- returns
    /// whatever's cached from a prior GetAllAsync call without a DB round trip, or does
    /// its own one-time fetch (also caching) if nothing's cached yet. Guards against
    /// duplicate concurrent loads the same way the old _employeeSuggestionLoadTask
    /// field did, since several keystrokes can race in before the first resolves --
    /// _dbGate above is what keeps that fallback fetch from racing some *other* caller's
    /// GetAllAsync, this is just about not starting redundant fetches of its own. No
    /// CancellationToken parameter, deliberately: the caller only ever invokes this while
    /// AttendanceBusyState.IsRunning is false (see OnLogViewSearchTextChanged), so there's
    /// no in-flight operation's token to pass through here in the first place.</summary>
    public Task<List<Employee>> GetForSuggestionsAsync()
    {
        if (_suggestionCache is not null)
            return Task.FromResult(_suggestionCache);

        return _suggestionLoadTask ??= GetAllAsync();
    }

    /// <summary>Seeds the suggestion cache from a list already fetched elsewhere, with no
    /// DB call of its own -- used by AttendanceViewModel.InitializeAsync right after
    /// ReportScope.LoadEmployeeTreeCommand, whose own GetActiveDepartmentsWithEmployeesAsync/
    /// GetActiveUnassignedEmployeesAsync calls fetch the exact same Active-only roster
    /// GetAllAsync would. Calling GetAllAsync there too would mean paying for that identical
    /// pair of queries twice on every single Attendance-tab open, just to warm this cache --
    /// this gets GetForSuggestionsAsync the same result GetAllAsync's piggyback would have
    /// produced, for free. Only ever pass a list built the same Active-only way GetAllAsync
    /// builds one (see ReportScopeViewModel.LoadedEmployees) -- seeding from
    /// GetAllIncludingBlacklistedAsync's shape here would let a blacklisted employee leak
    /// into autosuggest.</summary>
    public void SeedCache(IReadOnlyList<Employee> employees) => _suggestionCache = [.. employees];
}
