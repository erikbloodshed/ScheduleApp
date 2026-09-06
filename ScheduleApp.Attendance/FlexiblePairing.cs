using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>Identifies one punch across the two unrelated id spaces -- a device
/// <see cref="AttendanceLog.Id"/> (<see cref="IsManual"/> false) or a
/// <see cref="ManualAttendanceLog.Id"/> surfaced through
/// <see cref="ManualAttendanceLog.ToAttendanceLog"/> (<see cref="IsManual"/>
/// true). Matches <see cref="DayPunchPairingSlot.PunchId"/> /
/// <see cref="DayPunchPairingSlot.IsManualPunch"/>.</summary>
public readonly record struct PunchKey(int PunchId, bool IsManual)
{
    public static PunchKey Of(AttendanceLog punch) =>
        new(punch.Id, punch.Source == AttendanceLogSource.Manual);
}

/// <summary>One paired work interval of a Flexible day: an in punch, an out
/// punch, and the 0-based segment (editor grid row) it belongs to.</summary>
public sealed record FlexiblePair(AttendanceLog In, AttendanceLog Out, int SegmentIndex);

/// <summary>Where a single punch sits before in-&gt;out matching happens -- which
/// segment (grid row) and which side of it. See
/// <see cref="FlexiblePairingBuilder.PlaceFromOverride"/>.</summary>
public sealed record PunchPlacement(AttendanceLog Punch, int SegmentIndex, PairingRole Role);

/// <summary>The result of pairing a Flexible day's punches -- the whole (in, out)
/// intervals plus any punch left without a partner (an odd tap, an out with no
/// in). <see cref="HasUnpairedPunch"/> is what turns an otherwise-Complete day
/// Partial.</summary>
public sealed record FlexiblePairing(
    IReadOnlyList<FlexiblePair> Pairs,
    IReadOnlyList<AttendanceLog> Unpaired)
{
    public bool HasUnpairedPunch => Unpaired.Count > 0;

    public static readonly FlexiblePairing Empty = new([], []);
}

/// <summary>
/// The single definition of "which Flexible-day punch goes with which." Shared by
/// <c>FlexibleShiftCalculationStrategy</c> (default, time-order pairing),
/// <c>OverriddenFlexibleShiftCalculationStrategy</c> (a saved
/// <see cref="DayPunchPairing"/>), and the Desktop Day Punch Pairing editor's
/// live preview, so none of them re-derive the rule slightly differently.
/// </summary>
public static class FlexiblePairingBuilder
{
    /// <summary>
    /// Default pairing: pair the day's punches off sequentially by count
    /// (1st = in, 2nd = out, 3rd = in, ...), then merge any two adjacent pairs
    /// whose out-&gt;in gap is under <paramref name="minimumBreakGapHours"/> into
    /// one continuous interval (a shorter gap is noise -- a duplicate tap or too
    /// brief to be a real break). An odd count leaves the last punch unpaired.
    /// This is exactly the original two-pass logic from
    /// FlexibleShiftCalculationStrategy, moved here verbatim.
    /// <paramref name="orderedPunches"/> must already be sorted by timestamp and
    /// deduped/normalized by the caller.
    /// </summary>
    public static FlexiblePairing BuildDefault(
        IReadOnlyList<AttendanceLog> orderedPunches, double minimumBreakGapHours)
    {
        var rawPairs = new List<(AttendanceLog In, AttendanceLog Out)>();
        for (int i = 0; i + 1 < orderedPunches.Count; i += 2)
            rawPairs.Add((orderedPunches[i], orderedPunches[i + 1]));

        var unpaired = orderedPunches.Count % 2 != 0
            ? new List<AttendanceLog> { orderedPunches[^1] }
            : new List<AttendanceLog>();

        var merged = new List<(AttendanceLog In, AttendanceLog Out)>();
        foreach (var pair in rawPairs)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                double gapHours = (pair.In.Timestamp - prev.Out.Timestamp).TotalHours;
                if (gapHours < minimumBreakGapHours)
                {
                    merged[^1] = (prev.In, pair.Out); // extend, don't start a new interval
                    continue;
                }
            }
            merged.Add(pair);
        }

        var pairs = new List<FlexiblePair>(merged.Count);
        for (int i = 0; i < merged.Count; i++)
            pairs.Add(new FlexiblePair(merged[i].In, merged[i].Out, i));

        return new FlexiblePairing(pairs, unpaired);
    }

    /// <summary>
    /// Pairing from a hand-edited <see cref="DayPunchPairing"/>: every punch named
    /// by <paramref name="overrideMap"/> takes its saved (segment, role); a punch
    /// the map doesn't name -- typically a device punch imported after the pairing
    /// was saved -- is folded in by timestamp with a positional default (role =
    /// opposite of the previous punch's role, starting with In; segment = the
    /// previous punch's segment). Then each segment's punches are paired in/out in
    /// time order. This is what makes "auto-append by time" fall out for free, and
    /// what can flip an overridden day back to Partial as a signal to re-open the
    /// editor.
    /// <paramref name="orderedPunches"/> must already be sorted by timestamp.
    /// </summary>
    public static FlexiblePairing BuildFromOverride(
        IReadOnlyList<AttendanceLog> orderedPunches,
        IReadOnlyDictionary<PunchKey, (int Segment, PairingRole Role)> overrideMap)
    {
        var placed = PlaceFromOverride(orderedPunches, overrideMap);

        var pairs = new List<FlexiblePair>();
        var unpaired = new List<AttendanceLog>();

        foreach (var segment in placed.GroupBy(x => x.SegmentIndex).OrderBy(g => g.Key))
        {
            AttendanceLog? pendingIn = null;
            foreach (var (punch, _, role) in segment.OrderBy(x => x.Punch.Timestamp))
            {
                if (role == PairingRole.In)
                {
                    if (pendingIn is not null)
                        unpaired.Add(pendingIn); // two ins in a row -- the earlier one is orphaned
                    pendingIn = punch;
                }
                else if (pendingIn is not null)
                {
                    pairs.Add(new FlexiblePair(pendingIn, punch, pairs.Count));
                    pendingIn = null;
                }
                else
                {
                    unpaired.Add(punch); // out with no in
                }
            }

            if (pendingIn is not null)
                unpaired.Add(pendingIn); // trailing in with no out
        }

        return new FlexiblePairing(pairs, unpaired);
    }

    /// <summary>
    /// The first half of <see cref="BuildFromOverride"/> on its own: which segment
    /// and role each punch ends up with, before any in-&gt;out matching. Exposed
    /// separately so the Desktop Day Punch Pairing editor can seed its grid rows
    /// from a saved pairing -- it needs each punch's cell, which the paired-up
    /// <see cref="FlexiblePairing"/> no longer distinguishes for an unpaired
    /// punch.
    ///
    /// A punch named by <paramref name="overrideMap"/> takes its saved values and
    /// becomes the running position; one that isn't named -- a punch imported
    /// after the pairing was saved -- takes the opposite role from the punch
    /// before it (In for the very first) in that same punch's segment. That is
    /// the whole of the "auto-append by time" behaviour.
    /// </summary>
    public static IReadOnlyList<PunchPlacement> PlaceFromOverride(
        IReadOnlyList<AttendanceLog> orderedPunches,
        IReadOnlyDictionary<PunchKey, (int Segment, PairingRole Role)> overrideMap)
    {
        var placed = new List<PunchPlacement>(orderedPunches.Count);
        int runningSegment = 0;
        PairingRole? previousRole = null;

        foreach (var punch in orderedPunches)
        {
            int segment;
            PairingRole role;
            if (overrideMap.TryGetValue(PunchKey.Of(punch), out var slot))
            {
                segment = slot.Segment;
                role = slot.Role;
            }
            else
            {
                segment = previousRole is null ? 0 : runningSegment;
                role = previousRole == PairingRole.In ? PairingRole.Out : PairingRole.In;
            }

            placed.Add(new PunchPlacement(punch, segment, role));
            runningSegment = segment;
            previousRole = role;
        }

        return placed;
    }

    /// <summary>The [start, end] the day's punches are searched over -- midnight
    /// to midnight, unless <see cref="ScheduleEntry.RestrictedTimeOut"/> narrows
    /// the end (reaching into the next calendar day when it's at or before
    /// <see cref="ScheduleEntry.RestrictedTimeIn"/>, the crosses-midnight
    /// convention). Extracted from FlexibleShiftCalculationStrategy so the
    /// overridden path uses the identical boundary.</summary>
    public static (DateTime Start, DateTime End) SearchWindow(ScheduleEntry schedule)
    {
        DateTime start = schedule.Date.ToDateTime(TimeOnly.MinValue);
        DateTime end = schedule.RestrictedTimeOut is { } restrictedTimeOut
            ? (schedule.RestrictedTimeIn is { } inBoundForCrossing && restrictedTimeOut <= inBoundForCrossing
                ? schedule.Date.AddDays(1).ToDateTime(restrictedTimeOut)
                : schedule.Date.ToDateTime(restrictedTimeOut))
            : schedule.Date.ToDateTime(TimeOnly.MaxValue);
        return (start, end);
    }
}
