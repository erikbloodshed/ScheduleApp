using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// The "already-paired work intervals -&gt; AttendanceSummary" half of a Flexible
/// day's calculation, extracted out of FlexibleShiftCalculationStrategy so the
/// default path, the hand-edited path
/// (OverriddenFlexibleShiftCalculationStrategy), and the Desktop Day Punch
/// Pairing editor's live Worked/Remainder/status preview all read from one
/// definition rather than three drifting copies. The pairing rule itself lives in
/// <see cref="FlexiblePairingBuilder"/>.
/// </summary>
public static class FlexibleWorkedHours
{
    /// <summary>
    /// Fills <paramref name="summary"/>'s Clock/Worked/NightDiff/Status/Remain/
    /// Overtime fields from <paramref name="pairing"/>.
    ///
    /// Each pair is capped independently against
    /// <see cref="ScheduleEntry.RestrictedTimeIn"/> (gated on
    /// <see cref="AttendancePolicy.CapEarlyClockIn"/>, the same "no credit before
    /// the window opens" idea the windowed strategies apply) -- an unrestricted
    /// Flexible day can have several independent intervals and only the ones
    /// actually starting early need capping. RestrictedTimeOut never caps here; it
    /// only filters the search (see
    /// <see cref="FlexiblePairingBuilder.SearchWindow"/>). Night diff is summed
    /// per capped pair for the same reason -- there's no single scheduled window
    /// to measure against. LateIn_T/EarlyOut_T stay zero: a Flexible day has no
    /// fixed target time to be late or early against.
    ///
    /// <paramref name="orderedPunches"/> is the day's punch list in timestamp
    /// order, used only for the raw ClockIn/ClockOut endpoints, and must be
    /// non-empty -- callers handle the no-punch (Absent) case themselves, since
    /// "no punches at all" isn't a pairing outcome.
    /// </summary>
    public static void Populate(
        AttendanceSummary summary,
        FlexiblePairing pairing,
        ScheduleEntry schedule,
        AttendancePolicy policy,
        IReadOnlyList<AttendanceLog> orderedPunches)
    {
        summary.ClockIn = TimeOnly.FromDateTime(orderedPunches[0].Timestamp);
        summary.ClockInIsManual = orderedPunches[0].Source == AttendanceLogSource.Manual;
        summary.ClockOut = TimeOnly.FromDateTime(orderedPunches[^1].Timestamp);
        summary.ClockOutIsManual = orderedPunches[^1].Source == AttendanceLogSource.Manual;

        // ScheduleEntry.NightDiffEligibleOverride / Employee.QualifiesForNightDiff --
        // see SingleWindowShiftCalculationStrategy for the PH Labor Code Art. 82
        // rationale. Worked hours are unaffected; only the night-diff sum is
        // skipped when not eligible.
        bool qualifiesForNightDiff = NightDifferentialCalculator.ResolveEligible(schedule);

        DateTime? restrictedInBoundary = schedule.RestrictedTimeIn is { } tIn
            ? schedule.Date.ToDateTime(tIn)
            : null;

        double totalHours = 0;
        double nightDiffHours = 0;
        foreach (var pair in pairing.Pairs)
        {
            DateTime effectiveIn = pair.In.Timestamp;
            if (restrictedInBoundary is { } rin && policy.CapEarlyClockIn && effectiveIn < rin)
                effectiveIn = rin;

            DateTime effectiveOut = pair.Out.Timestamp;

            // A pair that fell entirely before the restricted window opened caps
            // down to zero-or-negative duration -- no credit at all rather than
            // letting it go negative.
            if (effectiveOut <= effectiveIn)
                continue;

            totalHours += (effectiveOut - effectiveIn).TotalHours;
            if (qualifiesForNightDiff)
            {
                nightDiffHours += NightDifferentialCalculator.CalculateHours(
                    effectiveIn, effectiveOut, policy.NightDiffStart, policy.NightDiffEnd);
            }
        }

        summary.Worked_H = totalHours;
        summary.Worked_T = TimeSpan.FromHours(totalHours);
        summary.NightDiff_H = nightDiffHours;
        summary.NightDiff_T = TimeSpan.FromHours(nightDiffHours);
        if (qualifiesForNightDiff)
        {
            summary.NightDiffRatePercentageOverride = schedule.NightDiffRatePercentageOverride;
        }

        summary.Status = pairing.Pairs.Count > 0 && !pairing.HasUnpairedPunch
            ? PunchStatus.Complete
            : PunchStatus.Partial;

        if (schedule.WorkTimeHours is { } requiredHours)
        {
            double requiredHoursDouble = (double)requiredHours;

            if (totalHours < requiredHoursDouble)
            {
                summary.Remain_H = requiredHoursDouble - totalHours;
                summary.Remain_T = TimeSpan.FromHours(summary.Remain_H);
            }
            else if (totalHours > requiredHoursDouble && ResolveOvertimeEligible(schedule))
            {
                // ScheduleEntry.OvertimeEligibleOverride / Employee.QualifiesForOvertime --
                // same PH Labor Code Art. 82 exemption as QualifiesForNightDiff above,
                // applied independently.
                summary.Overtime_H = totalHours - requiredHoursDouble;
                summary.Overtime_T = TimeSpan.FromHours(summary.Overtime_H);
                summary.OvertimeRatePercentageOverride = schedule.OvertimeRatePercentageOverride;
                summary.ApplyOvertimeRatePercentage = ResolveApplyOvertimeRatePercentage(schedule);
            }
        }
    }

    /// <summary>ScheduleEntry.OvertimeEligibleOverride takes priority when set (a
    /// per-day exception); missing override falls back to
    /// Employee.QualifiesForOvertime, and missing Employee (shouldn't normally
    /// happen) defaults to eligible.</summary>
    private static bool ResolveOvertimeEligible(ScheduleEntry schedule) =>
        schedule.OvertimeEligibleOverride ?? schedule.Employee?.QualifiesForOvertime ?? true;

    /// <summary>Same idea as <see cref="ResolveOvertimeEligible"/>, but for the
    /// separate "does the premium actually apply, once eligible" toggle -- see
    /// AttendanceSummary.ApplyOvertimeRatePercentage's doc comment.</summary>
    private static bool ResolveApplyOvertimeRatePercentage(ScheduleEntry schedule) =>
        schedule.ApplyOvertimeRatePercentageOverride ?? schedule.Employee?.ApplyOvertimeRatePercentageByDefault ?? true;
}
