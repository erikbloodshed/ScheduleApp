using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels.Payroll;

/// <summary>The four "what are we looking at right now" properties every Payroll-tab
/// concern reads and/or writes -- PeriodStart/PeriodEnd (the itemized breakdown, the
/// attendance grid underneath it, the Payroll Group grid, and batch print/export all
/// recompute off the same date range), BatchScopeEmployees (the employee roster most
/// recently confirmed via "New Payroll Run…"/"Load Payroll Group…"), and
/// ActivePayrollRunId (which saved PayrollRun, if any, that scope was confirmed against).
/// See PayrollViewModel_Refactor_Plan.md's "Target shape" section -- this class is
/// build-order step 1 of that plan, and the Payroll equivalent of AttendanceDataVersion
/// (see AttendanceSharedState.cs): a small, deliberately dumb ObservableObject that only
/// holds shared state and raises PropertyChanged, with no reaction logic of its own.
///
/// Deliberately still holds no behavior even now that PayrollViewModel is its only
/// reader/writer -- every reaction (RequestRefresh, RequestPayrollGroupRefresh, the
/// group-membership commands' CanExecute, and so on) stays on PayrollViewModel, which
/// subscribes to this class's PropertyChanged once in its own constructor and runs its own
/// reaction per property, the same "each subscriber runs its own reaction independently"
/// shape AttendanceBusyState's own doc comment describes for its multiple subscribers.
/// Once PayrollSummaryViewModel/PayrollGroupViewModel/PayrollRunViewModel/
/// PayrollPrintExportViewModel exist (build-order steps 2-5), each of them takes this same
/// instance in its own constructor and subscribes independently the same way
/// PayrollViewModel does here -- nothing about this class needs to change for that to
/// happen, only who's listening.
///
/// PeriodStart/PeriodEnd genuinely belong to all four eventual children (unlike
/// Attendance, where each tab has its own date range) -- that's the actual reason these
/// four live in shared state at all rather than being split apart along with everything
/// else; see the refactor plan's own "Target shape" section for the fuller reasoning.
/// </summary>
public partial class PayrollScopeState : ObservableObject
{
    /// <summary>periodStart/periodEnd are assigned directly to the backing fields here,
    /// bypassing the generated property setters below, so constructing this with today's
    /// default half-month range doesn't raise a PropertyChanged that nothing is
    /// subscribed to catch yet -- the same reason PayrollViewModel's own constructor used
    /// to assign its periodStart/periodEnd backing fields directly, before those fields
    /// lived here (see that constructor's own comment for the full "avoid a stale-name
    /// flicker on first load" story this sidesteps).</summary>
    public PayrollScopeState(DateTime periodStart, DateTime periodEnd)
    {
        this.periodStart = periodStart;
        this.periodEnd = periodEnd;
    }

    [ObservableProperty]
    private DateTime periodStart;

    [ObservableProperty]
    private DateTime periodEnd;

    /// <summary>See PayrollViewModel.BatchScopeEmployees' own doc comment (still the
    /// facade every reaction and every XAML binding goes through) for what this actually
    /// represents and who writes it -- this field is only the storage.</summary>
    [ObservableProperty]
    private IReadOnlyList<Employee> batchScopeEmployees = [];

    /// <summary>See PayrollViewModel.ActivePayrollRunId's own doc comment.</summary>
    [ObservableProperty]
    private int? activePayrollRunId;
}
