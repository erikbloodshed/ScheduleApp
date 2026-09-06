namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Which side of a work interval a punch fills in a hand-edited
/// <see cref="DayPunchPairing"/> -- the clock-in end or the clock-out end of one
/// segment. Stored as a string on <see cref="DayPunchPairingSlot"/> (see
/// ScheduleDbContext), same readable-column convention as
/// <see cref="AttendanceLogSource"/>.
/// </summary>
public enum PairingRole
{
    In,
    Out,
}
