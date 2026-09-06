using ScheduleApp.Core.Enums;

namespace ScheduleApp.Core.Attendance;

public enum PunchStatus
{
    Complete,           // Every punch found paired off (in+out, in+out, ...)
    Partial,            // At least one punch found, but an odd one out left unpaired
    Absent,             // No punches found at all
    Leave,              // Day marked as leave -- punch matching skipped
    OfficialBusiness,   // Day marked as official business -- punch matching skipped
    RestDay,            // Day marked as a rest day -- see RestDayShiftCalculationStrategy;
                         // still reflects whether the day was actually worked (and how much
                         // was earned for it) rather than skipping punch matching outright
                         // the way Leave/OfficialBusiness do.
}

/// <summary>
/// Display text for PunchStatus, kept separate from ToString() the same way
/// ScheduleTypeLabel is: callers that need a stable, code-shaped value (e.g. a
/// file name -- see AttendanceStatusDetailDialog) keep using ToString()/
/// interpolation directly, while anything shown to a person goes through
/// this instead.
/// </summary>
public static class PunchStatusLabel
{
    public static string ToText(this PunchStatus status) => status switch
    {
        PunchStatus.OfficialBusiness => "Official Business",
        PunchStatus.RestDay => "Rest Day",
        _ => status.ToString()
    };
}

/// <summary>
/// One employee's computed attendance result for one calendar day, produced by
/// comparing their ScheduleEntry for that day against their punches for that day.
/// </summary>
public class AttendanceSummary
{
    /// <summary>The employee's Employee ID -- i.e. Employee.Pin, the same
    /// punch-clock code shown in the Stored Punch Logs viewer and matched against
    /// in AttendanceWorkflowService -- *not* Employee.Id (the ScheduleApp database
    /// key), which has no meaning outside this app and was previously (wrongly)
    /// what ended up here. Every IShiftCalculationStrategy populates this from
    /// schedule.Employee.Pin for that reason.</summary>
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;

    /// <summary>Which ScheduleType produced this summary. Status alone can't tell a
    /// Flexible day apart from a Normal one -- both report Complete/Partial/Absent --
    /// so AttendanceExcelExporter reads this to label the Type column and to pick
    /// what RemainDuration/OvertimeDuration mean in that column's header tooltip. It is *not*
    /// what decides literal-values-vs-live-formula for WorkedDuration etc. -- that's
    /// HasScheduledWindow below, since a SplitShift row needs the formula
    /// just as much as a Normal row does.</summary>
    public ScheduleType ScheduleType { get; set; } = ScheduleType.Normal;

    /// <summary>True when CheckIn/CheckOut represent a real scheduled window worth
    /// reporting -- always true for Normal, true for a SplitShift row (see
    /// SplitShiftCalculationStrategy, which produces one AttendanceSummary per
    /// segment), and false for a Flexible day (no single window, restricted or
    /// not -- see FlexibleShiftCalculationStrategy) or a malformed Normal entry
    /// missing TimeIn/WorkTimeHours. AttendanceExcelExporter reads this instead of
    /// switching on ScheduleType, with one extra carve-out for Flexible's own
    /// CheckIn (see WriteScheduleColumns).</summary>
    public bool HasScheduledWindow { get; set; }

    /// <summary>Only meaningful when ScheduleType == Leave -- copied from
    /// ScheduleEntry.IsPaidLeave by LeaveShiftCalculationStrategy so
    /// ScheduleApp.Core.Payroll.PayrollCalculator can read Basic Pay's Leave case
    /// (Employee.DailyRate when paid, 0.00 when unpaid) straight off the
    /// attendance result without a second schedule lookup. Null for every other
    /// ScheduleType.</summary>
    public bool? IsPaidLeave { get; set; }

    // Date & points in time
    public DateOnly ShiftDate { get; set; }
    public decimal? Span { get; set; } = 0.0m;
    public TimeOnly CheckIn { get; set; }
    public TimeOnly CheckOut { get; set; }
    public TimeOnly? ClockIn { get; set; }
    public TimeOnly? ClockOut { get; set; }

    /// <summary>True when ClockIn/ClockOut came from a ManualAttendanceLog
    /// entry rather than a real device punch -- i.e. the device side had
    /// nothing for that slot and a manual entry filled the gap (manual never
    /// overrides a device punch that's actually present, see PunchMatching).
    /// Lets AttendanceExcelExporter and the WPF results grid flag which times
    /// in a report were typed in by hand rather than recorded by the clock.
    /// Both false whenever ClockIn/ClockOut is itself null.</summary>
    public bool ClockInIsManual { get; set; }
    public bool ClockOutIsManual { get; set; }

    // Durations
    public TimeSpan WorkedDuration { get; set; } = TimeSpan.Zero;
    public TimeSpan LateInDuration { get; set; } = TimeSpan.Zero;
    public TimeSpan EarlyOutDuration { get; set; } = TimeSpan.Zero;
    public TimeSpan RemainDuration { get; set; } = TimeSpan.Zero;
    public TimeSpan OvertimeDuration { get; set; } = TimeSpan.Zero;

    /// <summary>The portion of WorkedDuration that falls within AttendancePolicy.NightDiffStart/
    /// NightDiffEnd -- not an amount on top of WorkedDuration, but a subset of it (same relationship
    /// Excel's live formula computes; see AttendanceExcelExporter). Always zero for Official
    /// Business (see OfficialBusinessShiftCalculationStrategy) and for Leave/Absent/Partial
    /// days, since night diff only credits actual worked time.</summary>
    public TimeSpan NightDiffDuration { get; set; } = TimeSpan.Zero;

    // Decimal measurements
    public double WorkedHours { get; set; }
    public double RemainHours { get; set; }
    public double OvertimeHours { get; set; }
    public double NightDiffHours { get; set; }

    /// <summary>
    /// WorkedHours expressed as a fraction of a full work day, where "1 work day"
    /// is this day's own scheduled Work Time -- Span, i.e.
    /// ScheduleEntry.WorkTimeHours -- not a global standard-hours constant.
    /// Deliberately keyed off Span rather than PayrollPolicy.StandardHoursPerDay:
    /// the Attendance layer has no dependency on PayrollPolicy (see
    /// PayrollViewModel's own doc comment -- AttendancePolicy decides
    /// Complete/Partial/etc., PayrollPolicy decides what those figures are
    /// worth in pesos), and Span is already computed here for every day
    /// regardless.
    ///
    /// Null when Span itself is null or &lt;= 0 -- no scheduled Work Time to
    /// measure against (e.g. an unrestricted Flexible day with no
    /// WorkTimeHours set) -- so there's nothing to divide by; callers render
    /// this the same as any other not-applicable column, i.e. blank rather
    /// than a misleading 0.00.
    ///
    /// Capped at 1.0: hours beyond the scheduled Work Time already show up in
    /// OvertimeHours/OvertimeDuration, so this isn't a second place the same hours get
    /// credited -- a day with heavy overtime still reports OvertimeHours in full,
    /// it just doesn't also push WorkDay past 1.0. An Official Business day
    /// lands on exactly 1.0, since OfficialBusinessShiftCalculationStrategy
    /// sets WorkedHours equal to Span for that status.
    /// </summary>
    public decimal? WorkDay => Span is { } span && span > 0
        ? Math.Min((decimal)WorkedHours / span, 1.0m)
        : null;

    /// <summary>
    /// Per-day override of the overtime premium percentage, copied straight
    /// through from ScheduleEntry.OvertimeRatePercentageOverride by whichever
    /// IShiftCalculationStrategy produced this summary -- not merged against
    /// PayrollPolicy.OvertimeRatePercentage here, since the Attendance layer
    /// deliberately has no dependency on PayrollPolicy (see PayrollViewModel's
    /// own doc comment: AttendancePolicy decides whether a day is
    /// Complete/Partial/Absent/etc., PayrollPolicy decides what those figures
    /// are worth in pesos -- rate percentages belong to the latter). Null means
    /// "no per-day override" -- ScheduleApp.Payroll.PayrollCalculator resolves
    /// this the rest of the way against its own PayrollPolicy parameter
    /// (<c>day.OvertimeRatePercentageOverride ?? policy.OvertimeRatePercentage</c>),
    /// so this class still spares PayrollCalculator from having to re-read
    /// ScheduleEntry itself -- it just resolves the last step (the global
    /// default) where the policy value actually lives. Only meaningful when
    /// OvertimeHours > 0 and <see cref="ApplyOvertimeRatePercentage"/> is true.
    /// </summary>
    public decimal? OvertimeRatePercentageOverride { get; set; }

    /// <summary>Same idea as <see cref="OvertimeRatePercentageOverride"/>, but for
    /// ScheduleEntry.NightDiffRatePercentageOverride / PayrollPolicy.NightDiffRatePercentage.
    /// Only meaningful when NightDiffHours > 0.</summary>
    public decimal? NightDiffRatePercentageOverride { get; set; }

    /// <summary>
    /// The resolved answer to "does the overtime rate percentage premium above
    /// actually apply this day, or is overtime paid at straight hourly rate?" --
    /// ScheduleEntry.ApplyOvertimeRatePercentageOverride if set, else
    /// Employee.ApplyOvertimeRatePercentageByDefault. Fully resolved here (unlike
    /// the two rate-percentage fields above) since both inputs -- ScheduleEntry
    /// and Employee -- are already in hand at the point a shift-calculation
    /// strategy builds this summary, with no PayrollPolicy dependency needed.
    /// Independent of whether overtime is eligible at all (OvertimeHours is already
    /// zero for ineligible days, suppressed upstream by the shift-calculation
    /// strategies -- this flag only distinguishes straight-time overtime from
    /// premium overtime among days that *do* have overtime hours). No Night Diff
    /// equivalent -- once eligible, Night Diff's rate percentage always applies.
    /// </summary>
    public bool ApplyOvertimeRatePercentage { get; set; } = true;

    public PunchStatus Status { get; set; }
}