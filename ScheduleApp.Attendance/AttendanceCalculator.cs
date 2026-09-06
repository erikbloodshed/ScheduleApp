using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Public entry point for shift calculation. Picks a strategy from
/// <see cref="Strategies"/> keyed by schedule.ScheduleType instead of an
/// if/else chain -- see IShiftCalculationStrategy for why. Callers
/// (AttendanceWorkflowService) are unaffected by *which* strategy runs or how
/// many schedule types exist -- CalculateShift returns a ShiftCalculationResult
/// (one Summaries entry for most schedule types, one per segment for a
/// SplitShift day -- see IShiftCalculationStrategy) regardless.
/// </summary>
public static class AttendanceCalculator
{
    private static readonly IShiftCalculationStrategy SingleWindow = new SingleWindowShiftCalculationStrategy();

    private static readonly Dictionary<ScheduleType, IShiftCalculationStrategy> Strategies = new()
    {
        [ScheduleType.Leave] = new LeaveShiftCalculationStrategy(),
        [ScheduleType.Normal] = SingleWindow,
        [ScheduleType.Flexible] = new FlexibleShiftCalculationStrategy(),
        [ScheduleType.OfficialBusiness] = new OfficialBusinessShiftCalculationStrategy(),
        [ScheduleType.SplitShift] = new SplitShiftCalculationStrategy(),
        [ScheduleType.RestDay] = new RestDayShiftCalculationStrategy(),
    };

    /// <param name="pairingOverride">A pairing for this employee/day decided by
    /// hand in the Day Punch Pairing editor, or null (the overwhelmingly common
    /// case) for the normal ScheduleType-driven calculation. Only consulted for
    /// <see cref="ScheduleType.Flexible"/>: that's the type whose pairing is
    /// purely time-order and therefore the one a missed or duplicated tap
    /// actually breaks -- see OverriddenFlexibleShiftCalculationStrategy. Passing
    /// one for any other type is ignored rather than an error, so a caller
    /// (AttendanceWorkflowService) can look an override up without also having to
    /// re-check the schedule type. Handled here, ahead of the table below, rather
    /// than by giving <see cref="IShiftCalculationStrategy"/> a fourth parameter
    /// the other five implementations would all have to accept and ignore.</param>
    public static ShiftCalculationResult CalculateShift(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy,
        DayPunchPairing? pairingOverride = null)
    {
        if (pairingOverride is not null && schedule.ScheduleType == ScheduleType.Flexible)
        {
            return new OverriddenFlexibleShiftCalculationStrategy(pairingOverride)
                .Calculate(schedule, employeePunches, policy);
        }

        var strategy = Strategies.TryGetValue(schedule.ScheduleType, out var found)
            ? found
            : SingleWindow;

        return strategy.Calculate(schedule, employeePunches, policy);
    }
}
