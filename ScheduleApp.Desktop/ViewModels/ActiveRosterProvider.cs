using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>Caches the one Active-only roster shape -- GetActiveDepartmentsWithEmployeesAsync's
/// departments (each with its own Employees collection) paired with
/// GetActiveUnassignedEmployeesAsync's flat list -- that ReportScopeViewModel,
/// PayslipScopeViewModel, PayrollWizardViewModel, PayrollRunViewModel.LoadPayrollGroupAsync, and
/// PayrollGroupViewModel (RefreshFullRosterAsync and AddEmployeesToGroupAsync) each used to
/// re-fetch independently on every one of their own loads, even when nothing about the roster
/// had actually changed since the last of those six call sites ran. Same "one owner instead of
/// several call sites paying for the identical pair of queries" motivation as
/// AttendanceEmployeeDirectory (see that class's own doc comment), but gated on
/// AttendanceDataVersion.RosterVersion rather than piggybacking off a sibling's own load the way
/// AttendanceEmployeeDirectory.SeedCache does -- there's no single "whoever loads first, for
/// free" call site here the way Attendance's own tab-open sequence gives that class; ReportScope,
/// PayslipScope, the wizard, and Payroll Run/Group can each be the very first thing that runs in
/// a given session, in any order, so this instead caches its own result the first time *any* of
/// them calls in, and every consumer after that -- regardless of order -- gets served from that
/// cache until RosterVersion actually moves.
///
/// Returns the raw (Departments, Unassigned) pair rather than a flattened employee list --
/// unlike AttendanceEmployeeDirectory, whose every caller wants "everyone, flat" (see that
/// class's own doc comment), three of this class's five callers (ReportScopeViewModel,
/// PayslipScopeViewModel, PayrollWizardViewModel via EmployeeTreeBuilder, and
/// PayrollGroupViewModel.AddEmployeesToGroupAsync) need the Department-shaped tree to build
/// their own checkbox UI from, so flattening here would just make them undo it. The two callers
/// that do want a flat list (PayrollRunViewModel.LoadPayrollGroupAsync,
/// PayrollGroupViewModel.RefreshFullRosterAsync) already flatten it themselves with one
/// SelectMany + Concat, the same one line GetSelectedPins-style callers everywhere else in this
/// app already write.
///
/// Safe to share the same Department/Employee object graph across every caller without copying:
/// every repository read in this codebase uses AsNoTracking (see the codebase-wide audit this
/// class's own introduction was prompted by), so these are detached entities nobody here ever
/// mutates in place -- every write path goes back through IScheduleRepository, never through an
/// Employee/Department instance handed out by this class. A caller that wraps these in its own
/// EmployeeNodeViewModel/DepartmentGroupViewModel tree (ReportScopeViewModel, PayslipScopeViewModel,
/// PayrollWizardViewModel, PayrollGroupViewModel's picker tree) is free to give each of its own
/// wrapper instances independent IsSelected/IsVisible state -- those live on the wrapper, not on
/// the shared Employee/Department underneath, so two callers holding the same cached snapshot at
/// once never see each other's checkbox state.
///
/// _gate serializes the actual database round trip the same way AttendanceEmployeeDirectory's own
/// _dbGate does, for the same underlying reason (this app's shared, app-lifetime-scoped
/// ScheduleDbContext can't run two operations at once) -- but unlike that class, a cache hit here
/// never even reaches the gate: the version check just above WaitAsync is a plain, synchronous
/// field read, so a caller arriving while RosterVersion hasn't moved gets the cached snapshot back
/// with no await at all. Checked again after acquiring the gate (the usual double-checked-cache
/// shape) so two callers racing a genuine cache miss don't both pay for the round trip -- the
/// second one through blocks on the first, then reads the snapshot the first just installed
/// instead of firing a second, redundant pair of queries.</summary>
public sealed class ActiveRosterProvider(IScheduleRepository scheduleRepository, AttendanceDataVersion dataVersion)
    : IDisposable
{
    private readonly IScheduleRepository _scheduleRepository = scheduleRepository;
    private readonly AttendanceDataVersion _dataVersion = dataVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _cachedVersion = -1; // never matches RosterVersion's own starting value of 0 until a real fetch has happened
    private (List<Department> Departments, List<Employee> Unassigned)? _cache;

    public async Task<(List<Department> Departments, List<Employee> Unassigned)> GetAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cache is { } cached && _cachedVersion == _dataVersion.RosterVersion)
            return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check now that the gate is ours -- see this class's own doc comment for why:
            // whichever caller got here first may have already refreshed the cache while this
            // one was waiting, in which case there's nothing left to fetch.
            if (_cache is { } cachedAfterWait && _cachedVersion == _dataVersion.RosterVersion)
                return cachedAfterWait;

            var departments = await _scheduleRepository.GetActiveDepartmentsWithEmployeesAsync(cancellationToken);
            var unassigned = await _scheduleRepository.GetActiveUnassignedEmployeesAsync(cancellationToken);

            var result = (departments, unassigned);
            _cache = result;
            _cachedVersion = _dataVersion.RosterVersion;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases _gate. Registered AddScoped (see App.xaml.cs), and App only ever
    /// creates the one scope, so this runs when that scope is disposed at app exit.</summary>
    public void Dispose() => _gate.Dispose();
}
