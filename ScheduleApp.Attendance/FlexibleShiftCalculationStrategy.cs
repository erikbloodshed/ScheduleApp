using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Used for every Flexible ScheduleEntry: an employee can clock in and out any
/// number of times during the day, and WorkTimeHours means the day's required
/// total, not a shift length to measure lateness against. An odd number of
/// punches is normalized first -- the smallest adjacent gap in the day, if
/// under AttendancePolicy.FlexibleMinimumBreakGap, has its noisier side
/// dropped (see CalculateUnrestrictedDay's odd-count normalization) -- then
/// every remaining punch within the day's search boundary counts, sorted and
/// paired off sequentially (1st=in, 2nd=out, 3rd=in, ...) -- see
/// CalculateUnrestrictedDay. Two adjacent pairs only count as separate work
/// intervals if the gap between
/// the first pair's clock-out and the second pair's clock-in is at least
/// AttendancePolicy.FlexibleMinimumBreakGap -- e.g. paired_punch(x,y),
/// gap(&gt;=1h), paired_punch(a,b). A shorter gap is treated as noise (a
/// duplicate device tap, or too brief to be a real break) and the two pairs
/// are merged into one continuous interval instead of being reported as two.
/// Worked hours is the sum of each resulting (possibly merged) interval's
/// duration, compared against the day's single WorkTimeHours target. Always
/// yields exactly one AttendanceSummary for the day -- unlike
/// SplitShiftCalculationStrategy, which yields one per segment.
///
/// ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut optionally bound *when*
/// punches are allowed, as a single (TimeIn, TimeOut) window for the day (see
/// ScheduleEntry's own doc comment). Both null (the common case) means no
/// restriction at all -- every punch that calendar day counts, same as
/// before RestrictedTimeIn/RestrictedTimeOut existed. The two sides behave
/// differently, deliberately:
///
/// - RestrictedTimeIn never removes a punch from the search or the pairing --
///   an early punch still pairs normally and still shows up in the raw
///   ClockIn -- it only clamps the *effective* start time used for the
///   Worked_H/NightDiff_H math (applied per resulting pair, after the merge
///   pass below), the same "no credit before the window opens" idea
///   SingleWindowShiftCalculationStrategy/SplitShiftCalculationStrategy apply
///   to their own scheduled starts, gated the same way behind
///   policy.CapEarlyClockIn (no new policy knob).
/// - RestrictedTimeOut is a hard search-boundary filter instead: any punch
///   timestamped after it is excluded from the day's search entirely, so it
///   can never appear as ClockIn/ClockOut, get paired, or contribute to a
///   merged interval. No capping on this side -- a punch either falls inside
///   the window or it doesn't.
///
/// Same crosstime convention as FlexibleSegment.CrossesMidnight: a
/// RestrictedTimeOut at or before RestrictedTimeIn means the window actually
/// ends on the calendar day after schedule.Date, so the search-boundary
/// filter below has to reach into the next calendar day rather than stopping
/// at schedule.Date's midnight the way the fully-unrestricted case does.
///
/// A manually-entered punch (AttendanceLogSource.Manual, see
/// ManualAttendanceLog) is treated as a fallback only, but that preference
/// is decided per-punch, not for the whole day: a manual punch is dropped
/// only when a device punch lands within
/// AttendancePolicy.FlexibleMinimumBreakGap of it -- close enough to be the
/// same physical clock event recorded twice -- and otherwise stays in the
/// pool even if the day has other, unrelated device punches. See
/// CalculateUnrestrictedDay's own note for why (a manual entry genuinely
/// filling a gap the device side never covered that day shouldn't be wiped
/// out just because some other device punch exists hours away). Unlike
/// CalculateSegment/SingleWindowShiftCalculationStrategy (see PunchMatching),
/// there's no fixed clock-in/clock-out slot here to match a punch against --
/// duplicate detection is purely "how close in time," the same idea the
/// merge pass below applies between pairs.
/// </summary>
internal sealed class FlexibleShiftCalculationStrategy : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var employeeName = schedule.Employee?.DisplayName ?? "Unknown";
        var departmentName = schedule.Employee?.Department?.Name ?? "(Unassigned)";
        var employeeId = schedule.Employee?.Pin ?? schedule.EmployeeId;

        var (summary, claimed, unclaimed) =
            CalculateUnrestrictedDay(schedule, employeePunches, policy, employeeId, employeeName, departmentName);

        return new ShiftCalculationResult
        {
            Summaries = [summary],
            ClaimedPunches = claimed,
            UnclaimedPunches = unclaimed,
        };
    }

    /// <summary>Every punch within the day's search boundary counts, sorted
    /// and paired off sequentially (1st=in, 2nd=out, 3rd=in, ...), worked
    /// hours compared against the day's single WorkTimeHours target.
    /// LateIn_T/EarlyOut_T stay zero -- there's no fixed target time to be
    /// late or early against when nothing bounds *when* punches are allowed.
    ///
    /// Two adjacent pairs only count as separate work intervals if the gap
    /// between them is at least AttendancePolicy.FlexibleMinimumBreakGap --
    /// see the class doc comment. A gap shorter than that is treated as noise
    /// (e.g. a duplicate device tap, or too brief to be a real break) and the
    /// two pairs are merged into one continuous interval instead of being
    /// reported as two -- without this, e.g. a stray near-duplicate tap a
    /// minute after the real clock-out would silently start a second "pair"
    /// and inflate Worked_H, or (worse) leave a dangling unpaired punch that
    /// turns an otherwise-Complete day into Partial. This merge pass runs on
    /// the raw punch timestamps -- it's unaffected by RestrictedTimeIn's
    /// capping below, which only ever touches the totalHours/night-diff math
    /// afterward, never which punches count as a pair or how pairs merge.
    ///
    /// Night diff has no single "effective" capped/graced interval to work
    /// from here (there's no one scheduled window to cap against) -- it's
    /// summed per resulting pair instead, each pair independently capped the
    /// same way Worked_H is below -- policy is only needed for its
    /// NightDiffStart/NightDiffEnd.</summary>
    private static (AttendanceSummary Summary, List<AttendanceLog> Claimed, List<AttendanceLog> Unclaimed) CalculateUnrestrictedDay(
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
            ScheduleType = ScheduleType.Flexible,
            Span = schedule.WorkTimeHours,
        };

        // HasScheduledWindow stays false below -- this is still a
        // merge-of-arbitrary-pairs shape, not a single clean window, so
        // there's no scheduled CheckOut to show alongside it. Exposing
        // RestrictedTimeIn through CheckIn regardless gives
        // AttendanceExcelExporter something to display next to ClockIn for
        // export visibility -- see the ScheduleTimeRefactor Phase 5 notes.
        if (schedule.RestrictedTimeIn is { } restrictedTimeInForDisplay)
        {
            summary.CheckIn = restrictedTimeInForDisplay;
        }

        // RestrictedTimeIn never narrows this search -- an early punch still
        // needs to be seen and paired, just not credited (see
        // FlexibleWorkedHours.Populate's capping). RestrictedTimeOut does narrow
        // it: it's a hard boundary, so a punch after it is excluded here rather
        // than merely left uncredited. Same crosstime convention as
        // FlexibleSegment.CrossesMidnight -- see the class doc comment -- so
        // the boundary can reach into the day after schedule.Date when
        // RestrictedTimeOut is at or before RestrictedTimeIn. Shared with
        // OverriddenFlexibleShiftCalculationStrategy so both search identically.
        var (searchStart, searchEnd) = FlexiblePairingBuilder.SearchWindow(schedule);

        var dayPunches = employeePunches
            .Where(p => p.Timestamp >= searchStart && p.Timestamp <= searchEnd)
            .OrderBy(p => p.Timestamp)
            .ToList();

        // Device punches are preferred over a manual one representing the
        // *same* physical clock event -- but "device-first" is decided
        // per-punch, not for the whole day. A manual entry within
        // policy.FlexibleMinimumBreakGap of some device punch is treated as
        // that same event recorded twice (e.g. the device eventually synced
        // a punch someone had already hand-typed in the meantime) and is
        // dropped in favor of the device one -- the same "too close to be a
        // separate real thing" idea the merge pass below applies to pairs,
        // just applied here to a single punch against its nearest device
        // neighbor. A manual punch that ISN'T close to any device punch is a
        // genuine gap-filler (e.g. the employee forgot to badge in that
        // morning, but the device did catch their afternoon punch) and stays
        // in the pool regardless of what else got recorded that day. This
        // matches the class's own "every punch within the day's search
        // boundary counts, sorted and paired off sequentially" rule --
        // previously ANY device punch that day, however far from a given
        // manual entry, discarded every manual punch outright, which could
        // zero out a legitimate correction rather than just deduplicating a
        // genuine double-record.
        var devicePunches = dayPunches.Where(p => p.Source != AttendanceLogSource.Manual).ToList();
        var manualPunches = dayPunches.Where(p => p.Source == AttendanceLogSource.Manual).ToList();
        var duplicateManualPunches = manualPunches
            .Where(m => devicePunches.Any(d =>
                Math.Abs((d.Timestamp - m.Timestamp).TotalHours) < policy.FlexibleMinimumBreakGap))
            .ToList();
        var effectivePunches = devicePunches
            .Concat(manualPunches.Except(duplicateManualPunches))
            .OrderBy(p => p.Timestamp)
            .ToList();

        // Odd-count normalization: an odd count means one extra tap snuck in
        // somewhere in the day -- almost always a double-tap at the device,
        // not a genuine extra interval. Find the single smallest gap between
        // any two ADJACENT punches anywhere in the day (not just at the
        // edges); if it's under policy.FlexibleMinimumBreakGap, one of that
        // pair is the duplicate:
        //   (8:30, 8:38, 17:43)                -> drop 8:38  -> (8:30, 17:43)
        //   (8:30, 17:33, 17:45)               -> drop 17:33 -> (8:30, 17:45)
        //   (8:00, 12:00, 12:05, 13:00, 17:00) -> drop 12:05 -> (8:00, 12:00, 13:00, 17:00)
        // Which one gets dropped: whichever of the pair has the SMALLER gap
        // to its OTHER neighbor is the one sitting in a pocket of noise, so
        // it goes -- 12:00 is 4h from 8:00, 12:05 is only 55m from 13:00, so
        // 12:05 is the duplicate. The day's very first/last punch has no
        // "other neighbor" on that side, which is equivalent to an infinite
        // other-gap -- so an edge punch is never dropped by this comparison,
        // no separate edge check needed. Checked once: dropping a single
        // punch flips odd to even, so there's nothing left to normalize. If
        // the smallest gap isn't under the threshold, the count stays odd
        // and falls through to the dangling-last-punch handling in Pass 1
        // below (Partial status) -- this only resolves a boundary/adjacent
        // duplicate tap, not some other reason for an odd count.
        if (effectivePunches.Count % 2 != 0 && effectivePunches.Count >= 3)
        {
            int minGapIndex = 0;
            double minGapHours = double.MaxValue;
            for (int i = 0; i + 1 < effectivePunches.Count; i++)
            {
                double gapHours = (effectivePunches[i + 1].Timestamp - effectivePunches[i].Timestamp).TotalHours;
                if (gapHours < minGapHours)
                {
                    minGapHours = gapHours;
                    minGapIndex = i;
                }
            }

            if (minGapHours < policy.FlexibleMinimumBreakGap)
            {
                int left = minGapIndex;
                int right = minGapIndex + 1;

                double leftOtherGap = left > 0
                    ? (effectivePunches[left].Timestamp - effectivePunches[left - 1].Timestamp).TotalHours
                    : double.MaxValue; // left is the day's first punch -- never drop it
                double rightOtherGap = right < effectivePunches.Count - 1
                    ? (effectivePunches[right + 1].Timestamp - effectivePunches[right].Timestamp).TotalHours
                    : double.MaxValue; // right is the day's last punch -- never drop it

                effectivePunches.RemoveAt(leftOtherGap >= rightOtherGap ? right : left);
            }
        }

        // Every punch actually used (paired off, merged away as noise, the
        // odd trailing one, or normalized away above as an adjacent
        // duplicate) is claimed; the only ways a punch inside the search
        // boundary can be left out here are a manual entry dropped as a
        // duplicate of a nearby device punch (see duplicateManualPunches
        // above) or a punch dropped by the odd-count normalization above --
        // both are reported back as Unclaimed rather than silently
        // discarded. A punch excluded by the RestrictedTimeOut boundary
        // above never enters dayPunches in the first place, so it isn't part
        // of either list for this schedule entry at all (it can still
        // surface as Orphaned/Unscheduled via a different schedule entry, or
        // as Unscheduled if nothing claims/considers it).
        var unclaimed = dayPunches.Except(effectivePunches).ToList();

        if (effectivePunches.Count == 0)
        {
            summary.Status = PunchStatus.Absent;
            return (summary, [], unclaimed);
        }

        // Sequential pairing (1st=in, 2nd=out, ...) followed by the merge pass
        // for adjacent pairs closer together than policy.FlexibleMinimumBreakGap
        // -- see the class doc comment. Both live in FlexiblePairingBuilder now,
        // shared with the hand-edited path
        // (OverriddenFlexibleShiftCalculationStrategy) and with the Desktop Day
        // Punch Pairing editor's live preview, so the rule has one definition.
        // An odd count leaves the last punch unpaired, which is what makes the
        // day Partial -- by this point that only happens when the odd-count
        // normalization above didn't find a qualifying adjacent duplicate to
        // remove.
        var pairing = FlexiblePairingBuilder.BuildDefault(effectivePunches, policy.FlexibleMinimumBreakGap);

        FlexibleWorkedHours.Populate(summary, pairing, schedule, policy, effectivePunches);

        return (summary, effectivePunches, unclaimed);
    }
}
