using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Resolves the four Normal-shift search-window buffers (ClockInBufferBefore/
/// After, ClockOutBufferBefore/After) through their three-tier priority: a
/// specific ScheduleEntry's own override wins when set; failing that, the
/// Employee's own default wins when set; failing that, the app-wide
/// AttendancePolicy default applies. Shared by SingleWindowShiftCalculationStrategy
/// (Normal) and RestDayShiftCalculationStrategy (RestDay's windowed sub-case,
/// which reuses the exact same buffer-window matching) -- same "centralize once
/// a second caller needs the exact same cascade" reasoning as
/// NightDifferentialCalculator.ResolveEligible, just for these four numbers
/// instead of one eligibility flag.
/// </summary>
internal static class NormalBufferResolver
{
    /// <summary>Resolves all four buffers for one schedule entry at once, since
    /// every caller needs all four together. schedule.Employee is read directly
    /// (not passed separately) -- same convention ResolveOvertimeEligible/
    /// NightDifferentialCalculator.ResolveEligible already use, and just as safe
    /// here: every caller reaches this only after loading schedule entries with
    /// Employee included (see AttendanceWorkflowService), so a null Employee here
    /// would mean that Include was skipped, not a genuinely unmatched employee --
    /// same reasoning as SingleWindowShiftCalculationStrategy's own defensive
    /// null check on schedule.Employee elsewhere. A missing Employee simply falls
    /// straight through to the policy default, same as an Employee whose own
    /// buffer fields are all null.</summary>
    public static (double ClockInBefore, double ClockInAfter, double ClockOutBefore, double ClockOutAfter) Resolve(
        ScheduleEntry schedule, AttendancePolicy policy)
    {
        var employee = schedule.Employee;

        return (
            schedule.ClockInBufferBeforeHours ?? employee?.ClockInBufferBeforeHours ?? policy.ClockInBufferBefore,
            schedule.ClockInBufferAfterHours ?? employee?.ClockInBufferAfterHours ?? policy.ClockInBufferAfter,
            schedule.ClockOutBufferBeforeHours ?? employee?.ClockOutBufferBeforeHours ?? policy.ClockOutBufferBefore,
            schedule.ClockOutBufferAfterHours ?? employee?.ClockOutBufferAfterHours ?? policy.ClockOutBufferAfter
        );
    }
}
