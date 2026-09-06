namespace ScheduleApp.Core.Enums;

/// <summary>
/// A day's schedule is exactly one of these six things -- there's no overlap
/// or override to resolve, because ScheduleEntry stores one row per employee
/// per calendar day (see ScheduleEntry.Date).
/// </summary>
public enum ScheduleType
{
    Normal = 0,
    Leave = 1,

    /// <summary>An employee can clock in and out any number of times during the
    /// day (see FlexibleShiftCalculationStrategy), and WorkTimeHours means the
    /// day's required total, not a shift length to measure lateness against.
    /// ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut optionally bound *when*
    /// those punches are allowed, as a single (TimeIn, TimeOut) window for the
    /// day -- unlike Normal, that window's length has no fixed relationship to
    /// WorkTimeHours. Leaving both null (the common case) means no restriction
    /// at all: every punch that day counts.</summary>
    Flexible = 2,

    /// <summary>Same shape as Leave -- no punch is expected or required -- but
    /// kept as its own value rather than folded into Leave so the two can be
    /// reported on separately (see OfficialBusinessShiftCalculationStrategy,
    /// AttendanceDayStatus, and the Summary tab's own count tile). Used for
    /// days an employee is out on company business (e.g. an errand, a seminar,
    /// a client visit) rather than personal leave.</summary>
    OfficialBusiness = 3,

    /// <summary>ScheduleEntry.FlexibleSegments bounds *when* punches are
    /// allowed, as any number of (TimeIn, TimeOut) windows (e.g. 05:00-09:00 and
    /// 13:00-17:00 for a split shift) -- unlike Normal, the combined length of
    /// those windows has no fixed relationship to WorkTimeHours, which is still
    /// one number for the whole day, not a per-segment amount.</summary>
    SplitShift = 4,

    /// <summary>A day an employee isn't required to work at all -- their weekly
    /// rest day. Distinct from Leave/OfficialBusiness (which are also
    /// no-punch-expected days) because a Rest Day can still be worked: see
    /// RestDayShiftCalculationStrategy for the two modes (no schedule set at
    /// all vs. a WorkTimeHours/TimeIn actually punched against), and
    /// Employee.RestDayWorkPremiumPercentage for the premium paid when it is.
    /// Deliberately ordinal 5, not slotted in earlier among the existing
    /// values -- see Phase 0 of the implementation plan for why reordering
    /// the enum would silently reinterpret already-saved schedule rows.</summary>
    RestDay = 5
}

/// <summary>
/// Display text for ScheduleType, kept separate from ToString() so the raw
/// enum name (used for Excel schedule import/export round-tripping -- see
/// ExcelScheduleExporter/ExcelScheduleImporter, which read/write it as plain
/// text) never has to match what's shown in the UI.
/// </summary>
public static class ScheduleTypeLabel
{
    public static string ToText(this ScheduleType type) => type switch
    {
        ScheduleType.OfficialBusiness => "Official Business",
        ScheduleType.SplitShift => "Split Shift",
        ScheduleType.RestDay => "Rest Day",
        _ => type.ToString()
    };
}
