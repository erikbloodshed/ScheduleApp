using System.Globalization;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One row in the in-app Attendance Summary grid on the Attendance tab -- a
/// flattened, display-ready projection of an AttendanceSummary (one row per
/// segment for a SplitShift day, same as the underlying summaries -- see
/// SplitShiftCalculationStrategy). Built once per Generate
/// Reports run from AttendanceRunResult.Summaries; not edited in place, so it
/// doesn't need to be observable. Same pattern as StoredPunchLogRow.
///
/// The schedule-column blanking rules mirror AttendanceExcelExporter (Leave
/// rows show no times/durations; CheckIn/CheckOut are blank when
/// HasScheduledWindow is false, except that a Flexible day with a
/// RestrictedTimeIn still shows CheckIn alone -- see WriteScheduleColumns).
/// WorkDay/Remain/Overtime's duration text here blanks
/// a zero (or, for WorkDay, a null -- see AttendanceSummary.WorkDay) result
/// for readability, which the exported workbook deliberately does *not* do
/// for a zero -- AttendanceExcelExporter writes the literal value (including
/// zero) into those cells since WriteEmployeeTotalRow and WriteOverallTotalRow
/// both SumColumn() over them, which needs a real number there, not a blank
/// cell.
/// </summary>
public class AttendanceSummaryRow
{
    private const string Blank = "—";

    public int EmployeeId { get; init; }
    public required string EmployeeName { get; init; }
    public required string Department { get; init; }
    public required string TypeText { get; init; }
    public DateTime ShiftDate { get; init; }

    public required string CheckInText { get; init; }
    public required string CheckOutText { get; init; }
    public required string ClockInText { get; init; }
    public required string ClockOutText { get; init; }

    public required string WorkDayText { get; init; }
    public required string LateInText { get; init; }
    public required string EarlyOutText { get; init; }
    public required string RemainText { get; init; }
    public required string RemainHoursText { get; init; }
    public required string OvertimeText { get; init; }
    public required string OvertimeHoursText { get; init; }

    /// <summary>Blank on Official Business the same way it's blank for any other
    /// zero result -- s.NightDiff_H/NightDiff_T are always zero for OB (see
    /// OfficialBusinessShiftCalculationStrategy), so no separate check is needed
    /// here beyond the FormatDurationOrBlank/FormatHoursOrBlank zero-blanking
    /// every other duration column already gets.</summary>
    public required string NightDiffText { get; init; }
    public required string NightDiffHoursText { get; init; }

    /// <summary>Kept as the enum (not just StatusText) so the DataGrid's Status
    /// column can bind its Foreground through PunchStatusToBrushConverter.</summary>
    public PunchStatus Status { get; init; }

    public required string StatusText { get; init; }

    public static AttendanceSummaryRow FromSummary(AttendanceSummary s)
    {
        // Leave skips punch matching entirely and never has a scheduled window,
        // so it has nothing to show in the schedule/duration columns. Official
        // Business also skips punch matching, but -- when its entry carries a
        // scheduled TimeIn/WorkTimeHours -- still gets a real window and worked
        // total credited to it (see OfficialBusinessShiftCalculationStrategy),
        // so it's deliberately left out of this blanket blank-out and instead
        // follows the same HasScheduledWindow-driven rules as Normal/Flexible
        // below.
        bool noPunchExpected = s.Status is PunchStatus.Leave;

        return new AttendanceSummaryRow
        {
            EmployeeId = s.EmployeeId,
            EmployeeName = s.EmployeeName,
            Department = s.Department,
            TypeText = s.ScheduleType.ToText(),
            ShiftDate = s.ShiftDate.ToDateTime(TimeOnly.MinValue),

            // Same HasScheduledWindow check WriteScheduleColumns uses, for the
            // same reason -- a Flexible day has no single window to show
            // (see FlexibleShiftCalculationStrategy). The same carve-out as
            // the exporter applies to CheckIn, though: a Flexible day with a
            // RestrictedTimeIn still shows it here, for the same export-
            // visibility reason (see AttendanceSummary.CheckIn's copy of it),
            // with CheckOut left blank since RestrictedTimeOut is a search
            // filter, not a window edge. ClockIn/ClockOut need no separate
            // Leave/Official Business check: neither ever has them set (see
            // LeaveShiftCalculationStrategy/OfficialBusinessShiftCalculationStrategy),
            // so they're already null and fall through to Blank on their own.
            // The trailing " *" flags a manually-entered time (see
            // AttendanceSummary.ClockInIsManual/ClockOutIsManual) -- same
            // signal as AttendanceExcelExporter's italic + cell comment, just
            // as plain text since this grid's columns are already strings.
            CheckInText = !noPunchExpected && (s.HasScheduledWindow
                || (s.ScheduleType == ScheduleType.Flexible && s.CheckIn != default))
                ? FormatTime(s.CheckIn) : Blank,
            CheckOutText = !noPunchExpected && s.HasScheduledWindow ? FormatTime(s.CheckOut) : Blank,
            ClockInText = s.ClockIn is { } ci ? FormatTime(ci) + (s.ClockInIsManual ? " *" : "") : Blank,
            ClockOutText = s.ClockOut is { } co ? FormatTime(co) + (s.ClockOutIsManual ? " *" : "") : Blank,

            // Durations/hours are blank on Leave/Official Business -- same as
            // WriteHoursAndTimeColumns skipping the whole row for either.
            // All duration/hours columns blank out a zero result rather than
            // cluttering the grid with "0:00"/"0.00" -- same treatment
            // Late In/Early Out and the Hours columns already had.
            WorkDayText = noPunchExpected ? Blank : FormatWorkDayOrBlank(s.WorkDay),
            LateInText = noPunchExpected ? Blank : FormatDurationOrBlank(s.LateIn_T),
            EarlyOutText = noPunchExpected ? Blank : FormatDurationOrBlank(s.EarlyOut_T),
            RemainText = noPunchExpected ? Blank : FormatDurationOrBlank(s.Remain_T),
            RemainHoursText = noPunchExpected ? Blank : FormatHoursOrBlank(s.Remain_H),
            OvertimeText = noPunchExpected ? Blank : FormatDurationOrBlank(s.Overtime_T),
            OvertimeHoursText = noPunchExpected ? Blank : FormatHoursOrBlank(s.Overtime_H),
            NightDiffText = noPunchExpected ? Blank : FormatDurationOrBlank(s.NightDiff_T),
            NightDiffHoursText = noPunchExpected ? Blank : FormatHoursOrBlank(s.NightDiff_H),

            Status = s.Status,
            StatusText = s.Status.ToText(),
        };
    }

    /// <summary>Groups summaries by employee, orders by department then employee
    /// name (so the grid reads department-by-department rather than in whatever
    /// order AttendanceWorkflowService happened to produce them), and flattens
    /// back out to individual rows via FromSummary. Used by ReportViewModel for
    /// the full Summary grid, and reused (via the same status-based filtering)
    /// wherever a caller needs one status's rows on their own -- see
    /// ReportViewModel.ShowStatusDetail.</summary>
    public static IEnumerable<AttendanceSummaryRow> BuildRows(IEnumerable<AttendanceSummary> summaries) =>
        summaries
            .GroupBy(s => s.EmployeeId)
            .OrderBy(g => g.First().Department)
            .ThenBy(g => g.First().EmployeeName)
            .SelectMany(g => g)
            .Select(FromSummary);

    // 12-hour with AM/PM (e.g. "5:00 PM") for on-screen display only --
    // AttendanceExcelExporter's "[h]:mm" Excel number format for
    // CheckIn/CheckOut/ClockIn/ClockOut is unaffected, since that's a
    // separate, unrelated formatting path over the same underlying TimeOnly.
    private static string FormatTime(TimeOnly t) => TimeDisplayFormat.Format(t);

    // Same elapsed-hours idea as FormatTime, but for a TimeSpan duration
    // (Worked_T etc.) instead of a time-of-day -- matches Excel's "[h]:mm"
    // bracket format, i.e. total elapsed hours rather than hours-mod-24.
    private static string FormatDuration(TimeSpan ts)
    {
        string sign = ts < TimeSpan.Zero ? "-" : "";
        TimeSpan abs = ts.Duration();
        return $"{sign}{(int)abs.TotalHours}:{abs.Minutes:00}";
    }

    private static string FormatHours(double hours) => hours.ToString("0.00", CultureInfo.CurrentCulture);

    // Blanks a duration that would otherwise display as "0:00" -- checks the
    // *formatted* text rather than comparing ts to TimeSpan.Zero directly, so
    // a value that merely rounds down to "0:00" (there's no sub-minute
    // display here, so this only matters if a computed value is a handful of
    // seconds off from exactly zero) is blanked the same way an exact zero
    // is, i.e. consistently with what's actually shown.
    private static string FormatDurationOrBlank(TimeSpan ts)
    {
        string formatted = FormatDuration(ts);
        return formatted is "0:00" or "-0:00" ? Blank : formatted;
    }

    // Same idea as FormatDurationOrBlank, for the decimal-hours columns.
    private static string FormatHoursOrBlank(double hours)
    {
        string formatted = FormatHours(hours);
        return formatted is "0.00" or "-0.00" ? Blank : formatted;
    }

    // AttendanceSummary.WorkDay is already null when there's nothing to show
    // (no Span to measure against) -- that's blanked directly, same as every
    // other not-applicable column. A non-null 0.00 (e.g. an Absent day, which
    // still carries a scheduled Span) is blanked too, same zero-blanking
    // FormatHoursOrBlank already does for readability.
    private static string FormatWorkDayOrBlank(decimal? workDay)
    {
        if (workDay is not { } wd)
            return Blank;

        string formatted = wd.ToString("0.00", CultureInfo.CurrentCulture);
        return formatted is "0.00" or "-0.00" ? Blank : formatted;
    }
}