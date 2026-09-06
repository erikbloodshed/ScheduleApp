using ScheduleApp.Core.Enums;

namespace ScheduleApp.Core.Models;

/// <summary>
/// One employee's schedule for exactly one calendar day. There is at most one of
/// these per (EmployeeId, Date) -- setting a schedule for a day replaces whatever
/// was there before for that day rather than layering on top of it, so there's no
/// range overlap or override priority to reason about.
/// </summary>
public class ScheduleEntry
{
    public int Id { get; set; }

    /// <summary>The punch clock's own employee code -- matches Employee.Pin, same
    /// convention as AttendanceLog.EmployeeId/ManualAttendanceLog.EmployeeId/
    /// PayrollAdjustment.EmployeeId, NOT Employee.Id (the ScheduleApp database
    /// key). A real FK, targeting Employee.Pin as an alternate key (see
    /// ScheduleDbContext's own remarks on this entity) rather than Employee.Id --
    /// possible now that Pin is required and unique for every Employee row (see
    /// Employee.Pin's own doc comment), unlike the punch-log tables' EmployeeId,
    /// which still can't be FK-enforced the same way (a punch can reference a Pin
    /// ScheduleApp has never seen an Employee row for at all).</summary>
    public int EmployeeId { get; set; }

    /// <summary>A real EF navigation again (see ScheduleDbContext's own HasOne/
    /// HasPrincipalKey config for this entity) -- populated automatically by
    /// Include(s => s.Employee) wherever a query needs it, the same way it always
    /// was before Employee.Pin briefly went through a stretch of being optional.
    /// Null only when a query genuinely didn't Include it, same convention as
    /// every other lazy-unless-Included navigation in this codebase.</summary>
    public Employee? Employee { get; set; }

    public ScheduleType ScheduleType { get; set; } = ScheduleType.Normal;

    public DateOnly Date { get; set; }

    /// <summary>Shift length in hours for Normal; required total hours for the day
    /// for Flexible and SplitShift. Null for Leave entries. Also optionally set
    /// for OfficialBusiness and RestDay (see
    /// OfficialBusinessShiftCalculationStrategy/RestDayShiftCalculationStrategy)
    /// -- unlike Normal, it stays nullable rather than required for either of
    /// those two. For SplitShift, this is still a single number for the whole
    /// day even when the punching window is split across several
    /// <see cref="FlexibleSegments"/> -- it's the required total, not a
    /// per-segment amount.</summary>
    public decimal? WorkTimeHours { get; set; }

    /// <summary>Shift start time -- required for Normal (TimeOut is then
    /// derived from this plus WorkTimeHours, see below), and optionally set for
    /// OfficialBusiness and RestDay (same two strategies as
    /// <see cref="WorkTimeHours"/> above), where it likewise stays nullable
    /// rather than required. Null for Leave, and also null for
    /// Flexible/SplitShift: Flexible's optional single-window restriction lives
    /// in <see cref="RestrictedTimeIn"/>/<see cref="RestrictedTimeOut"/>
    /// instead, and SplitShift's windows live in <see cref="FlexibleSegments"/>
    /// instead, since there can be any number of them (including zero) rather
    /// than exactly one.</summary>
    public TimeOnly? TimeIn { get; set; }

    /// <summary>
    /// Only meaningful when ScheduleType == Leave; null for every other type,
    /// same nullable-when-inapplicable convention as WorkTimeHours/TimeIn above.
    /// True = paid leave (ScheduleApp.Core.Payroll.PayrollCalculator's Basic Pay
    /// for the day is Employee.DailyRate, paid outright since Leave carries no
    /// hours figure), false = unpaid leave (Basic Pay is 0.00 for the day) -- see
    /// AttendanceSummary.IsPaidLeave, which LeaveShiftCalculationStrategy copies
    /// this into so the calculator can read it straight off the attendance result.
    /// The write path that saves a Leave day (ApplyScheduleDialog/
    /// IScheduleRepository.SetScheduleForDatesAsync) is expected to default this
    /// to true unless the person explicitly picks Unpaid.
    /// </summary>
    public bool? IsPaidLeave { get; set; }

    /// <summary>
    /// Only meaningful for SplitShift: the day's punching windows, each an
    /// independent (TimeIn, TimeOut) pair -- see <see cref="FlexibleSegment"/>.
    /// Always empty for Normal/Leave/OfficialBusiness/Flexible -- Flexible's own,
    /// single-window restriction lives in <see cref="RestrictedTimeIn"/>/
    /// <see cref="RestrictedTimeOut"/> instead (see the ScheduleTimeRefactor
    /// notes). There's no data migration reclassifying old data, though: a row
    /// saved as segmented Flexible before SplitShift existed keeps whatever
    /// segments it already has and keeps reading as Flexible until someone
    /// re-applies the schedule and explicitly picks SplitShift, so this can
    /// still be non-empty on a Flexible row loaded from an older save -- treat
    /// that as legacy data, not a shape new code should produce.
    /// </summary>
    public List<FlexibleSegment> FlexibleSegments { get; set; } = new();

    /// <summary>
    /// Only meaningful for Flexible: optionally bounds the day's single allowed
    /// punching window. RestrictedTimeIn caps how early a punch counts (see
    /// FlexibleShiftCalculationStrategy); RestrictedTimeOut excludes punches
    /// after it from the day's search entirely. Unlike
    /// <see cref="FlexibleSegments"/> -- SplitShift's multi-window equivalent --
    /// this is exactly one window (or half of one, if only RestrictedTimeIn or
    /// only RestrictedTimeOut is set) for the whole day. Null (the common case)
    /// means no restriction on that side at all; both null is the same
    /// "every punch that day counts" fallback Flexible has always had. Same
    /// crosstime convention as <see cref="FlexibleSegment.CrossesMidnight"/>: a
    /// RestrictedTimeOut at or before RestrictedTimeIn means the window actually
    /// ends on the calendar day after <see cref="Date"/>. Always null for
    /// Normal/Leave/OfficialBusiness/SplitShift.
    /// </summary>
    public TimeOnly? RestrictedTimeIn { get; set; }

    /// <summary>Same idea as <see cref="RestrictedTimeIn"/>, but for the end of
    /// Flexible's single allowed punching window.</summary>
    public TimeOnly? RestrictedTimeOut { get; set; }

    /// <summary>
    /// Throws if this entry's fields don't match the shape its ScheduleType
    /// implies -- currently just that Flexible and SplitShift each carry only
    /// the field group that belongs to their own shape: a Flexible entry can't
    /// carry <see cref="FlexibleSegments"/>, and a SplitShift entry can't carry
    /// <see cref="RestrictedTimeIn"/>/<see cref="RestrictedTimeOut"/>. Meant to
    /// be called by write paths building/saving an entry (e.g.
    /// ScheduleRepository, defense in depth) -- deliberately NOT called by EF
    /// Core materialization, since existing segmented-Flexible rows saved
    /// before SplitShift existed are intentionally left as-is (no data
    /// migration -- see FlexibleSegments' own doc comment above), so a row
    /// loaded from an older save can still violate this and that's expected,
    /// not a bug.
    /// </summary>
    public void ValidateScheduleTypeShape()
    {
        if (ScheduleType == ScheduleType.Flexible && FlexibleSegments.Count > 0)
        {
            throw new InvalidOperationException(
                "A Flexible ScheduleEntry can't carry FlexibleSegments -- use ScheduleType.SplitShift instead.");
        }

        if (ScheduleType == ScheduleType.SplitShift && (RestrictedTimeIn is not null || RestrictedTimeOut is not null))
        {
            throw new InvalidOperationException(
                "A SplitShift ScheduleEntry can't carry RestrictedTimeIn/RestrictedTimeOut -- those belong to ScheduleType.Flexible.");
        }
    }

    /// <summary>
    /// Optional override for AttendancePolicy.ClockInBufferBefore, scoped to just
    /// this day. Null (the common case) means "use the policy default" -- see
    /// SingleWindowShiftCalculationStrategy, which reads this first and only
    /// falls back to the policy-wide value when it's null. Hours, same unit as
    /// the policy value it overrides. Only meaningful for Normal; always null
    /// for Flexible/SplitShift/Leave (SplitShift has its own, separately-scoped
    /// overrides -- see FlexibleSegment.ClockInBufferHours -- and Leave has no
    /// punch window to buffer at all).
    /// </summary>
    public double? ClockInBufferBeforeHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferBeforeHours"/>, but for
    /// AttendancePolicy.ClockInBufferAfter.</summary>
    public double? ClockInBufferAfterHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferBeforeHours"/>, but for
    /// AttendancePolicy.ClockOutBufferBefore.</summary>
    public double? ClockOutBufferBeforeHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferBeforeHours"/>, but for
    /// AttendancePolicy.ClockOutBufferAfter.</summary>
    public double? ClockOutBufferAfterHours { get; set; }

    /// <summary>
    /// Per-day override of Employee.QualifiesForOvertime. Null (the common case)
    /// means "use the employee's own default"; the resolved value ultimately
    /// falls back to <c>true</c> if even Employee is somehow missing -- see
    /// SingleWindowShiftCalculationStrategy/FlexibleShiftCalculationStrategy's
    /// ResolveOvertimeEligible. Setting this lets a specific day (or date range,
    /// applied day-by-day) diverge from the employee's usual eligibility without
    /// changing that employee-level default -- e.g. a one-off exception -- while
    /// the day-to-day common case (this employee is always/never eligible) is
    /// still driven entirely by the employee-level flag.
    /// </summary>
    public bool? OvertimeEligibleOverride { get; set; }

    /// <summary>Same idea as <see cref="OvertimeEligibleOverride"/>, but for
    /// Employee.QualifiesForNightDiff.</summary>
    public bool? NightDiffEligibleOverride { get; set; }

    /// <summary>
    /// Per-day override of Employee.ApplyOvertimeRatePercentageByDefault -- the
    /// third overtime state (see Employee.ApplyOvertimeRatePercentageByDefault
    /// and PayrollPolicy.OvertimeRatePercentage): whether overtime hours, once
    /// eligible, actually get the rate-percentage premium on top of straight
    /// hourly pay, or are paid straight-time instead. Null (the common case)
    /// means "use the employee's own default." Independent of
    /// OvertimeEligibleOverride above -- an employee can stay eligible for
    /// overtime throughout a transition period while only this toggle changes
    /// per day/date-range.
    /// </summary>
    public bool? ApplyOvertimeRatePercentageOverride { get; set; }

    /// <summary>
    /// Per-day override of PayrollPolicy.OvertimeRatePercentage. Null (the common
    /// case) means "use the global policy default." Only takes effect when
    /// overtime eligibility AND the apply-rate-% toggle both resolve true for the
    /// day -- see ScheduleApp.Payroll.PayrollCalculator.
    /// </summary>
    public decimal? OvertimeRatePercentageOverride { get; set; }

    /// <summary>Same idea as <see cref="OvertimeRatePercentageOverride"/>, but for
    /// PayrollPolicy.NightDiffRatePercentage -- only takes effect when night diff
    /// eligibility resolves true for the day (no separate apply-rate-% toggle for
    /// Night Diff, see Employee.ApplyOvertimeRatePercentageByDefault's doc
    /// comment for why Overtime has one and Night Diff doesn't).</summary>
    public decimal? NightDiffRatePercentageOverride { get; set; }

    /// <summary>
    /// Derived, exactly like the "=TimeIn + TIME(WorkTime,0,0)" formula in the
    /// original workbook this app replaced. Never stored, so it can never drift
    /// from TimeIn/WorkTime. Meaningless for Flexible (whose optional window is
    /// independently stored in <see cref="RestrictedTimeIn"/>/
    /// <see cref="RestrictedTimeOut"/>) and SplitShift (whose windows are
    /// independently stored per-segment -- see <see cref="FlexibleSegments"/>)
    /// -- neither derives from TimeIn/WorkTimeHours, so this is only ever used
    /// for Normal.
    /// </summary>
    public TimeOnly? TimeOut =>
        TimeIn is { } timeIn && WorkTimeHours is { } hours
            ? timeIn.Add(TimeSpan.FromHours((double)hours), out _)
            : null;

    /// <summary>True when a Normal, OfficialBusiness, or RestDay entry's
    /// TimeIn/WorkTimeHours window runs past midnight (e.g. 19:00 -> 05:00) --
    /// those three are the only ScheduleTypes with a single TimeIn/WorkTimeHours
    /// pair to derive this from (see the doc comments on each above). Always
    /// false for Flexible/SplitShift/Leave -- neither Flexible (whose optional
    /// single window lives in RestrictedTimeIn/RestrictedTimeOut instead,
    /// independently capable of crossing midnight) nor SplitShift (whose
    /// windows, each independently capable of crossing midnight, live in
    /// FlexibleSegments instead -- see FlexibleSegment.CrossesMidnight for the
    /// per-segment equivalent) has a single TimeIn/WorkTimeHours pair to derive
    /// this from, and Leave has no shift at all. OfficialBusiness/RestDay are
    /// also false whenever TimeIn/WorkTimeHours aren't set on them, same as
    /// Normal would be if it were somehow missing either (shouldn't normally
    /// happen for Normal, but both are legitimately optional for these
    /// two).</summary>
    public bool CrossesMidnight
    {
        get
        {
            if (ScheduleType != ScheduleType.Normal &&
                ScheduleType != ScheduleType.OfficialBusiness &&
                ScheduleType != ScheduleType.RestDay)
            {
                return false;
            }

            if (TimeIn is not { } timeIn || WorkTimeHours is not { } hours)
                return false;

            timeIn.Add(TimeSpan.FromHours((double)hours), out int wrappedDays);
            return wrappedDays > 0;
        }
    }

    /// <summary>Short text used in the calendar day cell, e.g. "8:00 AM-5:00 PM", "Leave",
    /// "Official Business" (or, when the entry has a scheduled TimeIn/WorkTimeHours --
    /// see OfficialBusinessShiftCalculationStrategy -- two lines: "Official Business"
    /// then "8:00 AM-5:00 PM"), "Flexible (8h, 5:00 AM-9:00 AM)" when RestrictedTimeIn/
    /// RestrictedTimeOut are both set, "Flexible (8h, from 5:00 AM)"/"Flexible (8h,
    /// until 9:00 AM)" when only one side is set (RestrictedTimeIn/RestrictedTimeOut
    /// are independently optional -- see their own doc comments), or "Split Shift (8h,
    /// 5:00 AM-9:00 AM + 1:00 PM-5:00 PM)". Times are shown 12-hour for display only --
    /// TimeIn/TimeOut/RestrictedTimeIn/RestrictedTimeOut/FlexibleSegments are still
    /// plain 24-hour TimeOnly underneath. A segment that crosses midnight (see
    /// FlexibleSegment.CrossesMidnight) gets the same "(+1)" suffix on its end
    /// time that a Normal overnight shift gets below, e.g. "10:00 PM-6:00 AM
    /// (+1)", so the cell doesn't silently imply the window ends earlier than it
    /// started.</summary>
    public string DisplayText => ScheduleType switch
    {
        ScheduleType.Leave => "Leave",
        ScheduleType.OfficialBusiness when TimeIn is not null && TimeOut is not null =>
            $"Official Business\n{TimeIn:h:mm tt}-{TimeOut:h:mm tt}{(CrossesMidnight ? " (+1)" : "")}",
        ScheduleType.OfficialBusiness => "Official Business",
        // Mirrors OfficialBusiness's own two-line/plain split immediately above,
        // for the same reason -- but RestDay's plain fallback ("Rest Day" alone,
        // no window) is reachable through completely ordinary use here, not just
        // legacy/malformed data: it's ApplyScheduleDialog's actual unscheduled
        // mode (see RestDayShiftCalculationStrategy.CalculateUnscheduledDay), not
        // a fallback for a missing field. Without this explicit case, that mode
        // fell through to the generic ScheduleType.ToString() below and showed
        // the raw enum name "RestDay" (no space) in the calendar cell.
        ScheduleType.RestDay when TimeIn is not null && TimeOut is not null =>
            $"Rest Day\n{TimeIn:h:mm tt}-{TimeOut:h:mm tt}{(CrossesMidnight ? " (+1)" : "")}",
        ScheduleType.RestDay => "Rest Day",
        ScheduleType.SplitShift when WorkTimeHours is { } hours && FlexibleSegments.Count > 0 =>
            $"Split Shift ({hours}h, {string.Join(" + ", FlexibleSegments.OrderBy(s => s.TimeIn).Select(s => $"{s.TimeIn:h:mm tt}-{s.TimeOut:h:mm tt}{(s.CrossesMidnight ? " (+1)" : "")}"))})",
        ScheduleType.SplitShift => WorkTimeHours is { } hours ? $"Split Shift ({hours}h)" : "Split Shift",
        ScheduleType.Flexible when WorkTimeHours is { } hours && RestrictedTimeIn is not null && RestrictedTimeOut is not null =>
            $"Flexible ({hours}h, {RestrictedTimeIn:h:mm tt}-{RestrictedTimeOut:h:mm tt})",
        ScheduleType.Flexible when WorkTimeHours is { } hours && RestrictedTimeIn is not null =>
            $"Flexible ({hours}h, from {RestrictedTimeIn:h:mm tt})",
        ScheduleType.Flexible when WorkTimeHours is { } hours && RestrictedTimeOut is not null =>
            $"Flexible ({hours}h, until {RestrictedTimeOut:h:mm tt})",
        ScheduleType.Flexible => WorkTimeHours is { } hours ? $"Flexible ({hours}h)" : "Flexible",
        _ when TimeIn is not null && TimeOut is not null =>
            $"{TimeIn:h:mm tt}-{TimeOut:h:mm tt}{(CrossesMidnight ? " (+1)" : "")}",
        _ => ScheduleType.ToString()
    };
}