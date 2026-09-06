using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Calculates a Flexible day whose punch pairing was decided by hand rather than
/// by time order -- i.e. a day with a saved <see cref="DayPunchPairing"/>. Reached
/// only through <see cref="AttendanceCalculator.CalculateShift"/>'s
/// pairingOverride parameter (see AttendanceWorkflowService, which looks the
/// override up per (employee, date)), never from the ScheduleType strategy table,
/// so a day with no override is completely unaffected.
///
/// Deliberately skips two things
/// <see cref="FlexibleShiftCalculationStrategy"/> does before pairing:
/// the device-beats-nearby-manual dedup, and the odd-count normalization that
/// drops the noisier side of the day's smallest adjacent gap. Both exist to guess
/// which punches are real when nothing better is known -- and here something
/// better *is* known, because a person looked at the day and said so. Guessing on
/// top of that would silently undo their edit.
///
/// The search boundary
/// (<see cref="FlexiblePairingBuilder.SearchWindow"/>) and the pairs-to-hours math
/// (<see cref="FlexibleWorkedHours.Populate"/>) are shared with the default path,
/// so an overridden day's Worked/NightDiff/Remain/Overtime figures are computed
/// exactly the same way -- only *which punches form which interval* differs. That
/// also means AttendanceExcelExporter, the Summary grid, and payroll need no
/// awareness of overrides at all: they just see an ordinary AttendanceSummary.
/// </summary>
internal sealed class OverriddenFlexibleShiftCalculationStrategy(DayPunchPairing pairing)
    : IShiftCalculationStrategy
{
    public ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy)
    {
        var summary = new AttendanceSummary
        {
            EmployeeId = schedule.Employee?.Pin ?? schedule.EmployeeId,
            EmployeeName = schedule.Employee?.DisplayName ?? "Unknown",
            Department = schedule.Employee?.Department?.Name ?? "(Unassigned)",
            ShiftDate = schedule.Date,
            ScheduleType = ScheduleType.Flexible,
            Span = schedule.WorkTimeHours,
        };

        // Same export-visibility carve-out as the default path -- see
        // FlexibleShiftCalculationStrategy. HasScheduledWindow stays false.
        if (schedule.RestrictedTimeIn is { } restrictedTimeInForDisplay)
        {
            summary.CheckIn = restrictedTimeInForDisplay;
        }

        var (searchStart, searchEnd) = FlexiblePairingBuilder.SearchWindow(schedule);
        var dayPunches = employeePunches
            .Where(p => p.Timestamp >= searchStart && p.Timestamp <= searchEnd)
            .OrderBy(p => p.Timestamp)
            .ToList();

        // A saved pairing for a day that now has no punches at all (every punch
        // deleted since it was saved) is still an Absent day, not an empty
        // Partial one -- same answer the default path gives, and the reason
        // FlexibleWorkedHours.Populate requires a non-empty punch list.
        if (dayPunches.Count == 0)
        {
            summary.Status = PunchStatus.Absent;
            return new ShiftCalculationResult
            {
                Summaries = [summary],
                ClaimedPunches = [],
                UnclaimedPunches = [],
            };
        }

        // GroupBy rather than ToDictionary: two slots naming the same punch would
        // throw on a duplicate key, and a corrupt/hand-edited row shouldn't take
        // a whole attendance run down. Last one wins, arbitrarily but predictably.
        var overrideMap = pairing.Slots
            .GroupBy(s => new PunchKey(s.PunchId, s.IsManualPunch))
            .ToDictionary(
                g => g.Key,
                g => (g.Last().SegmentIndex, g.Last().Role));

        var built = FlexiblePairingBuilder.BuildFromOverride(dayPunches, overrideMap);

        FlexibleWorkedHours.Populate(summary, built, schedule, policy, dayPunches);

        // Claimed = actually formed an interval. Everything else in the day --
        // a punch left unpaired by the saved layout, or one that arrived later
        // and auto-appended into a half-open segment -- is Unclaimed, so it
        // still surfaces on the report's Orphaned tile rather than vanishing.
        var claimed = built.Pairs.SelectMany(p => new[] { p.In, p.Out }).ToList();
        var unclaimed = dayPunches.Except(claimed).ToList();

        return new ShiftCalculationResult
        {
            Summaries = [summary],
            ClaimedPunches = claimed,
            UnclaimedPunches = unclaimed,
        };
    }
}
