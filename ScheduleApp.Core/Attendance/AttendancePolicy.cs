namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Tunable rules for matching punches to a scheduled shift and computing
/// hours from the result. Loaded from ScheduleApp.Wpf/appsettings.json
/// ("Attendance:Policy") with these values as defaults.
/// </summary>
public class AttendancePolicy
{
    /// <summary>
    /// Search window buffers (in hours) -- how far from the scheduled time a
    /// punch is still considered "for this shift" rather than noise. These are
    /// only the defaults: an individual Normal ScheduleEntry can override any of
    /// the four for itself via ScheduleEntry.ClockInBufferBeforeHours/
    /// ClockInBufferAfterHours/ClockOutBufferBeforeHours/ClockOutBufferAfterHours,
    /// which take priority over these policy-wide values when set (see
    /// SingleWindowShiftCalculationStrategy). Most entries leave those null and
    /// just inherit whatever's configured here.
    /// </summary>
    public double ClockInBufferBefore { get; set; } = 2.0;
    public double ClockInBufferAfter { get; set; } = 2.0;
    public double ClockOutBufferBefore { get; set; } = 6.0;
    public double ClockOutBufferAfter { get; set; } = 6.0;

    /// <summary>
    /// Search window buffers (in hours) for a split shift segment's own start/end
    /// -- see SplitShiftCalculationStrategy. Field names are unchanged from
    /// before SplitShift existed as its own type (still "Flexible..." rather
    /// than "SplitShift..."), since FlexibleSegment itself wasn't renamed
    /// either -- see the ScheduleTimeRefactor notes. Deliberately separate from
    /// the Normal-shift buffers above: a segment is one piece of a split shift and
    /// is usually much shorter than a full Normal shift, so it gets its own
    /// symmetric, tighter default (+/-1h) rather than reusing buffers tuned for
    /// a whole-day window. A punch found within +/-FlexibleSegmentClockInBuffer
    /// of a segment's start is that segment's clock-in candidate; a punch found
    /// within +/-FlexibleSegmentClockOutBuffer of its end is its clock-out
    /// candidate -- independently, so a single punch can only ever satisfy one
    /// of the two, never both.
    ///
    /// These are only the defaults: an individual FlexibleSegment can override
    /// either one for itself via FlexibleSegment.ClockInBufferHours /
    /// ClockOutBufferHours, which take priority over these policy-wide values
    /// when set. Most segments leave those null and just inherit whatever's
    /// configured here.
    /// </summary>
    public double FlexibleSegmentClockInBuffer { get; set; } = 1.0;
    public double FlexibleSegmentClockOutBuffer { get; set; } = 1.0;

    /// <summary>
    /// Minimum gap (hours) between one paired punch-interval's clock-out and
    /// the next one's clock-in, for a Flexible day -- see
    /// FlexibleShiftCalculationStrategy.CalculateUnrestrictedDay. Two adjacent
    /// pairs only count as separate work intervals if the gap between them is
    /// at least this long; a shorter gap is treated as noise (e.g. a duplicate
    /// device tap, or too brief to be a real break) and the two pairs are
    /// merged into one continuous interval instead. SplitShift days don't use
    /// this -- see FlexibleSegmentClockInBuffer/FlexibleSegmentClockOutBuffer
    /// above for those.
    /// </summary>
    public double FlexibleMinimumBreakGap { get; set; } = 1.0;

    // Grace periods & caps
    public double ClockOutGracePeriod { get; set; } = 0.5;

    /// <summary>
    /// Grace period, in minutes, before a late clock-in or early clock-out actually
    /// counts against the employee. A punch within this many minutes of the scheduled
    /// start (LateInDuration) or scheduled end (EarlyOutDuration) is treated as exactly on time --
    /// both stay zero -- rather than reporting the literal difference down to the
    /// minute. Once the difference exceeds the grace period, the *entire* difference
    /// counts (not just the amount past the grace period) -- the same all-or-nothing
    /// shape as ClockOutGracePeriod's own overtime carve-out just above, which this is
    /// otherwise independent of: that one only clips the clock-out side for overtime
    /// purposes, this clips both LateInDuration and EarlyOutDuration for tardiness/undertime
    /// purposes (RemainDuration/RemainHours, and in turn ScheduleApp.Payroll.PayrollCalculator's
    /// undertime deduction, since both are just LateInDuration + EarlyOutDuration).
    ///
    /// Deliberately expressed in minutes rather than hours, unlike every other field on
    /// this class -- a grace period this small is naturally minute-scaled ("5" reads far
    /// better than "0.0833...").
    ///
    /// Used by SingleWindowShiftCalculationStrategy (Normal) and
    /// SplitShiftCalculationStrategy (each segment, against its own start/end) -- the
    /// same two strategies that populate LateInDuration/EarlyOutDuration at all. Flexible has no
    /// fixed target time to grace against (LateInDuration/EarlyOutDuration always stay zero there --
    /// see FlexibleShiftCalculationStrategy), and RestDay never populates them either
    /// (see RestDayShiftCalculationStrategy), so this has no effect on either. Default 5.
    /// </summary>
    public double LateInEarlyOutGraceMinutes { get; set; } = 5.0;

    public bool CapEarlyClockIn { get; set; } = true;
    public bool StrictOvertimeFromShiftEnd { get; set; } = true;

    /// <summary>
    /// The night-differential window (PH labor law's standard is 10:00 PM-6:00 AM;
    /// NightDiffEnd &lt;= NightDiffStart is what marks it as crossing midnight, the
    /// same convention ScheduleEntry.CrossesMidnight/FlexibleSegment.CrossesMidnight
    /// already use for TimeIn/TimeOut). Night diff is computed from each day's actual
    /// *worked* time -- effectiveTimeIn/effectiveTimeOut, the same capped/graced
    /// DateTimes SingleWindowShiftCalculationStrategy and FlexibleShiftCalculationStrategy
    /// already compute for WorkedDuration -- not the raw punches and not the scheduled
    /// CheckIn/CheckOut window, so it only ever credits hours actually on the clock
    /// (see NightDifferentialCalculator). Official Business is a deliberate exception:
    /// it's credited as fully worked without ever generating night diff, even if its
    /// scheduled window overlaps this range -- see OfficialBusinessShiftCalculationStrategy.
    /// </summary>
    public TimeOnly NightDiffStart { get; set; } = new TimeOnly(22, 0);
    public TimeOnly NightDiffEnd { get; set; } = new TimeOnly(6, 0);

    /// <summary>
    /// When true, the Excel summary export writes live-recalculating formulas
    /// instead of literal computed values, so the workbook stays correct if
    /// the buffer/grace values are tweaked by hand after export. Either way,
    /// AttendanceSummary itself always carries correct computed values --
    /// this only affects what AttendanceExcelExporter writes.
    /// </summary>
    public bool UseExcelFormula { get; set; } = true;

    /// <summary>
    /// A complete copy, for a caller changing some fields and carrying the rest
    /// through untouched -- see PayrollPolicy.Clone's own doc comment for the full
    /// reasoning; this is the same helper for the same reason, on the class with
    /// the longer hand-copied field list of the two.
    ///
    /// MemberwiseClone is a full copy here rather than a shallow one that aliases
    /// something, since every property on this class is a value type (double, bool,
    /// TimeOnly). Keep it that way, or this needs revisiting.
    /// </summary>
    public AttendancePolicy Clone() => (AttendancePolicy)MemberwiseClone();
}