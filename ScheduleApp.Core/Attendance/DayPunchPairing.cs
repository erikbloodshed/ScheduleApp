namespace ScheduleApp.Core.Attendance;

/// <summary>
/// A hand-edited pairing of one employee's punches for one Flexible-schedule day
/// into work segments -- the authoritative answer to "which punch goes with which"
/// for that day, overriding FlexibleShiftCalculationStrategy's default
/// pair-by-time-order behaviour (see OverriddenFlexibleShiftCalculationStrategy).
///
/// Produced by the Day Punch Pairing editor (see the Desktop project's
/// DayPunchPairingEditor), which a reviewer opens on a Flexible day that came out
/// Partial -- a missed tap or a stray double-tap left an unpaired punch -- to drag
/// the day's punches into the right In/Out slots.
///
/// One row per (EmployeeId, Date) -- see the unique index in ScheduleDbContext.
/// The row simply not existing for a day means "no override, use the default
/// pairing"; deleting it is the editor's "reset to automatic". A saved override
/// still isn't rigid: a device punch that arrives later and isn't named by any
/// <see cref="DayPunchPairingSlot"/> is folded back in by timestamp with a
/// positional default role, which can flip the day back to Partial as a signal to
/// re-open the editor (see FlexiblePairingBuilder).
/// </summary>
public class DayPunchPairing
{
    public int Id { get; set; }

    /// <summary>The punch clock's own employee code -- matches Employee.Pin, same
    /// convention as <see cref="AttendanceLog.EmployeeId"/> /
    /// <see cref="ManualAttendanceLog.EmployeeId"/> / ScheduleEntry.EmployeeId,
    /// not Employee.Id.</summary>
    public int EmployeeId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Free-text name of whoever last saved this pairing -- there's no
    /// user/auth system in ScheduleApp, so this is just a typed name (defaults to
    /// the current Windows username in the editor), mirroring
    /// <see cref="ManualAttendanceLog.EnteredBy"/>.</summary>
    public string EditedBy { get; set; } = string.Empty;

    /// <summary>When this pairing was last saved -- distinct from
    /// <see cref="Date"/> (the day it describes). Mirrors
    /// <see cref="ManualAttendanceLog.CreatedAt"/>'s role, but updated on every
    /// save rather than only the first.</summary>
    public DateTime EditedAt { get; set; }

    /// <summary>One entry per non-empty cell in the editor grid. Grouped by
    /// <see cref="DayPunchPairingSlot.SegmentIndex"/> into segments, each with an
    /// In slot and/or an Out slot. Replaced wholesale on every save (see
    /// IDayPunchPairingRepository.SaveAsync).</summary>
    public List<DayPunchPairingSlot> Slots { get; set; } = new();
}
