namespace ScheduleApp.Core.Attendance;

/// <summary>
/// Collapses one or more AttendanceSummary rows for the same employee/day into
/// a single day-level PunchStatus. Most schedule types (Normal, Leave,
/// Flexible) only ever produce one AttendanceSummary per day, so this is an
/// identity operation for them -- it only matters for a SplitShift day (see
/// SplitShiftCalculationStrategy), which produces one row per segment
/// rather than one row for the whole day. Counting those
/// rows directly by Status (as AttendanceViewModel's results panel and
/// AttendanceExcelExporter's totals both used to) silently counts a single
/// two-segment day as two, and can even count it toward two different
/// statuses at once (one segment Complete, the other Absent) -- both now
/// group by (EmployeeId, ShiftDate) and resolve through here instead, so a
/// dashboard count and the exported workbook's own totals always agree.
/// </summary>
public static class AttendanceDayStatus
{
    /// <summary>One resolved status per distinct (EmployeeId, ShiftDate) in the
    /// input, collapsing multiple segment rows for the same day into one.</summary>
    public static IEnumerable<PunchStatus> ByDay(IEnumerable<AttendanceSummary> summaries) =>
        summaries
            .GroupBy(s => (s.EmployeeId, s.ShiftDate))
            .Select(g => Resolve(g.Select(s => s.Status)));

    /// <summary>
    /// All-Complete -> Complete. All-Absent -> Absent. All-Leave -> Leave.
    /// All-OfficialBusiness -> OfficialBusiness. All-RestDay -> RestDay
    /// (Leave/OfficialBusiness/RestDay never actually mix with anything else
    /// in practice -- none of the three has segments, RestDay included -- but
    /// all three are handled the same way for completeness). Any other mix
    /// (e.g. one segment worked, the other missed) -> Partial, the same
    /// meaning Partial already has for a single Normal shift with only an in
    /// or only an out punch: something happened that day, but not uniformly.
    /// </summary>
    public static PunchStatus Resolve(IEnumerable<PunchStatus> statuses)
    {
        var list = statuses as ICollection<PunchStatus> ?? statuses.ToList();

        if (list.Count == 0) return PunchStatus.Absent; // defensive; every real day has >=1 row
        if (list.All(s => s == PunchStatus.Complete)) return PunchStatus.Complete;
        if (list.All(s => s == PunchStatus.Absent)) return PunchStatus.Absent;
        if (list.All(s => s == PunchStatus.Leave)) return PunchStatus.Leave;
        if (list.All(s => s == PunchStatus.OfficialBusiness)) return PunchStatus.OfficialBusiness;
        if (list.All(s => s == PunchStatus.RestDay)) return PunchStatus.RestDay;
        return PunchStatus.Partial;
    }
}
