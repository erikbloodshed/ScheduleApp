using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Matches actual punches against one continuous scheduled window -- used for
/// every Normal ScheduleEntry, which is always a single TimeIn/WorkTimeHours
/// window (there's no segmented/split-shift schedule type in ScheduleApp).
/// </summary>
internal sealed class SingleWindowShiftCalculationStrategy : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var (summary, claimed, unclaimed) = CalculateOne(schedule, employeePunches, policy);
        return new ShiftCalculationResult
        {
            Summaries = [summary],
            ClaimedPunches = claimed,
            UnclaimedPunches = unclaimed,
        };
    }

    private static (AttendanceSummary Summary, List<AttendanceLog> Claimed, List<AttendanceLog> Unclaimed) CalculateOne(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var employeeName = schedule.Employee?.DisplayName ?? "Unknown";
        var departmentName = schedule.Employee?.Department?.Name ?? "(Unassigned)";
        var employeeId = schedule.Employee?.Pin ?? schedule.EmployeeId;

        // A Normal entry should always have both set (the Set Schedule dialog
        // requires them), but a malformed/imported row without them has no
        // window to compare punches against -- report Absent rather than throw.
        // No window at all means nothing to consider, so both punch lists stay
        // empty rather than trying to guess a window.
        if (schedule.TimeIn is not { } scheduledTimeIn || schedule.WorkTimeHours is not { } workTimeHours)
        {
            var absentSummary = new AttendanceSummary
            {
                EmployeeId = employeeId,
                EmployeeName = employeeName,
                Department = departmentName,
                ShiftDate = schedule.Date,
                ScheduleType = ScheduleType.Normal,
                Status = PunchStatus.Absent,
            };
            return (absentSummary, [], []);
        }

        var scheduledTimeOut = schedule.TimeOut!.Value; // derived from TimeIn + WorkTimeHours, so non-null here
        double scheduledWorkHours = (double)workTimeHours;

        DateTime targetTimeInStart = schedule.Date.ToDateTime(scheduledTimeIn);
        DateTime targetTimeOutStart = schedule.CrossesMidnight
            ? schedule.Date.AddDays(1).ToDateTime(scheduledTimeOut)
            : schedule.Date.ToDateTime(scheduledTimeOut);

        // The entry's own ClockInBufferBeforeHours/.../ClockOutBufferAfterHours
        // (see ScheduleEntry) take priority over the employee's own default (see
        // Employee.ClockInBufferBeforeHours), which in turn takes priority over
        // the policy-wide defaults -- null at each tier falls through to the
        // next, one field at a time. Same pattern as FlexibleSegment's
        // per-segment buffer overrides, just scoped to the whole entry instead
        // of a child row, since Normal has no segments to hang them off of. See
        // NormalBufferResolver for the shared three-tier resolution itself.
        var (clockInBufferBefore, clockInBufferAfter, clockOutBufferBefore, clockOutBufferAfter) =
            NormalBufferResolver.Resolve(schedule, policy);

        DateTime minTimeIn = targetTimeInStart.AddHours(-clockInBufferBefore);
        DateTime maxTimeIn = targetTimeInStart.AddHours(clockInBufferAfter);
        DateTime minTimeOut = targetTimeOutStart.AddHours(-clockOutBufferBefore);
        DateTime maxTimeOut = targetTimeOutStart.AddHours(clockOutBufferAfter);

        // Device punches are preferred; a manual entry (AttendanceLogSource.Manual,
        // see ManualAttendanceLog) is only used when the window has no device
        // punch at all -- see PunchMatching.
        //
        // exclude: clockInPunch matters whenever the clock-in and clock-out
        // buffer windows overlap (i.e. the shift is shorter than
        // ClockInBufferAfter + ClockOutBufferBefore) -- otherwise a single
        // punch that falls in the overlap (e.g. an employee who clocked in
        // but forgot to clock out) gets picked as *both* clockInPunch and
        // clockOutPunch. That collapses to a same-instant window, which
        // reports Status=Complete with 0 worked hours instead of Partial --
        // silently hiding exactly the missed-punch case this window-based
        // (not punch-status-based) matching exists to tolerate. Flexible's
        // CalculateSegment already excludes this way; Normal needs the same
        // guard for the same reason.
        var clockInPunch = PunchMatching.EarliestInWindow(employeePunches, minTimeIn, maxTimeIn);
        var clockOutPunch = PunchMatching.LatestInWindow(employeePunches, minTimeOut, maxTimeOut, exclude: clockInPunch);

        // Every punch either window actually looked at, minus whichever ended
        // up picked -- e.g. a duplicate device tap a minute after the real
        // clock-in, or a manual entry the device punch took priority over
        // (see PunchMatching). Reported back so AttendanceWorkflowService can
        // flag these as Orphaned rather than silently discarding them the way
        // this strategy always has.
        var claimed = new List<AttendanceLog>();
        if (clockInPunch is not null) claimed.Add(clockInPunch);
        if (clockOutPunch is not null) claimed.Add(clockOutPunch);

        var unclaimed = PunchMatching.AllInWindow(employeePunches, minTimeIn, maxTimeIn)
            .Concat(PunchMatching.AllInWindow(employeePunches, minTimeOut, maxTimeOut))
            .Distinct()
            .Except(claimed)
            .ToList();

        var actualTimeIn = (DateTime?)clockInPunch?.Timestamp;
        var actualTimeOut = (DateTime?)clockOutPunch?.Timestamp;

        var summary = new AttendanceSummary
        {
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            Department = departmentName,
            ShiftDate = schedule.Date,
            ScheduleType = ScheduleType.Normal,
            HasScheduledWindow = true,
            CheckIn = scheduledTimeIn,
            CheckOut = scheduledTimeOut,
            Span = workTimeHours,
        };

        if (actualTimeIn.HasValue)
        {
            summary.ClockIn = TimeOnly.FromDateTime(TruncateToMinute(actualTimeIn.Value));
            summary.ClockInIsManual = clockInPunch!.Source == AttendanceLogSource.Manual;
        }

        if (actualTimeOut.HasValue)
        {
            summary.ClockOut = TimeOnly.FromDateTime(TruncateToMinute(actualTimeOut.Value));
            summary.ClockOutIsManual = clockOutPunch!.Source == AttendanceLogSource.Manual;
        }

        if (!actualTimeIn.HasValue && !actualTimeOut.HasValue)
        {
            summary.Status = PunchStatus.Absent;
            return (summary, claimed, unclaimed);
        }

        if (!actualTimeIn.HasValue || !actualTimeOut.HasValue)
        {
            summary.Status = PunchStatus.Partial;
            return (summary, claimed, unclaimed);
        }

        summary.Status = PunchStatus.Complete;

        DateTime effectiveTimeIn = actualTimeIn.Value;
        if (policy.CapEarlyClockIn && effectiveTimeIn < targetTimeInStart)
        {
            effectiveTimeIn = targetTimeInStart;
        }

        DateTime effectiveTimeOut = actualTimeOut.Value;
        if (effectiveTimeOut >= targetTimeOutStart && effectiveTimeOut < targetTimeOutStart.AddHours(policy.ClockOutGracePeriod))
        {
            effectiveTimeOut = targetTimeOutStart;
        }

        effectiveTimeIn = TruncateToMinute(effectiveTimeIn);
        effectiveTimeOut = TruncateToMinute(effectiveTimeOut);

        if (effectiveTimeOut <= effectiveTimeIn)
        {
            return (summary, claimed, unclaimed);
        }

        double totalHours = (effectiveTimeOut - effectiveTimeIn).TotalHours;
        summary.WorkedHours = totalHours;
        summary.WorkedDuration = TimeSpan.FromHours(totalHours);

        // Employee.QualifiesForNightDiff -- e.g. false for managerial/supervisory
        // staff, who under PH Labor Code Art. 82 aren't entitled to night
        // differential pay even though they still clock in and out normally.
        // WorkedDuration above is unaffected; only this figure is suppressed to zero.
        // ScheduleEntry.NightDiffEligibleOverride takes priority when set (a
        // per-day exception); missing Employee (shouldn't normally happen)
        // defaults to eligible, same as before this override existed.
        if (NightDifferentialCalculator.ResolveEligible(schedule))
        {
            summary.NightDiffHours = NightDifferentialCalculator.CalculateHours(
                effectiveTimeIn, effectiveTimeOut, policy.NightDiffStart, policy.NightDiffEnd);
            summary.NightDiffDuration = TimeSpan.FromHours(summary.NightDiffHours);
            summary.NightDiffRatePercentageOverride = schedule.NightDiffRatePercentageOverride;
        }

        // A punch within policy.LateInEarlyOutGraceMinutes of the scheduled start/end
        // is treated as exactly on time -- LateInDuration/EarlyOutDuration stay zero rather than
        // reporting a couple minutes' slack. Past the grace period, the *entire*
        // difference counts, not just the amount beyond it -- see
        // AttendancePolicy.LateInEarlyOutGraceMinutes's own doc comment.
        if (effectiveTimeIn > targetTimeInStart &&
            (effectiveTimeIn - targetTimeInStart).TotalMinutes > policy.LateInEarlyOutGraceMinutes)
        {
            summary.LateInDuration = effectiveTimeIn - targetTimeInStart;
        }

        if (effectiveTimeOut < targetTimeOutStart &&
            (targetTimeOutStart - effectiveTimeOut).TotalMinutes > policy.LateInEarlyOutGraceMinutes)
        {
            summary.EarlyOutDuration = targetTimeOutStart - effectiveTimeOut;
        }

        summary.RemainDuration = summary.LateInDuration + summary.EarlyOutDuration;
        summary.RemainHours = summary.RemainDuration.TotalHours;

        // ScheduleEntry.OvertimeEligibleOverride / Employee.QualifiesForOvertime --
        // same PH Labor Code Art. 82 exemption as NightDiff above, applied
        // independently, plus this revision's per-day exception on top. Skipping
        // the whole block when not qualified (rather than computing overtimeHours
        // and then discarding it) avoids doing the work for an employee/day it'll
        // never apply to.
        if (ResolveOvertimeEligible(schedule) &&
            effectiveTimeOut >= targetTimeOutStart.AddHours(policy.ClockOutGracePeriod))
        {
            double overtimeHours = 0;

            if (policy.StrictOvertimeFromShiftEnd)
            {
                overtimeHours = (effectiveTimeOut - targetTimeOutStart).TotalHours;
            }
            else if (totalHours > scheduledWorkHours)
            {
                overtimeHours = totalHours - scheduledWorkHours;
            }

            if (overtimeHours > 0)
            {
                summary.OvertimeHours = overtimeHours;
                summary.OvertimeDuration = TimeSpan.FromHours(summary.OvertimeHours);
                summary.OvertimeRatePercentageOverride = schedule.OvertimeRatePercentageOverride;
                summary.ApplyOvertimeRatePercentage = ResolveApplyOvertimeRatePercentage(schedule);
            }
        }

        return (summary, claimed, unclaimed);
    }

    /// <summary>ScheduleEntry.OvertimeEligibleOverride takes priority when set (a
    /// per-day exception -- see Section 5/6.3 of the payroll refactor plan);
    /// missing override falls back to Employee.QualifiesForOvertime, and missing
    /// Employee (shouldn't normally happen) defaults to eligible.</summary>
    private static bool ResolveOvertimeEligible(ScheduleEntry schedule) =>
        schedule.OvertimeEligibleOverride ?? schedule.Employee?.QualifiesForOvertime ?? true;

    /// <summary>Same idea as <see cref="ResolveOvertimeEligible"/>, but for
    /// ScheduleEntry.ApplyOvertimeRatePercentageOverride /
    /// Employee.ApplyOvertimeRatePercentageByDefault -- the separate "does the
    /// premium actually apply, once eligible" toggle. No Night Diff
    /// equivalent -- see AttendanceSummary.ApplyOvertimeRatePercentage's doc
    /// comment.</summary>
    private static bool ResolveApplyOvertimeRatePercentage(ScheduleEntry schedule) =>
        schedule.ApplyOvertimeRatePercentageOverride ?? schedule.Employee?.ApplyOvertimeRatePercentageByDefault ?? true;

    private static DateTime TruncateToMinute(DateTime dt)
    {
        return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
    }
}
