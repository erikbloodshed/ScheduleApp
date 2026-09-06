using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Attendance;

/// <summary>
/// Shared "device punches first, a manual entry only fills in when the
/// device side has nothing" selection logic, used by
/// SingleWindowShiftCalculationStrategy (Normal), FlexibleShiftCalculationStrategy
/// (Flexible), and SplitShiftCalculationStrategy (SplitShift) so none of them
/// each grow their own slightly-different copy of it. A manual entry (see
/// ManualAttendanceLog.ToAttendanceLog, AttendanceLogSource.Manual) is just
/// another AttendanceLog in the same list by the time either strategy sees
/// it -- this is the one place that actually enforces "manual never outranks
/// a real punch," rather than leaving it to whichever one happens to sort
/// earliest/latest.
/// </summary>
internal static class PunchMatching
{
    /// <summary>The earliest punch in [windowStart, windowEnd] -- preferring
    /// any non-Manual (real device) punch if the window has at least one;
    /// only falls back to the earliest Manual-sourced punch in the window
    /// when it doesn't.</summary>
    public static AttendanceLog? EarliestInWindow(
        IEnumerable<AttendanceLog> punches, DateTime windowStart, DateTime windowEnd)
    {
        var inWindow = punches.Where(p => p.Timestamp >= windowStart && p.Timestamp <= windowEnd);
        return PreferDevice(inWindow, ascending: true);
    }

    /// <summary>The latest punch in [windowStart, windowEnd] -- same
    /// device-first preference as EarliestInWindow, and optionally excluding
    /// one specific punch (e.g. whichever one was already claimed as the
    /// clock-in for this same slot) so it can't satisfy both roles at once.</summary>
    public static AttendanceLog? LatestInWindow(
        IEnumerable<AttendanceLog> punches, DateTime windowStart, DateTime windowEnd, AttendanceLog? exclude = null)
    {
        var inWindow = punches.Where(p => p.Timestamp >= windowStart && p.Timestamp <= windowEnd && p != exclude);
        return PreferDevice(inWindow, ascending: false);
    }

    /// <summary>Every punch in [windowStart, windowEnd], with no device/manual
    /// preference applied -- unlike EarliestInWindow/LatestInWindow, which each
    /// return the single punch actually used for a slot, this returns every
    /// candidate that window considered. Used by each strategy to report which
    /// punches it looked at but didn't end up picking (see
    /// ShiftCalculationResult.UnclaimedPunches), not for matching itself.</summary>
    public static List<AttendanceLog> AllInWindow(
        IEnumerable<AttendanceLog> punches, DateTime windowStart, DateTime windowEnd) =>
        punches.Where(p => p.Timestamp >= windowStart && p.Timestamp <= windowEnd).ToList();

    private static AttendanceLog? PreferDevice(IEnumerable<AttendanceLog> inWindow, bool ascending)
    {
        // Materializing once avoids evaluating the (possibly re-used, e.g.
        // FlexibleShiftCalculationStrategy's dayPunches) source sequence twice.
        var candidates = inWindow.ToList();
        if (candidates.Count == 0)
            return null;

        var device = candidates.Where(p => p.Source != AttendanceLogSource.Manual);
        var ordered = ascending ? device.OrderBy(p => p.Timestamp) : device.OrderByDescending(p => p.Timestamp);
        var devicePunch = ordered.FirstOrDefault();
        if (devicePunch is not null)
            return devicePunch;

        var manual = candidates.Where(p => p.Source == AttendanceLogSource.Manual);
        var orderedManual = ascending ? manual.OrderBy(p => p.Timestamp) : manual.OrderByDescending(p => p.Timestamp);
        return orderedManual.FirstOrDefault();
    }
}
