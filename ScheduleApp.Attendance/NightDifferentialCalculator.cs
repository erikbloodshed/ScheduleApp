using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// Shared night-differential overlap math, used by
/// SingleWindowShiftCalculationStrategy (Normal), FlexibleShiftCalculationStrategy
/// (Flexible), and SplitShiftCalculationStrategy (SplitShift, per segment) so
/// none of them each grow their own slightly-different copy of it, the same
/// reason PunchMatching is shared between them.
///
/// Always computed from the *actual* worked interval -- effectiveTimeIn/effectiveTimeOut,
/// the same capped/graced DateTimes each strategy already derives for Worked_T -- never
/// from the scheduled CheckIn/CheckOut window and never from the raw, uncapped punch
/// timestamps. This is a deliberate policy choice (PH labor law bases night differential
/// pay on hours actually rendered), not just a convenience: an employee who clocks in
/// early or stays late only gets night diff credit for the portion of that time (if any)
/// that's also within the configured window, exactly mirroring how Worked_T itself is
/// capped/graced first and only *then* measured.
/// </summary>
internal static class NightDifferentialCalculator
{
    /// <summary>
    /// Sums the overlap between [effectiveTimeIn, effectiveTimeOut) and every
    /// night-differential window the interval touches. A window runs from
    /// nightStart to nightEnd, wrapping past midnight into the next calendar day
    /// whenever nightEnd &lt;= nightStart (the standard 10 PM-6 AM case) -- same
    /// crosses-midnight convention ScheduleEntry/FlexibleSegment already use for
    /// TimeIn/TimeOut. Windows are anchored to *every* calendar day the interval
    /// spans (not just effectiveTimeIn's own day), so a shift long enough to
    /// stretch across more than one night -- rare, but possible with a long
    /// enough shift plus buffers -- is credited for each night it actually
    /// touches rather than only the first.
    /// </summary>
    public static double CalculateHours(
        DateTime effectiveTimeIn,
        DateTime effectiveTimeOut,
        TimeOnly nightStart,
        TimeOnly nightEnd)
    {
        if (effectiveTimeOut <= effectiveTimeIn)
            return 0;

        double totalHours = 0;

        // Start one day before effectiveTimeIn's own day: that earlier day's
        // window (nightStart *that* day through nightEnd the following day)
        // can still reach into the very start of the interval when nightStart
        // is late enough in the evening. Walk forward one calendar day at a
        // time through effectiveTimeOut's day -- each day's window can't reach
        // past the next day's own window start (nightEnd <= 24h after
        // nightStart), so consecutive days' windows never double-count the
        // same instant.
        DateOnly day = DateOnly.FromDateTime(effectiveTimeIn).AddDays(-1);
        DateOnly lastDay = DateOnly.FromDateTime(effectiveTimeOut);

        while (day <= lastDay)
        {
            DateTime windowStart = day.ToDateTime(nightStart);
            DateTime windowEnd = nightEnd <= nightStart
                ? day.AddDays(1).ToDateTime(nightEnd)
                : day.ToDateTime(nightEnd);

            DateTime overlapStart = effectiveTimeIn > windowStart ? effectiveTimeIn : windowStart;
            DateTime overlapEnd = effectiveTimeOut < windowEnd ? effectiveTimeOut : windowEnd;

            if (overlapEnd > overlapStart)
                totalHours += (overlapEnd - overlapStart).TotalHours;

            day = day.AddDays(1);
        }

        return totalHours;
    }

    /// <summary>
    /// ScheduleEntry.NightDiffEligibleOverride takes priority when set (a
    /// per-day exception); missing override falls back to
    /// Employee.QualifiesForNightDiff, and missing Employee (shouldn't
    /// normally happen) defaults to eligible. Was duplicated byte-for-byte as
    /// a private ResolveNightDiffEligible in SingleWindowShiftCalculationStrategy,
    /// FlexibleShiftCalculationStrategy, and SplitShiftCalculationStrategy;
    /// centralized here once RestDayShiftCalculationStrategy needed the exact
    /// same gate as a fourth caller -- same reasoning as CalculateHours
    /// above, just for the eligibility check itself rather than the hours sum.
    /// </summary>
    public static bool ResolveEligible(ScheduleEntry schedule) =>
        schedule.NightDiffEligibleOverride ?? schedule.Employee?.QualifiesForNightDiff ?? true;
}