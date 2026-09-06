using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Used for every SplitShift ScheduleEntry. ScheduleEntry.FlexibleSegments
/// holds any number of (TimeIn, TimeOut) windows for the day (e.g.
/// 05:00-09:00 and 13:00-17:00 for a split shift), each evaluated as its own
/// scheduled window -- but unlike a Normal shift, which searches one combined
/// buffer window for both its clock-in and clock-out, a segment searches
/// *two independent* buffer windows: one around its own start
/// (+/-FlexibleSegment.ClockInBufferHours, falling back to
/// +/-AttendancePolicy.FlexibleSegmentClockInBuffer when that's null) for
/// its clock-in, and one around its own end
/// (+/-FlexibleSegment.ClockOutBufferHours, same fallback, against
/// AttendancePolicy.FlexibleSegmentClockOutBuffer) for its clock-out --
/// letting one unusual segment override just its own buffer(s) without
/// changing the policy default every other segment still inherits. This is
/// deliberate: a segment can easily see only one
/// punch (e.g. the employee's actual in/out for the day doesn't line up with
/// this particular segment's boundaries at all), and a single combined
/// window would then report that one punch as *both* the clock-in and the
/// clock-out -- a real bug the two-window design specifically avoids, since
/// a single punch can only ever fall in (and satisfy) one of the two roles.
/// Whichever of clock-in/clock-out isn't found for a segment is left blank
/// (not filled in with the other side's punch) -- see CalculateSegment.
/// Each independent window is only clamped against a neighboring segment
/// when the two buffered windows actually overlap (split evenly at the
/// overlap's midpoint); windows that don't reach each other -- e.g.
/// because the clock-in and clock-out buffers differ in size -- are left
/// untouched. An early clock-in is capped up to the
/// segment's start (no credit); a clock-out within the grace period of the
/// segment's end is capped down to it (no overtime); beyond the grace
/// period, overtime is the actual time past the segment's end. LateInDuration/
/// EarlyOutDuration/RemainDuration follow the same "minutes late plus minutes early"
/// model as SingleWindowShiftCalculationStrategy uses for Normal -- not the
/// "hours short of the day's total" model FlexibleShiftCalculationStrategy
/// uses for a Flexible day -- since AttendanceExcelExporter reuses that
/// exact Normal-shift formula for a SplitShift row (see HasScheduledWindow on
/// AttendanceSummary), and the literal values here have to agree with it.
/// This yields one AttendanceSummary *per segment*, not one for the whole
/// day -- see IShiftCalculationStrategy.
///
/// A manually-entered punch (AttendanceLogSource.Manual, see
/// ManualAttendanceLog) is treated as a fallback only: CalculateSegment
/// prefers a device punch within each of its two windows and only drops to a
/// manual one when a window has no device punch at all -- see PunchMatching.
/// </summary>
internal sealed class SplitShiftCalculationStrategy : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var employeeName = schedule.Employee?.DisplayName ?? "Unknown";
        var departmentName = schedule.Employee?.Department?.Name ?? "(Unassigned)";
        var employeeId = schedule.Employee?.Pin ?? schedule.EmployeeId;

        // A malformed SplitShift entry with no segments configured has no
        // window to compare punches against -- the loop below simply never
        // runs, so this yields no summary rows at all for the day (nothing
        // to compute), rather than guessing a window the way SingleWindow's
        // missing-TimeIn case reports Absent instead -- there's no single
        // "the day" summary shape for SplitShift to fall back to.
        var segments = schedule.FlexibleSegments.OrderBy(s => s.TimeIn).ToList();
        var results = new List<AttendanceSummary>(segments.Count);

        // Two independent +/-buffer windows per segment -- one around its own
        // start (for finding its clock-in), one around its own end (for
        // finding its clock-out) -- instead of one combined window spanning
        // the whole segment. See CalculateSegment for why that distinction
        // matters. Computed for every segment up front (unclamped) so the
        // neighbor-overlap pass below can compare each segment's real window
        // against its neighbor's real window, instead of guessing from the
        // raw, unbuffered segment boundaries.
        var segStarts = new DateTime[segments.Count];
        var segEnds = new DateTime[segments.Count];
        var inWindowStarts = new DateTime[segments.Count];
        var inWindowEnds = new DateTime[segments.Count];
        var outWindowStarts = new DateTime[segments.Count];
        var outWindowEnds = new DateTime[segments.Count];

        for (int i = 0; i < segments.Count; i++)
        {
            // A segment's own ClockInBufferHours/ClockOutBufferHours (see
            // FlexibleSegment) take priority over the policy-wide default --
            // null on the segment (the common case) falls back to it. This is
            // what lets one unusually tight or loose segment (e.g. a short
            // lunch-return window that shouldn't tolerate a +/-1h policy
            // default) be tuned without changing every other segment/day.
            double clockInBuffer = segments[i].ClockInBufferHours ?? policy.FlexibleSegmentClockInBuffer;
            double clockOutBuffer = segments[i].ClockOutBufferHours ?? policy.FlexibleSegmentClockOutBuffer;

            // A segment that crosses midnight (TimeOut <= TimeIn -- see
            // FlexibleSegment.CrossesMidnight) actually ends on the calendar day
            // after schedule.Date, e.g. a 22:00-06:00 night segment's real end is
            // tomorrow 06:00, not today 06:00 (which would be *before* its own
            // start). Same AddDays(1) adjustment SingleWindowShiftCalculationStrategy
            // already makes for an overnight Normal shift (see schedule.CrossesMidnight
            // there) -- this is the per-segment equivalent, since a SplitShift day can
            // have several segments and only some of them might wrap.
            segStarts[i] = schedule.Date.ToDateTime(segments[i].TimeIn);
            segEnds[i] = segments[i].CrossesMidnight
                ? schedule.Date.AddDays(1).ToDateTime(segments[i].TimeOut)
                : schedule.Date.ToDateTime(segments[i].TimeOut);
            inWindowStarts[i] = segStarts[i].AddHours(-clockInBuffer);
            inWindowEnds[i] = segStarts[i].AddHours(clockInBuffer);
            outWindowStarts[i] = segEnds[i].AddHours(-clockOutBuffer);
            outWindowEnds[i] = segEnds[i].AddHours(clockOutBuffer);
        }

        // Only clip a segment's out-window / the next segment's in-window when
        // they actually overlap -- i.e. this segment's out-buffer reaches past
        // where the next segment's in-buffer already starts -- and then split
        // only the overlapping region down the middle, so a punch sitting in
        // a genuine gap between two segments (e.g. a lunch break) can't be
        // claimed by both sides. Windows that don't reach each other -- e.g.
        // because the clock-in and clock-out buffers differ in size -- are
        // left untouched.
        for (int i = 0; i < segments.Count - 1; i++)
        {
            if (outWindowEnds[i] > inWindowStarts[i + 1])
            {
                DateTime midpoint = inWindowStarts[i + 1] +
                    TimeSpan.FromTicks((outWindowEnds[i] - inWindowStarts[i + 1]).Ticks / 2);
                outWindowEnds[i] = midpoint;
                inWindowStarts[i + 1] = midpoint;
            }
        }

        // Aggregated across every segment before computing the final unclaimed
        // set -- a punch that's an unclaimed candidate in one segment's window
        // (e.g. this segment's out-window) can still be the *actual* clock-in
        // a neighboring segment picked, since neighbor windows are clamped but
        // not guaranteed disjoint from every other segment's windows, only
        // their immediate neighbor's. Subtracting claimed only after seeing
        // every segment avoids reporting that punch as orphaned.
        var claimed = new List<AttendanceLog>();
        var considered = new List<AttendanceLog>();

        for (int i = 0; i < segments.Count; i++)
        {
            var (segmentSummary, clockInPunch, clockOutPunch, segmentConsidered) = CalculateSegment(
                schedule, segments[i], segStarts[i], segEnds[i],
                inWindowStarts[i], inWindowEnds[i], outWindowStarts[i], outWindowEnds[i],
                employeePunches, policy, employeeId, employeeName, departmentName);

            results.Add(segmentSummary);
            considered.AddRange(segmentConsidered);
            if (clockInPunch is not null) claimed.Add(clockInPunch);
            if (clockOutPunch is not null) claimed.Add(clockOutPunch);
        }

        var unclaimed = considered.Distinct().Except(claimed).ToList();

        return new ShiftCalculationResult
        {
            Summaries = results,
            ClaimedPunches = claimed,
            UnclaimedPunches = unclaimed,
        };
    }

    /// <summary>Evaluates one FlexibleSegment as its own scheduled window --
    /// the segment-scoped equivalent of SingleWindowShiftCalculationStrategy,
    /// except with two independent search windows instead of one combined one
    /// (see the class doc comment for why). inWindowStart/End and
    /// outWindowStart/End are the (neighbor-clamped) buffer ranges to search for
    /// this segment's clock-in and clock-out respectively; segStart/segEnd are
    /// the segment's own scheduled TimeIn/TimeOut (segEnd already rolled forward
    /// a day if the segment crosses midnight -- see Calculate), used as the
    /// cap/grace targets. candidatePunches is the employee's full punch list,
    /// not pre-filtered to schedule.Date -- inWindowStart/End and
    /// outWindowStart/End already carry the right calendar day(s) for this
    /// segment (including the day after schedule.Date for an overnight one), so
    /// PunchMatching's own DateTime-range filtering is what actually narrows it,
    /// the same way SingleWindowShiftCalculationStrategy relies on it for an
    /// overnight Normal shift. Returns the clockIn/clockOut punch references
    /// themselves (not just the TimeOnly values folded into Summary) plus every
    /// punch either window considered, so Calculate can aggregate
    /// claimed/considered across every segment before deciding what's actually
    /// unclaimed -- see Calculate's own doc comment for why that has to happen
    /// after seeing every segment, not per-segment here.</summary>
    private static (AttendanceSummary Summary, AttendanceLog? ClockInPunch, AttendanceLog? ClockOutPunch, List<AttendanceLog> Considered) CalculateSegment(
        ScheduleEntry schedule,
        FlexibleSegment segment,
        DateTime segStart,
        DateTime segEnd,
        DateTime inWindowStart,
        DateTime inWindowEnd,
        DateTime outWindowStart,
        DateTime outWindowEnd,
        List<AttendanceLog> candidatePunches,
        AttendancePolicy policy,
        int employeeId,
        string employeeName,
        string departmentName)
    {
        decimal segmentHours = (decimal)(segEnd - segStart).TotalHours;

        var summary = new AttendanceSummary
        {
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            Department = departmentName,
            ShiftDate = schedule.Date,
            ScheduleType = ScheduleType.SplitShift,
            HasScheduledWindow = true,
            CheckIn = segment.TimeIn,
            CheckOut = segment.TimeOut,
            Span = segmentHours,
        };

        // The earliest punch in the in-window is this segment's clock-in
        // candidate; the latest punch in the out-window (excluding whichever
        // punch was just claimed as the clock-in) is its clock-out candidate.
        // Searching independently like this -- rather than one combined window
        // with "first punch = in, last punch = out" -- means a single punch
        // can satisfy at most one of the two roles, never both. Device punches
        // are preferred in each window; a manual entry only fills in when its
        // window has no device punch at all -- see PunchMatching.
        var clockInPunch = PunchMatching.EarliestInWindow(candidatePunches, inWindowStart, inWindowEnd);
        var clockOutPunch = PunchMatching.LatestInWindow(candidatePunches, outWindowStart, outWindowEnd, exclude: clockInPunch);

        // Every punch either window actually looked at -- Calculate subtracts
        // out whatever ends up claimed (by this segment or a neighbor) once
        // every segment has run, so this is deliberately the full considered
        // set, not yet narrowed to just this segment's leftovers.
        var considered = PunchMatching.AllInWindow(candidatePunches, inWindowStart, inWindowEnd)
            .Concat(PunchMatching.AllInWindow(candidatePunches, outWindowStart, outWindowEnd))
            .Distinct()
            .ToList();

        if (clockInPunch is not null)
        {
            summary.ClockIn = TimeOnly.FromDateTime(clockInPunch.Timestamp);
            summary.ClockInIsManual = clockInPunch.Source == AttendanceLogSource.Manual;
        }

        if (clockOutPunch is not null)
        {
            summary.ClockOut = TimeOnly.FromDateTime(clockOutPunch.Timestamp);
            summary.ClockOutIsManual = clockOutPunch.Source == AttendanceLogSource.Manual;
        }

        if (clockInPunch is null && clockOutPunch is null)
        {
            summary.Status = PunchStatus.Absent;
            return (summary, clockInPunch, clockOutPunch, considered);
        }

        if (clockInPunch is null || clockOutPunch is null)
        {
            // Exactly one side found -- report it, leave the other blank rather
            // than duplicating it. This is the fix for the bug this replaced.
            summary.Status = PunchStatus.Partial;
            return (summary, clockInPunch, clockOutPunch, considered);
        }

        summary.Status = PunchStatus.Complete;

        DateTime effectiveTimeIn = clockInPunch.Timestamp;
        if (policy.CapEarlyClockIn && effectiveTimeIn < segStart)
            effectiveTimeIn = segStart;

        DateTime effectiveTimeOut = clockOutPunch.Timestamp;
        if (effectiveTimeOut >= segEnd && effectiveTimeOut < segEnd.AddHours(policy.ClockOutGracePeriod))
            effectiveTimeOut = segEnd;

        if (effectiveTimeOut <= effectiveTimeIn)
            return (summary, clockInPunch, clockOutPunch, considered); // degenerate ordering -- nothing further to compute

        double totalHours = (effectiveTimeOut - effectiveTimeIn).TotalHours;
        summary.WorkedHours = totalHours;
        summary.WorkedDuration = TimeSpan.FromHours(totalHours);

        // ScheduleEntry.NightDiffEligibleOverride / Employee.QualifiesForNightDiff --
        // see SingleWindowShiftCalculationStrategy for the PH Labor Code Art. 82
        // rationale and the per-day override this revision adds. WorkedDuration above
        // is unaffected; only this figure is suppressed to zero when not eligible.
        if (NightDifferentialCalculator.ResolveEligible(schedule))
        {
            summary.NightDiffHours = NightDifferentialCalculator.CalculateHours(
                effectiveTimeIn, effectiveTimeOut, policy.NightDiffStart, policy.NightDiffEnd);
            summary.NightDiffDuration = TimeSpan.FromHours(summary.NightDiffHours);
            summary.NightDiffRatePercentageOverride = schedule.NightDiffRatePercentageOverride;
        }

        // Late/early minutes against this segment's own boundaries -- the same
        // model SingleWindowShiftCalculationStrategy uses for Normal, not a
        // "hours short of the segment's span" figure. This has to match: with
        // Excel formula export on (the default), AttendanceExcelExporter reuses
        // that exact Normal-shift formula for a SplitShift row (see
        // HasScheduledWindow), so the literal values computed here and the
        // live formula in the workbook need to agree, or the row would show a
        // different Late/Early/Remain depending on policy.UseExcelFormula.
        // Same grace-period carve-out as SingleWindowShiftCalculationStrategy -- a
        // punch within policy.LateInEarlyOutGraceMinutes of this segment's own
        // start/end is exactly on time (LateInDuration/EarlyOutDuration stay zero); past that,
        // the entire difference counts. See AttendancePolicy.LateInEarlyOutGraceMinutes.
        if (effectiveTimeIn > segStart &&
            (effectiveTimeIn - segStart).TotalMinutes > policy.LateInEarlyOutGraceMinutes)
            summary.LateInDuration = effectiveTimeIn - segStart;

        if (effectiveTimeOut < segEnd &&
            (segEnd - effectiveTimeOut).TotalMinutes > policy.LateInEarlyOutGraceMinutes)
            summary.EarlyOutDuration = segEnd - effectiveTimeOut;

        summary.RemainDuration = summary.LateInDuration + summary.EarlyOutDuration;
        summary.RemainHours = summary.RemainDuration.TotalHours;

        // ScheduleEntry.OvertimeEligibleOverride / Employee.QualifiesForOvertime --
        // same PH Labor Code Art. 82 exemption as NightDiff above, applied
        // independently, plus this revision's per-day exception on top.
        if (ResolveOvertimeEligible(schedule) &&
            effectiveTimeOut >= segEnd.AddHours(policy.ClockOutGracePeriod))
        {
            double segmentHoursDouble = (double)segmentHours;
            double overtimeHours = policy.StrictOvertimeFromShiftEnd
                ? (effectiveTimeOut - segEnd).TotalHours
                : Math.Max(totalHours - segmentHoursDouble, 0);

            if (overtimeHours > 0)
            {
                summary.OvertimeHours = overtimeHours;
                summary.OvertimeDuration = TimeSpan.FromHours(overtimeHours);
                summary.OvertimeRatePercentageOverride = schedule.OvertimeRatePercentageOverride;
                summary.ApplyOvertimeRatePercentage = ResolveApplyOvertimeRatePercentage(schedule);
            }
        }

        return (summary, clockInPunch, clockOutPunch, considered);
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
}
