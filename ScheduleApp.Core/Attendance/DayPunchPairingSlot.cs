namespace ScheduleApp.Core.Attendance;

/// <summary>
/// One punch placed into one In/Out slot of one segment of a
/// <see cref="DayPunchPairing"/> -- i.e. one filled cell in the Day Punch Pairing
/// editor grid.
/// </summary>
public class DayPunchPairingSlot
{
    public int Id { get; set; }

    public int DayPunchPairingId { get; set; }
    public DayPunchPairing? DayPunchPairing { get; set; }

    /// <summary>The paired punch's own primary key -- an
    /// <see cref="AttendanceLog.Id"/> when <see cref="IsManualPunch"/> is false, a
    /// <see cref="ManualAttendanceLog.Id"/> when it's true. The two id spaces are
    /// unrelated tables (see <see cref="AttendanceLogSource"/>.Manual's remarks),
    /// so <see cref="IsManualPunch"/> is what says which one this is.</summary>
    public int PunchId { get; set; }

    /// <summary>True when <see cref="PunchId"/> refers to a
    /// <see cref="ManualAttendanceLog"/> row rather than a device
    /// <see cref="AttendanceLog"/> row.</summary>
    public bool IsManualPunch { get; set; }

    /// <summary>0-based, gapless segment ordinal. Punches sharing a
    /// <see cref="SegmentIndex"/> form one work interval; its In slot pairs with its
    /// Out slot. It's the position of the segment *among the occupied ones* -- the
    /// editor can hold empty working rows between segments, but those carry no punch
    /// and aren't stored, so a saved pairing renumbers to a dense 0..N-1 (see
    /// DayPunchPairingEditorViewModel.BuildPairing). Consumers only ever group and
    /// order by this, never index an array with it.</summary>
    public int SegmentIndex { get; set; }

    public PairingRole Role { get; set; }
}
