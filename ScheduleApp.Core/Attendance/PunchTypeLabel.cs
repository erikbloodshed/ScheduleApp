namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Maps AttendanceLog/ManualAttendanceLog.PunchType to display text.
///
/// A manual entry can only ever carry 0 or 1 -- ManualLogEntryDialog's
/// PunchTypeCombo has exactly two items -- so this stays exhaustive for that
/// path exactly as the old `== 0 ? "Clock In" : "Clock Out"` checks were.
///
/// A real device punch pushed via ScheduleApp.PushListener can carry the
/// wider ADMS ATTLOG status set (see IClockController.DataUpload's remarks):
/// 2=Break Out, 3=Break In, 4=Overtime In, 5=Overtime Out. Those previously
/// all collapsed into "Clock Out" under a plain `== 0` check, which is
/// display-misleading once a device actually sends one (e.g. a terminal with
/// break/overtime function keys in use) -- this fixes the label without
/// changing anything about how a punch is matched to a shift: PunchMatching
/// (ScheduleApp.Attendance) only ever keys off Timestamp and Source, never
/// PunchType, so widening this mapping is purely cosmetic, not a change to
/// how hours are calculated.
///
/// The exact status-code semantics here are corroborated by community ADMS
/// implementations, not verified against ScheduleApp's own device -- if a
/// live push ever shows something under "Unknown (n)" that should have a
/// real label, that's the signal to add it here.
/// </summary>
public static class PunchTypeLabel
{
    public static string ToText(int punchType) => punchType switch
    {
        0 => "Clock In",
        1 => "Clock Out",
        2 => "Break Out",
        3 => "Break In",
        4 => "Overtime In",
        5 => "Overtime Out",
        _ => $"Unknown ({punchType})"
    };
}
