using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Used for every RestDay ScheduleEntry -- a day an employee isn't required to
/// work at all, and can only be paid Rest Day Duty premium for (see Employee.
/// RestDayWorkPremiumPercentage) when a schedule was actually pre-set for it.
/// Status is always PunchStatus.RestDay, whether or not a duty was actually
/// recognized -- there's no separate "worked" vs. "unworked" status; only the
/// hours fields differ. Neither check ScheduleApp.Payroll.PayrollCalculator
/// makes reads Status: Worked_H &gt; 0 decides whether the Rest Day Pay
/// premium applies to a given day, and NightDiff_H &gt; 0 decides whether
/// Night Diff does.
///
/// Two modes, chosen by whether the ScheduleEntry carries a real
/// TimeIn/WorkTimeHours (schedule.TimeIn/schedule.WorkTimeHours both set) or
/// not -- see ScheduleEntry's own doc comments for why that pair, unlike
/// Leave, is optional-but-meaningful for RestDay. In the desktop dialog this
/// is what RestDayDutyCheckBox controls: checked writes both fields (this
/// mode), unchecked leaves both null (the other mode).
///
/// - No schedule (CalculateUnscheduledDay): a plain day off. Punches are
///   never looked at -- ClaimedPunches/UnclaimedPunches always come back
///   empty, the same "skip punch matching entirely" shape
///   LeaveShiftCalculationStrategy and OfficialBusinessShiftCalculationStrategy's
///   own blank (no-scheduled-window) case use -- so a punch recorded on an
///   unchecked Rest Day is never honored: no Worked_H, no premium, no matter
///   what was punched. It also isn't reported back as Orphaned the way a
///   punch this strategy did look at but declined to use would be; it falls
///   through to AttendanceWorkflowService's Unscheduled bucket instead, same
///   as a punch on any other day nothing expects one -- Leave/OfficialBusiness
///   included.
/// - Schedule set (CalculateWindowedDay): buffer/window matching against the
///   scheduled TimeIn/TimeOut, reusing SingleWindowShiftCalculationStrategy's
///   own buffer logic -- but only its Complete outcome (a punch found in
///   *both* the clock-in and clock-out windows) counts as a duty; a Partial
///   outcome (only one side found) counts as no duty here too, unlike
///   SingleWindowShiftCalculationStrategy itself, which still reports
///   whichever side it did find. No Remain_H/Undertime either way -- a Rest
///   Day was never a required workday, so falling short of an optional
///   expected window isn't a shortfall to dock.
///
/// Overtime_H/Remain_H/LateIn_T/EarlyOut_T are never populated in either
/// mode. In particular, hours past a scheduled window's end are not split
/// into a separate Overtime figure the way SingleWindow/SplitShift would --
/// they simply extend Worked_H to cover the whole actual clock-in-to-clock-out
/// span, and all of it prices through the one Rest Day Pay premium
/// (ScheduleApp.Payroll.PayrollCalculator) instead. ScheduleEntry.
/// OvertimeEligibleOverride/ApplyOvertimeRatePercentageOverride/
/// OvertimeRatePercentageOverride are consequently never read here.
///
/// NightDiff_H/NightDiff_T/NightDiffRatePercentageOverride, when set, reuse
/// the same NightDifferentialCalculator.ResolveEligible gate and
/// NightDifferentialCalculator.CalculateHours sum every other strategy
/// already uses -- only reachable through the windowed mode now, since the
/// unscheduled mode never looks at punches at all.
/// </summary>
internal sealed class RestDayShiftCalculationStrategy : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var employeeId = schedule.Employee?.Pin ?? schedule.EmployeeId;
        var employeeName = schedule.Employee?.DisplayName ?? "Unknown";
        var departmentName = schedule.Employee?.Department?.Name ?? "(Unassigned)";

        var (summary, claimed, unclaimed) = schedule.TimeIn is not null && schedule.WorkTimeHours is not null
            ? CalculateWindowedDay(schedule, employeePunches, policy, employeeId, employeeName, departmentName)
            : CalculateUnscheduledDay(schedule, employeePunches, policy, employeeId, employeeName, departmentName);

        return new ShiftCalculationResult
        {
            Summaries = [summary],
            ClaimedPunches = claimed,
            UnclaimedPunches = unclaimed,
        };
    }

    /// <summary>No time schedule set on the entry -- RestDayDutyCheckBox was
    /// left unchecked, so this is a plain day off. Punch matching is skipped
    /// entirely, the same "there's nothing to clock in or out of" shape
    /// LeaveShiftCalculationStrategy uses (and OfficialBusinessShiftCalculationStrategy's
    /// own no-scheduled-window branch): whatever was punched that day, if
    /// anything, is neither claimed nor scored -- it's not this schedule
    /// entry's concern, so it's left for AttendanceWorkflowService to bucket
    /// as Unscheduled like any other punch nothing expects. There is
    /// deliberately no ad-hoc/call-in fallback here -- a Rest Day only earns
    /// Rest Day Duty pay when a schedule was actually pre-set for it (see
    /// CalculateWindowedDay below).</summary>
    private static (AttendanceSummary Summary, List<AttendanceLog> Claimed, List<AttendanceLog> Unclaimed) CalculateUnscheduledDay(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy,
        int employeeId,
        string employeeName,
        string departmentName)
    {
        var summary = new AttendanceSummary
        {
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            Department = departmentName,
            ShiftDate = schedule.Date,
            ScheduleType = ScheduleType.RestDay,
            Status = PunchStatus.RestDay,
            Span = schedule.WorkTimeHours, // null here -- see OfficialBusinessShiftCalculationStrategy's
                                            // "keep the old blank shape rather than writing a misleading
                                            // Span of 0" for why this is set explicitly rather than left
                                            // at AttendanceSummary.Span's own 0.0m default.
        };

        return (summary, [], []);
    }

    /// <summary>Time schedule set on the entry -- buffer/window matching
    /// against the scheduled TimeIn/TimeOut, reusing
    /// SingleWindowShiftCalculationStrategy's own buffer logic (the entry's
    /// own ClockInBufferBeforeHours/etc. take priority over the policy-wide
    /// defaults, same as Normal). A duty exists only when a punch is found in
    /// *both* the clock-in and the clock-out window -- that strategy's own
    /// Complete outcome. Only one of the two found (its Partial outcome)
    /// counts as no duty here too, unlike SingleWindowShiftCalculationStrategy
    /// itself, which still reports whichever side it did find and leaves
    /// Worked_H at zero anyway -- so ClockIn/ClockOut/Worked_H/Worked_T/
    /// NightDiff_H here all stay at their zero/blank defaults whenever both
    /// sides aren't found, not just the hours fields.
    ///
    /// No clock-out grace-period capping and no Overtime_H -- a Rest Day was
    /// never a required workday, so there's no "shift end" to measure
    /// overtime against; a clock-out found later than the scheduled window
    /// (still within the clock-out buffer) simply extends Worked_H to cover
    /// the whole actual clock-in-to-clock-out span.</summary>
    private static (AttendanceSummary Summary, List<AttendanceLog> Claimed, List<AttendanceLog> Unclaimed) CalculateWindowedDay(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy,
        int employeeId,
        string employeeName,
        string departmentName)
    {
        var scheduledTimeIn = schedule.TimeIn!.Value;
        var scheduledTimeOut = schedule.TimeOut!.Value; // derived from TimeIn + WorkTimeHours, so non-null here
        var workTimeHours = schedule.WorkTimeHours!.Value;

        DateTime targetTimeInStart = schedule.Date.ToDateTime(scheduledTimeIn);
        DateTime targetTimeOutStart = schedule.CrossesMidnight
            ? schedule.Date.AddDays(1).ToDateTime(scheduledTimeOut)
            : schedule.Date.ToDateTime(scheduledTimeOut);

        // Same three-tier day/employee/policy cascade Normal uses -- see
        // NormalBufferResolver, shared with SingleWindowShiftCalculationStrategy.
        var (clockInBufferBefore, clockInBufferAfter, clockOutBufferBefore, clockOutBufferAfter) =
            NormalBufferResolver.Resolve(schedule, policy);

        DateTime minTimeIn = targetTimeInStart.AddHours(-clockInBufferBefore);
        DateTime maxTimeIn = targetTimeInStart.AddHours(clockInBufferAfter);
        DateTime minTimeOut = targetTimeOutStart.AddHours(-clockOutBufferBefore);
        DateTime maxTimeOut = targetTimeOutStart.AddHours(clockOutBufferAfter);

        // Device punches preferred; a manual entry only used when the window
        // has no device punch at all -- see PunchMatching. The exclude on
        // clockOutPunch guards against the same same-instant-double-pick bug
        // SingleWindowShiftCalculationStrategy guards against, for the same
        // reason (see its own comment on this line).
        var clockInPunch = PunchMatching.EarliestInWindow(employeePunches, minTimeIn, maxTimeIn);
        var clockOutPunch = PunchMatching.LatestInWindow(employeePunches, minTimeOut, maxTimeOut, exclude: clockInPunch);

        var claimed = new List<AttendanceLog>();
        if (clockInPunch is not null) claimed.Add(clockInPunch);
        if (clockOutPunch is not null) claimed.Add(clockOutPunch);

        var unclaimed = PunchMatching.AllInWindow(employeePunches, minTimeIn, maxTimeIn)
            .Concat(PunchMatching.AllInWindow(employeePunches, minTimeOut, maxTimeOut))
            .Distinct()
            .Except(claimed)
            .ToList();

        var summary = new AttendanceSummary
        {
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            Department = departmentName,
            ShiftDate = schedule.Date,
            ScheduleType = ScheduleType.RestDay,
            Status = PunchStatus.RestDay,
            HasScheduledWindow = true,
            CheckIn = scheduledTimeIn,
            CheckOut = scheduledTimeOut,
            Span = workTimeHours,
        };

        if (clockInPunch is null || clockOutPunch is null)
        {
            // No duty recognized -- see the class doc comment. Punches found
            // for only one side are still Claimed above (so they're not
            // reported as Orphaned/Unscheduled), but nothing about them
            // surfaces on the summary itself.
            return (summary, claimed, unclaimed);
        }

        summary.ClockIn = TimeOnly.FromDateTime(TruncateToMinute(clockInPunch.Timestamp));
        summary.ClockInIsManual = clockInPunch.Source == AttendanceLogSource.Manual;
        summary.ClockOut = TimeOnly.FromDateTime(TruncateToMinute(clockOutPunch.Timestamp));
        summary.ClockOutIsManual = clockOutPunch.Source == AttendanceLogSource.Manual;

        DateTime effectiveTimeIn = clockInPunch.Timestamp;
        if (policy.CapEarlyClockIn && effectiveTimeIn < targetTimeInStart)
        {
            effectiveTimeIn = targetTimeInStart;
        }

        // No grace-period capping on the clock-out side -- see the class doc
        // comment: hours past the scheduled window's end just extend
        // Worked_H, never split off into Overtime_H.
        DateTime effectiveTimeOut = clockOutPunch.Timestamp;

        effectiveTimeIn = TruncateToMinute(effectiveTimeIn);
        effectiveTimeOut = TruncateToMinute(effectiveTimeOut);

        if (effectiveTimeOut <= effectiveTimeIn)
        {
            return (summary, claimed, unclaimed);
        }

        double totalHours = (effectiveTimeOut - effectiveTimeIn).TotalHours;
        summary.Worked_H = totalHours;
        summary.Worked_T = TimeSpan.FromHours(totalHours);

        if (NightDifferentialCalculator.ResolveEligible(schedule))
        {
            summary.NightDiff_H = NightDifferentialCalculator.CalculateHours(
                effectiveTimeIn, effectiveTimeOut, policy.NightDiffStart, policy.NightDiffEnd);
            summary.NightDiff_T = TimeSpan.FromHours(summary.NightDiff_H);
            summary.NightDiffRatePercentageOverride = schedule.NightDiffRatePercentageOverride;
        }

        return (summary, claimed, unclaimed);
    }

    private static DateTime TruncateToMinute(DateTime dt) =>
        new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
}
