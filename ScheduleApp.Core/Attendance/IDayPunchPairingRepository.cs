namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Abstraction over ScheduleApp's own DayPunchPairings/DayPunchPairingSlots tables
/// (see SqlDayPunchPairingRepository in ScheduleApp.Data) -- the hand-edited
/// per-day punch pairings a reviewer saves from the Day Punch Pairing editor.
///
/// Read by AttendanceWorkflowService (over the same padded period as the punch
/// query) so a Flexible day with a saved pairing is calculated from it rather than
/// the default time-order pairing; written by the editor. Registered in
/// App.xaml.cs alongside IManualAttendanceLogRepository.
/// </summary>
public interface IDayPunchPairingRepository
{
    /// <summary>Every saved pairing whose <see cref="DayPunchPairing.Date"/> is in
    /// [start, end], with <see cref="DayPunchPairing.Slots"/> populated.</summary>
    /// <param name="pins">Same meaning as
    /// IManualAttendanceLogRepository.GetLogsAsync's own pins parameter --
    /// null means every employee; otherwise restrict to these
    /// <see cref="DayPunchPairing.EmployeeId"/> (Pin) values.</param>
    Task<List<DayPunchPairing>> GetForRangeAsync(DateOnly start, DateOnly end,
        IReadOnlyCollection<int>? pins = null, CancellationToken cancellationToken = default);

    /// <summary>The saved pairing for one employee/day, or null when that day has
    /// no override (the common case). Slots populated.</summary>
    Task<DayPunchPairing?> GetAsync(int employeePin, DateOnly date,
        CancellationToken cancellationToken = default);

    /// <summary>Upserts by (EmployeeId, Date): inserts a new pairing, or replaces
    /// an existing one's <see cref="DayPunchPairing.Slots"/> wholesale and updates
    /// <see cref="DayPunchPairing.EditedBy"/>/<see cref="DayPunchPairing.EditedAt"/>.
    /// Id/EditedAt are assigned here.</summary>
    Task SaveAsync(DayPunchPairing pairing, CancellationToken cancellationToken = default);

    /// <summary>Removes the override for one employee/day -- the editor's "reset to
    /// automatic". No-op if there's nothing saved for that day, same as
    /// IManualAttendanceLogRepository.DeleteAsync.</summary>
    Task DeleteAsync(int employeePin, DateOnly date, CancellationToken cancellationToken = default);
}
