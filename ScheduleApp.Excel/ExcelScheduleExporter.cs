using OfficeOpenXml;
using OfficeOpenXml.Table;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Writes departments/employees/schedule entries to an .xlsx workbook using the
/// same "one worksheet per department" layout as the original spreadsheet:
/// Id, FirstName, ScheduleType, StartDate, EndDate, WorkTime, TimeIn, TimeOut,
/// ClockInBufferBefore, ClockInBufferAfter, ClockOutBufferBefore, ClockOutBufferAfter.
/// Internally each day is stored separately (ScheduleEntry.Date), but consecutive
/// days with an identical type/hours/time-in/restricted-window/segment-set/buffer
/// overrides are collapsed back into a single StartDate-EndDate run here, purely so
/// the export stays compact and readable -- this has no bearing on how the data is
/// stored or edited in the app.
///
/// Columns 7/8 (TimeIn/TimeOut) carry a different meaning per ScheduleType rather
/// than adding new columns for Flexible/SplitShift's own fields (see the
/// ScheduleTimeRefactor Phase 6a notes):
/// - Normal/OfficialBusiness/RestDay: TimeIn is a literal, TimeOut is a formula
///   (TimeIn + WorkTime hours) -- never a literal, exactly like the source
///   workbook -- so it stays correct if someone tweaks a cell by hand later.
///   Always exactly one row. RestDay's "no schedule at all" mode (see
///   RestDayShiftCalculationStrategy) exports the same as Leave -- both cells
///   blank -- purely because TimeIn/WorkTimeHours are both null on the entry,
///   not because of any RestDay-specific branch here.
/// - Leave: both blank. Always exactly one row.
/// - Flexible: RestrictedTimeIn/RestrictedTimeOut as literals (see
///   ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut), each independently
///   optional -- blank/blank when both are null, same as an unrestricted Flexible
///   day has always exported. Always exactly one row per run.
/// - SplitShift: a run with N segments (see ScheduleEntry.FlexibleSegments) emits
///   N rows sharing the same StartDate-EndDate, one segment per row as literals in
///   these same two columns -- there's no per-segment WorkTime to derive TimeOut
///   from, unlike Normal. A SplitShift run with zero segments still emits exactly
///   one row, with both cells left blank.
///
/// Columns 9-12 (the clock-in/clock-out buffer overrides, in hours) follow that
/// same per-ScheduleType reuse. They are blank whenever the day carries no override
/// of its own, and blank means "inherit" -- fall back to the employee's own default
/// and then to the app-wide AttendancePolicy (see NormalBufferResolver) -- which is
/// the common case, so most exported rows leave all four empty:
/// - Normal/OfficialBusiness/RestDay: the entry's own four per-day overrides
///   (ScheduleEntry.ClockInBufferBeforeHours/ClockInBufferAfterHours/
///   ClockOutBufferBeforeHours/ClockOutBufferAfterHours), each independently
///   optional, in that column order.
/// - SplitShift: buffers are per-segment and symmetric there -- one
///   FlexibleSegment.ClockInBufferHours covers both sides of the segment's start,
///   and one ClockOutBufferHours both sides of its end (see
///   SplitShiftCalculationStrategy). So each segment row writes its clock-in value
///   into BOTH columns 9 and 10, and its clock-out value into BOTH columns 11 and
///   12, rather than leaving the "After" halves blank and implying a one-sided
///   window. ExcelScheduleImporter reads the "Before" half back, falling back to
///   the "After" half only when the "Before" one is blank.
/// - Flexible/Leave: all four blank. Flexible matches punches across the whole day
///   (optionally bounded by RestrictedTimeIn/RestrictedTimeOut) rather than against
///   a scheduled window, so it has no buffer to override; Leave has no punch window
///   at all.
///
/// Unassigned employees (no department) get their own sheet.
/// </summary>
public static class ExcelScheduleExporter
{
    private static readonly string[] Headers =
    {
        "Id", "FirstName", "ScheduleType", "StartDate", "EndDate", "WorkTime", "TimeIn", "TimeOut",
        "ClockInBufferBefore", "ClockInBufferAfter", "ClockOutBufferBefore", "ClockOutBufferAfter"
    };

    private const string WorkTimeNumberFormat = "_-* #,##0.0_-;\\-* #,##0.0_-;_-* \"-\"?_-;_-@_-";

    /// <summary>Plain up-to-two-decimals, unlike WorkTime's accounting-style format
    /// above: a buffer cell is blank far more often than not (blank = inherit, see
    /// the class doc comment), and the accounting format's "-" placeholder branch
    /// would render those blanks as a dash, reading like a real, deliberately-zero
    /// override rather than an absent one.</summary>
    private const string BufferNumberFormat = "0.##";

    public static void Export(IEnumerable<Department> departments, IEnumerable<Employee> unassignedEmployees, string filePath)
    {
        ExcelLicense.EnsureConfigured();

        using var package = new ExcelPackage();

        foreach (var department in departments.OrderBy(d => d.SortOrder))
            WriteSheet(package, department.Name, department.Employees);

        var unassignedList = unassignedEmployees.ToList();
        if (unassignedList.Count > 0)
            WriteSheet(package, "Unassigned", unassignedList);

        package.SaveAs(new FileInfo(filePath));
    }

    private static void WriteSheet(ExcelPackage package, string sheetName, IEnumerable<Employee> employees)
    {
        var ws = package.Workbook.Worksheets.Add(ExcelSheetNaming.SanitizeSheetName(sheetName));

        for (var c = 0; c < Headers.Length; c++)
            ws.Cells[1, c + 1].Value = Headers[c];

        var row = 2;
        foreach (var employee in employees.OrderBy(e => e.Pin))
        {
            var displayId = employee.Pin;

            foreach (var run in CollapseIntoRuns(employee.ScheduleEntries))
            {
                // SplitShift emits one row per segment (all sharing this run's date
                // range); every other case (Normal, Leave, OfficialBusiness, Flexible,
                // RestDay, or a segment-less SplitShift run) is exactly one row -- see
                // the class doc comment's per-ScheduleType column table.
                var segmentsToEmit = run.ScheduleType == ScheduleType.SplitShift && run.Segments.Count > 0
                    ? run.Segments
                    : new List<RunSegment> { default };

                foreach (var segment in segmentsToEmit)
                {
                    ws.Cells[row, 1].Value = displayId;
                    ws.Cells[row, 2].Value = employee.DisplayName;
                    ws.Cells[row, 3].Value = run.ScheduleType.ToString();

                    ws.Cells[row, 4].Value = run.Start.ToDateTime(TimeOnly.MinValue);
                    ws.Cells[row, 4].Style.Numberformat.Format = "mm-dd-yy";

                    ws.Cells[row, 5].Value = run.End.ToDateTime(TimeOnly.MinValue);
                    ws.Cells[row, 5].Style.Numberformat.Format = "mm-dd-yy";

                    if (run.WorkTimeHours is { } hours)
                        ws.Cells[row, 6].Value = hours;
                    ws.Cells[row, 6].Style.Numberformat.Format = WorkTimeNumberFormat;

                    if (run.ScheduleType == ScheduleType.SplitShift)
                    {
                        // Columns 7/8 (TimeIn/TimeOut) carry this row's segment as
                        // literals, or stay blank for a segment-less SplitShift run --
                        // see the class doc comment.
                        if (run.Segments.Count > 0)
                        {
                            ws.Cells[row, 7].Value = new DateTime(1899, 12, 30).Add(segment.TimeIn.ToTimeSpan());
                            ws.Cells[row, 7].Style.Numberformat.Format = "h:mm";

                            ws.Cells[row, 8].Value = new DateTime(1899, 12, 30).Add(segment.TimeOut.ToTimeSpan());
                            ws.Cells[row, 8].Style.Numberformat.Format = "h:mm";

                            // This segment's own symmetric buffer overrides, written to
                            // both halves of each pair -- see the class doc comment's
                            // columns 9-12 table.
                            WriteBuffer(ws, row, 9, segment.ClockInBufferHours);
                            WriteBuffer(ws, row, 10, segment.ClockInBufferHours);
                            WriteBuffer(ws, row, 11, segment.ClockOutBufferHours);
                            WriteBuffer(ws, row, 12, segment.ClockOutBufferHours);
                        }
                    }
                    else if (run.ScheduleType == ScheduleType.Flexible)
                    {
                        // Columns 7/8 carry RestrictedTimeIn/RestrictedTimeOut as
                        // literals, each independently optional -- blank/blank when
                        // both are null, same as an unrestricted Flexible day has
                        // always exported. See the class doc comment.
                        if (run.RestrictedTimeIn is { } restrictedTimeIn)
                        {
                            ws.Cells[row, 7].Value = new DateTime(1899, 12, 30).Add(restrictedTimeIn.ToTimeSpan());
                            ws.Cells[row, 7].Style.Numberformat.Format = "h:mm";
                        }

                        if (run.RestrictedTimeOut is { } restrictedTimeOut)
                        {
                            ws.Cells[row, 8].Value = new DateTime(1899, 12, 30).Add(restrictedTimeOut.ToTimeSpan());
                            ws.Cells[row, 8].Style.Numberformat.Format = "h:mm";
                        }

                        // Columns 9-12 stay blank for Flexible -- it has no scheduled
                        // window to buffer against, see the class doc comment.
                    }
                    else
                    {
                        if (run.TimeIn is { } timeIn)
                        {
                            // Excel's time-only serials are anchored at 1899-12-30; combining
                            // that with the time and formatting as "h:mm" displays just the
                            // time part, exactly like the source workbook's TimeIn cell.
                            ws.Cells[row, 7].Value = new DateTime(1899, 12, 30).Add(timeIn.ToTimeSpan());
                            ws.Cells[row, 7].Style.Numberformat.Format = "h:mm";
                        }

                        // Plain cell references (not table structured references) so the
                        // formula is valid whether or not the range below becomes a Table.
                        // Only ever correct for Normal/OfficialBusiness/RestDay's
                        // TimeIn+WorkTime relationship (it evaluates to blank for Leave,
                        // and for a RestDay run with no schedule set, since F/G are both
                        // blank there too).
                        ws.Cells[row, 8].Formula =
                            $"IF(OR(ISBLANK(F{row}),ISBLANK(G{row})),\"\",G{row}+TIME(F{row},0,0))";
                        ws.Cells[row, 8].Style.Numberformat.Format = "h:mm";

                        // The entry's own four per-day overrides, each independently
                        // optional -- see the class doc comment's columns 9-12 table.
                        // Written for Leave too, where they're always null (so always
                        // blank), for the same reason columns 6/7 are: this branch is the
                        // single-TimeIn/WorkTime-window shape, and Leave simply has
                        // nothing in any of those fields rather than needing a branch of
                        // its own.
                        WriteBuffer(ws, row, 9, run.ClockInBufferBeforeHours);
                        WriteBuffer(ws, row, 10, run.ClockInBufferAfterHours);
                        WriteBuffer(ws, row, 11, run.ClockOutBufferBeforeHours);
                        WriteBuffer(ws, row, 12, run.ClockOutBufferAfterHours);
                    }

                    row++;
                }
            }
        }

        if (row > 2)
        {
            var tableRange = ws.Cells[1, 1, row - 1, Headers.Length];
            var table = ws.Tables.Add(tableRange, SanitizeTableName(sheetName));
            table.TableStyle = TableStyles.Light13;
        }

        if (ws.Dimension is not null)
            ws.Cells[ws.Dimension.Address].AutoFitColumns();

        ws.View.FreezePanes(2, 1);
    }

    /// <summary>Writes one buffer-override cell, leaving it genuinely empty (not a
    /// zero) when the override is null -- blank is what tells both a reader and
    /// ExcelScheduleImporter "inherit the employee/policy default", so a null must
    /// never round-trip as 0, which is itself a real override meaning "no buffer at
    /// all". The number format is applied either way, so a value typed into a blank
    /// cell by hand later still displays like the rest of the column.</summary>
    private static void WriteBuffer(ExcelWorksheet ws, int row, int column, double? hours)
    {
        if (hours is { } value)
            ws.Cells[row, column].Value = value;

        ws.Cells[row, column].Style.Numberformat.Format = BufferNumberFormat;
    }

    /// <summary>One emitted SplitShift segment row's window plus that segment's own
    /// optional symmetric buffer overrides (see FlexibleSegment.ClockInBufferHours/
    /// ClockOutBufferHours). A named struct rather than the bare (TimeIn, TimeOut)
    /// tuple this used to be, now that it carries two pairs of fields that read
    /// nothing alike.</summary>
    private readonly record struct RunSegment(
        TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours);

    private readonly record struct EntryRun(
        DateOnly Start, DateOnly End, ScheduleType ScheduleType, decimal? WorkTimeHours, TimeOnly? TimeIn,
        TimeOnly? RestrictedTimeIn, TimeOnly? RestrictedTimeOut,
        double? ClockInBufferBeforeHours, double? ClockInBufferAfterHours,
        double? ClockOutBufferBeforeHours, double? ClockOutBufferAfterHours,
        List<RunSegment> Segments);

    /// <summary>
    /// Groups an employee's per-day entries (sorted by date) into runs of consecutive
    /// days that all share the same type/hours/time-in/restricted-window/segment-set/
    /// buffer overrides, so the export reads like the original range-based workbook
    /// instead of one row per single day. The buffer overrides are part of that
    /// sameness test rather than just carried along from the run's last day: two
    /// otherwise identical consecutive days differing only in, say,
    /// ClockOutBufferAfterHours are genuinely different schedules now that the export
    /// carries those columns, and collapsing them would silently apply one day's
    /// override to the other.
    /// </summary>
    private static IEnumerable<EntryRun> CollapseIntoRuns(IEnumerable<ScheduleEntry> entries)
    {
        var sorted = entries.OrderBy(e => e.Date).ToList();
        if (sorted.Count == 0) yield break;

        var runStart = sorted[0].Date;
        var previous = sorted[0];

        for (var i = 1; i < sorted.Count; i++)
        {
            var current = sorted[i];
            var isContiguous = current.Date == previous.Date.AddDays(1);
            var isSameSchedule = current.ScheduleType == previous.ScheduleType &&
                                  current.WorkTimeHours == previous.WorkTimeHours &&
                                  current.TimeIn == previous.TimeIn &&
                                  current.RestrictedTimeIn == previous.RestrictedTimeIn &&
                                  current.RestrictedTimeOut == previous.RestrictedTimeOut &&
                                  current.ClockInBufferBeforeHours == previous.ClockInBufferBeforeHours &&
                                  current.ClockInBufferAfterHours == previous.ClockInBufferAfterHours &&
                                  current.ClockOutBufferBeforeHours == previous.ClockOutBufferBeforeHours &&
                                  current.ClockOutBufferAfterHours == previous.ClockOutBufferAfterHours &&
                                  SameSegments(current.FlexibleSegments, previous.FlexibleSegments);

            if (isContiguous && isSameSchedule)
            {
                previous = current;
                continue;
            }

            yield return ToRun(runStart, previous);
            runStart = current.Date;
            previous = current;
        }

        yield return ToRun(runStart, previous);
    }

    private static EntryRun ToRun(DateOnly runStart, ScheduleEntry entry) => new(
        runStart, entry.Date, entry.ScheduleType, entry.WorkTimeHours, entry.TimeIn,
        entry.RestrictedTimeIn, entry.RestrictedTimeOut,
        entry.ClockInBufferBeforeHours, entry.ClockInBufferAfterHours,
        entry.ClockOutBufferBeforeHours, entry.ClockOutBufferAfterHours,
        entry.FlexibleSegments
            .OrderBy(s => s.TimeIn)
            .Select(s => new RunSegment(s.TimeIn, s.TimeOut, s.ClockInBufferHours, s.ClockOutBufferHours))
            .ToList());

    /// <summary>Order-independent equality between two entries' segment sets --
    /// both are sorted by TimeIn first so a day whose segments were saved/loaded
    /// in a different order still collapses into the same run as an identical day.
    /// Each segment's own buffer overrides count toward that equality, same
    /// reasoning as the entry-level buffers in CollapseIntoRuns above: they're
    /// exported per segment row now, so two days whose segments share every window
    /// but differ in a buffer are not the same run.</summary>
    private static bool SameSegments(List<FlexibleSegment> a, List<FlexibleSegment> b)
    {
        if (a.Count != b.Count) return false;

        var sortedA = a.OrderBy(s => s.TimeIn).ToList();
        var sortedB = b.OrderBy(s => s.TimeIn).ToList();

        for (var i = 0; i < sortedA.Count; i++)
        {
            if (sortedA[i].TimeIn != sortedB[i].TimeIn ||
                sortedA[i].TimeOut != sortedB[i].TimeOut ||
                sortedA[i].ClockInBufferHours != sortedB[i].ClockInBufferHours ||
                sortedA[i].ClockOutBufferHours != sortedB[i].ClockOutBufferHours)
            {
                return false;
            }
        }

        return true;
    }

    private static string SanitizeTableName(string name)
    {
        var cleaned = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return "Tbl_" + (string.IsNullOrEmpty(cleaned) ? "Sheet" : cleaned) + "_" + Guid.NewGuid().ToString("N")[..6];
    }
}
