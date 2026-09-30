using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>One closed [Start, End] span of clock time a punch can fall in to
/// count for a schedule entry. Both ends are inclusive, matching the
/// <c>&gt;= windowStart &amp;&amp; &lt;= windowEnd</c> predicates
/// <see cref="PunchMatching"/> has always used.</summary>
public readonly record struct PunchWindow(DateTime Start, DateTime End)
{
    public bool Contains(DateTime timestamp) => timestamp >= Start && timestamp <= End;
}

/// <summary>
/// The single definition of "which punches this schedule entry could possibly
/// match" -- the buffered clock-in/clock-out windows for the types that match
/// against a scheduled window, the restricted search boundary for Flexible, and
/// nothing at all for the types that never look at punches.
///
/// Every strategy already computed these windows for itself; they're pulled out
/// here because the Desktop Day Punch Pairing editor needs the same answer for a
/// different purpose -- greying out a punch the calculation will never consider --
/// and a second, lookalike copy of this arithmetic living in the view model would
/// be free to drift from the strategies' own. So the strategies now call these
/// helpers rather than the other way round: what the editor greys out is, by
/// construction, exactly what the calculation ignores.
///
/// Per ScheduleType (see <see cref="For"/>):
/// - Normal, and the windowed sub-case of RestDay: two windows -- one around the
///   scheduled time in, one around the scheduled time out -- each sized by the
///   three-tier entry/employee/policy buffer cascade (see
///   <see cref="NormalBufferResolver"/>). An entry missing TimeIn/WorkTimeHours
///   (including RestDay's ordinary unscheduled mode) has no window at all.
/// - SplitShift: two windows per segment, symmetric around that segment's own
///   start and end, with the neighbour-overlap clamp applied -- see
///   <see cref="ForSegments"/>.
/// - Flexible: one window, its restricted search boundary (see
///   <see cref="FlexiblePairingBuilder.SearchWindow"/>).
/// - Leave, OfficialBusiness: none. Neither ever looks at a punch (see
///   LeaveShiftCalculationStrategy/OfficialBusinessShiftCalculationStrategy,
///   whose ClaimedPunches/UnclaimedPunches always come back empty), so every
///   punch on such a day is outside "what counts" by definition.
/// </summary>
public static class PunchCandidateWindows
{
    /// <summary>Every window <paramref name="schedule"/> would search for a punch,
    /// in no particular order and not necessarily disjoint. Empty when this entry
    /// never looks at punches at all -- which is a real answer ("nothing here
    /// counts"), not a failure.</summary>
    public static IReadOnlyList<PunchWindow> For(ScheduleEntry schedule, AttendancePolicy policy)
    {
        switch (schedule.ScheduleType)
        {
            case ScheduleType.Leave:
            case ScheduleType.OfficialBusiness:
                return [];

            case ScheduleType.Flexible:
            {
                var (start, end) = FlexiblePairingBuilder.SearchWindow(schedule);
                return [new PunchWindow(start, end)];
            }

            case ScheduleType.SplitShift:
            {
                var segments = ForSegments(schedule, policy);
                var windows = new List<PunchWindow>(segments.Count * 2);
                foreach (var segment in segments)
                {
                    windows.Add(segment.In);
                    windows.Add(segment.Out);
                }
                return windows;
            }

            // Normal, RestDay, and -- deliberately -- any ScheduleType this build
            // doesn't know, mirroring AttendanceCalculator's own fallback to the
            // single-window strategy for an unrecognized type.
            default:
                return SingleWindow(schedule, policy) is { } single
                    ? [single.In, single.Out]
                    : [];
        }
    }

    /// <summary>True when <paramref name="punch"/> falls inside any of
    /// <paramref name="windows"/>. Always false for an empty window list, which is
    /// what makes every punch on a Leave/OfficialBusiness/unscheduled-RestDay day
    /// read as "not considered."</summary>
    public static bool IsCandidate(AttendanceLog punch, IReadOnlyList<PunchWindow> windows)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Contains(punch.Timestamp))
                return true;
        }

        return false;
    }

    /// <summary>The smallest span covering every window, or null when there are
    /// none. What a caller fetching punches to feed these windows should query
    /// over -- notably wider than the calendar day for an overnight shift, whose
    /// clock-out window sits on the following morning.</summary>
    public static PunchWindow? Bounds(IReadOnlyList<PunchWindow> windows)
    {
        if (windows.Count == 0)
            return null;

        var start = windows[0].Start;
        var end = windows[0].End;
        for (var i = 1; i < windows.Count; i++)
        {
            if (windows[i].Start < start) start = windows[i].Start;
            if (windows[i].End > end) end = windows[i].End;
        }

        return new PunchWindow(start, end);
    }

    /// <summary>The scheduled window and the two buffered search windows for an
    /// entry matched against a single TimeIn/WorkTimeHours pair -- Normal, and
    /// RestDay's windowed sub-case, which build these identically. Null when the
    /// entry has no such pair to derive them from: a malformed Normal row, or
    /// RestDay's ordinary "no schedule at all" mode, neither of which has a window
    /// to compare punches against.</summary>
    internal static SingleWindowBounds? SingleWindow(ScheduleEntry schedule, AttendancePolicy policy)
    {
        if (schedule.TimeIn is not { } scheduledTimeIn || schedule.WorkTimeHours is null)
            return null;

        var scheduledTimeOut = schedule.TimeOut!.Value; // derived from TimeIn + WorkTimeHours, so non-null here

        DateTime targetTimeInStart = schedule.Date.ToDateTime(scheduledTimeIn);
        DateTime targetTimeOutStart = schedule.CrossesMidnight
            ? schedule.Date.AddDays(1).ToDateTime(scheduledTimeOut)
            : schedule.Date.ToDateTime(scheduledTimeOut);

        var (clockInBufferBefore, clockInBufferAfter, clockOutBufferBefore, clockOutBufferAfter) =
            NormalBufferResolver.Resolve(schedule, policy);

        return new SingleWindowBounds(
            targetTimeInStart,
            targetTimeOutStart,
            new PunchWindow(
                targetTimeInStart.AddHours(-clockInBufferBefore),
                targetTimeInStart.AddHours(clockInBufferAfter)),
            new PunchWindow(
                targetTimeOutStart.AddHours(-clockOutBufferBefore),
                targetTimeOutStart.AddHours(clockOutBufferAfter)));
    }

    /// <summary>
    /// Per-segment windows for a SplitShift day, segments ordered by TimeIn, with
    /// the neighbour-overlap clamp already applied: where one segment's out-window
    /// reaches past where the next segment's in-window starts, the overlap is split
    /// down the middle so a punch in a genuine gap between segments can't be
    /// claimed by both sides. Empty for a SplitShift entry with no segments
    /// configured, which has nothing to match against at all.
    /// </summary>
    internal static IReadOnlyList<SegmentWindowBounds> ForSegments(ScheduleEntry schedule, AttendancePolicy policy)
    {
        var segments = schedule.FlexibleSegments.OrderBy(s => s.TimeIn).ToList();
        if (segments.Count == 0)
            return [];

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
            // start). Same AddDays(1) adjustment SingleWindow above already makes
            // for an overnight Normal shift -- this is the per-segment equivalent,
            // since a SplitShift day can have several segments and only some of
            // them might wrap.
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
        // only the overlapping region down the middle. Windows that don't reach
        // each other -- e.g. because the clock-in and clock-out buffers differ in
        // size -- are left untouched.
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

        var bounds = new SegmentWindowBounds[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            bounds[i] = new SegmentWindowBounds(
                segments[i],
                segStarts[i],
                segEnds[i],
                new PunchWindow(inWindowStarts[i], inWindowEnds[i]),
                new PunchWindow(outWindowStarts[i], outWindowEnds[i]));
        }

        return bounds;
    }

    /// <summary>The scheduled start/end and the two buffered search windows of a
    /// single-window entry -- see <see cref="SingleWindow"/>. The targets are
    /// carried alongside the windows because the strategies need them for
    /// lateness/undertime as well as for matching.</summary>
    internal readonly record struct SingleWindowBounds(
        DateTime TargetTimeIn, DateTime TargetTimeOut, PunchWindow In, PunchWindow Out);

    /// <summary>One SplitShift segment's real start/end (midnight-crossing already
    /// resolved) and its two clamped search windows -- see
    /// <see cref="ForSegments"/>.</summary>
    internal readonly record struct SegmentWindowBounds(
        FlexibleSegment Segment, DateTime SegmentStart, DateTime SegmentEnd, PunchWindow In, PunchWindow Out);
}
