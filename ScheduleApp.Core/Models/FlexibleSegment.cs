namespace ScheduleApp.Core.Models;

/// <summary>
/// One allowed punching window within a single SplitShift <see cref="ScheduleEntry"/>
/// day, e.g. 05:00-09:00. A SplitShift day can have any number of these (including
/// zero, meaning a split shift day with no windows configured yet). When there's
/// more than one, they represent separate windows the same day (e.g. 05:00-09:00
/// and 13:00-17:00) -- see SplitShiftCalculationStrategy, which checks "inside
/// *any* segment" instead of one window. Segments belonging to the same entry
/// must not overlap (checked in whichever 24h cycle each segment falls in -- see
/// ApplyScheduleDialog.OkButton_Click and SplitShiftCalculationStrategy.Calculate,
/// which both apply the same +1 day adjustment described below before comparing).
///
/// TimeOut later than TimeIn is the usual case (e.g. 05:00-09:00), but a window
/// that runs past midnight is represented the same way ScheduleEntry.TimeIn/
/// WorkTimeHours can for Normal: TimeOut less than or equal to TimeIn (e.g.
/// 22:00-06:00) means the window actually ends on the calendar day *after*
/// ScheduleEntry.Date -- see CrossesMidnight below, and
/// SplitShiftCalculationStrategy, which adds a day to TimeOut whenever this is
/// true before computing the actual search window. TimeOut equal to TimeIn (zero
/// real duration either way) isn't accepted by the dialog -- remove the window
/// entirely instead of leaving a same-instant one.
/// </summary>
public class FlexibleSegment
{
    public int Id { get; set; }

    public int ScheduleEntryId { get; set; }
    public ScheduleEntry? ScheduleEntry { get; set; }

    public TimeOnly TimeIn { get; set; }
    public TimeOnly TimeOut { get; set; }

    /// <summary>True when this window runs past midnight (e.g. 22:00 -> 06:00),
    /// i.e. TimeOut is at or before TimeIn -- see the class doc comment above.
    /// Same convention as ScheduleEntry.CrossesMidnight, just derived from TimeOut
    /// directly rather than TimeIn+WorkTimeHours, since a segment's end is stored
    /// rather than computed.</summary>
    public bool CrossesMidnight => TimeOut <= TimeIn;

    /// <summary>
    /// Optional override for AttendancePolicy.FlexibleSegmentClockInBuffer, scoped to
    /// just this segment. Null (the common case) means "use the policy default" --
    /// see SplitShiftCalculationStrategy, which reads this first and only falls
    /// back to the policy-wide value when it's null. Hours, same unit as the policy
    /// value it overrides.
    /// </summary>
    public double? ClockInBufferHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferHours"/>, but for
    /// AttendancePolicy.FlexibleSegmentClockOutBuffer.</summary>
    public double? ClockOutBufferHours { get; set; }
}
