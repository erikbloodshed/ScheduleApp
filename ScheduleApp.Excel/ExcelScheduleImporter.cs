using System.Globalization;
using OfficeOpenXml;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Reads a workbook in the legacy layout (one worksheet per department; columns
/// Id, FirstName, ScheduleType, StartDate, EndDate, WorkTime, TimeIn, TimeOut,
/// ClockInBufferBefore, ClockInBufferAfter, ClockOutBufferBefore,
/// ClockOutBufferAfter) into transient objects ready to hand to
/// IScheduleRepository.ImportAsync.
///
/// Columns 7/8 (TimeIn/TimeOut) carry a different meaning per ScheduleType,
/// mirroring ExcelScheduleExporter (see its own class doc comment for the full
/// per-type table):
/// - Normal/OfficialBusiness/RestDay: TimeIn is read as a literal; TimeOut is
///   ignored on read, since it's always re-derived from TimeIn + WorkTime. A
///   RestDay row exported from "no schedule at all" mode (see
///   RestDayShiftCalculationStrategy) has a blank column 7 like Leave, so
///   TimeIn comes back null the same way -- no RestDay-specific branch needed
///   here, same as on the export side.
/// - Leave: both ignored.
/// - Flexible: read straight off this row as RestrictedTimeIn/RestrictedTimeOut
///   (see ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut). Flexible is always
///   exactly one row per run now, so -- unlike SplitShift below -- it needs no
///   continuation logic.
/// - SplitShift: read as one segment (see TryReadSegment).
///   ExcelScheduleExporter emits one row per segment, all sharing the same
///   Id/ScheduleType/StartDate/EndDate/WorkTime -- so a SplitShift row here is
///   treated as a *continuation* of the immediately preceding row (adding another
///   segment to the same days) whenever those five fields match, rather than as a
///   new, separate run. The first row of a SplitShift run (or any row that
///   doesn't match the one before it) starts a fresh run as usual.
///
/// Columns 9-12 (the clock-in/clock-out buffer overrides, in hours) mirror the
/// exporter the same way, and a blank cell always means "no override -- inherit
/// the employee/policy default" rather than zero (see ReadBuffer):
/// - Normal/OfficialBusiness/RestDay: read straight into the entry's own
///   ClockInBufferBeforeHours/ClockInBufferAfterHours/ClockOutBufferBeforeHours/
///   ClockOutBufferAfterHours, each independently optional.
/// - Leave: exported blank (a Leave entry has no punch window to buffer), so all
///   four come back null through that same read -- no Leave-specific branch here,
///   exactly like TimeIn above.
/// - Flexible: all four ignored -- it matches punches across the whole day rather
///   than against a scheduled window, so there's no buffer to override.
/// - SplitShift: each segment carries a single symmetric clock-in buffer and a
///   single symmetric clock-out buffer (see FlexibleSegment.ClockInBufferHours/
///   ClockOutBufferHours), so the "Before" cell of each pair is read and the
///   "After" cell is used only as a fallback when the "Before" one is blank.
///   The exporter writes the same number into both halves; a hand-edited file
///   that fills in only one half still imports that value, and one that sets the
///   two halves to different numbers keeps the "Before" one, since there is no
///   asymmetric per-segment buffer to store the other in.
///
/// Each StartDate-EndDate row is expanded into one ScheduleEntry per day, since
/// the app now stores one schedule per employee per day rather than ranges. Rows
/// are expanded and added in sheet order, and later rows win when they land on
/// the same date as an earlier one (ScheduleRepository.ImportAsync applies them
/// in that order) -- which is how an old "Temporary override" row listed after
/// the baseline "Normal" row for the same date still takes effect. Any legacy
/// ScheduleType value this app no longer knows (e.g. old "Temporary" rows) falls
/// back to Normal via the TryParse below.
///
/// Backward compatibility: a file exported before this Phase 6a fix landed
/// (segmented Flexible, multiple rows per run sharing one Flexible ScheduleType)
/// does not round-trip correctly through this version -- each extra segment row
/// reads as a new one-row Flexible run overwriting the same dates, so only the
/// *last* segment row's window survives as RestrictedTimeIn/RestrictedTimeOut and
/// earlier segments are silently dropped. Treat old exported files as legacy and
/// re-export fresh copies rather than re-importing them. A file exported before
/// the buffer columns existed needs no such care, though: it simply has no
/// columns 9-12 at all, which reads as four blanks, i.e. the same "inherit the
/// default" every day without an override of its own already imports as.
/// </summary>
public static class ExcelScheduleImporter
{
    public static List<Department> Import(string filePath)
    {
        ExcelLicense.EnsureConfigured();

        using var package = new ExcelPackage(new FileInfo(filePath));
        var departments = new List<Department>();

        foreach (var ws in package.Workbook.Worksheets)
        {
            if (ws.Hidden != eWorkSheetHidden.Visible) continue;

            var department = new Department { Name = ws.Name.Trim() };
            var employeesByPin = new Dictionary<int, Employee>();

            // Tracks the run the *previous* row belongs to, so a SplitShift row that
            // matches it exactly (same employee/type/date-range/hours) can append
            // its segment to the entries already created for that run instead of
            // starting a new one.
            RunKey? previousRunKey = null;
            List<ScheduleEntry>? previousRunEntries = null;

            var lastRow = ws.Dimension?.End.Row ?? 1;
            for (var row = 2; row <= lastRow; row++)
            {
                var idValue = ws.Cells[row, 1].Value;
                if (idValue is null) continue;

                var pin = Convert.ToInt32(idValue, CultureInfo.InvariantCulture);
                var (lastName, firstName) = SplitName(ws.Cells[row, 2].Text.Trim());

                if (!employeesByPin.TryGetValue(pin, out var employee))
                {
                    employee = new Employee { Pin = pin, LastName = lastName, FirstName = firstName };
                    employeesByPin[pin] = employee;
                    department.Employees.Add(employee);
                }

                var typeText = ws.Cells[row, 3].Text.Trim();
                if (!Enum.TryParse<ScheduleType>(typeText, ignoreCase: true, out var scheduleType))
                    scheduleType = ScheduleType.Normal;

                var startDate = DateOnly.FromDateTime(ws.Cells[row, 4].GetValue<DateTime>());
                var endDate = DateOnly.FromDateTime(ws.Cells[row, 5].GetValue<DateTime>());

                var workTimeValue = ws.Cells[row, 6].Value;
                decimal? workTime = workTimeValue is null ? null : Convert.ToDecimal(workTimeValue, CultureInfo.InvariantCulture);

                var runKey = new RunKey(pin, scheduleType, startDate, endDate, workTime);

                if (scheduleType == ScheduleType.SplitShift && previousRunKey is { } prevKey && runKey.Equals(prevKey) && previousRunEntries is not null)
                {
                    // Continuation of the same SplitShift run -- add this row's segment
                    // to every day already created for it, rather than creating a
                    // second, competing set of entries for the same dates (which
                    // ScheduleRepository.ImportAsync would treat as a later row
                    // overwriting an earlier one, silently dropping the first segment).
                    // The buffer columns are deliberately NOT part of RunKey above:
                    // they vary per segment row within a single run (see the class doc
                    // comment), so including them would split one run into several.
                    if (TryReadSegment(ws, row, out var segIn, out var segOut, out var segInBuffer, out var segOutBuffer))
                    {
                        foreach (var entry in previousRunEntries)
                            entry.FlexibleSegments.Add(new FlexibleSegment
                            {
                                TimeIn = segIn,
                                TimeOut = segOut,
                                ClockInBufferHours = segInBuffer,
                                ClockOutBufferHours = segOutBuffer,
                            });
                    }

                    continue;
                }

                TimeOnly? timeIn = null;
                if (scheduleType != ScheduleType.Flexible && scheduleType != ScheduleType.SplitShift && ws.Cells[row, 7].Value is not null)
                    timeIn = TimeOnly.FromTimeSpan(ws.Cells[row, 7].GetValue<DateTime>().TimeOfDay);

                // Flexible is always exactly one row per run now -- no continuation
                // needed, unlike SplitShift above -- so RestrictedTimeIn/
                // RestrictedTimeOut are read straight off this row's columns 7/8,
                // each independently optional.
                TimeOnly? restrictedTimeIn = null;
                TimeOnly? restrictedTimeOut = null;
                if (scheduleType == ScheduleType.Flexible)
                {
                    if (ws.Cells[row, 7].Value is not null)
                        restrictedTimeIn = TimeOnly.FromTimeSpan(ws.Cells[row, 7].GetValue<DateTime>().TimeOfDay);
                    if (ws.Cells[row, 8].Value is not null)
                        restrictedTimeOut = TimeOnly.FromTimeSpan(ws.Cells[row, 8].GetValue<DateTime>().TimeOfDay);
                }

                // The four per-day overrides, for the types that actually match punches
                // against a scheduled window -- Flexible has none of its own, and
                // SplitShift's live per segment instead (both read as null here, then
                // picked up below/above from columns 9-12 in SplitShift's case). See the
                // class doc comment's columns 9-12 table.
                double? clockInBufferBefore = null;
                double? clockInBufferAfter = null;
                double? clockOutBufferBefore = null;
                double? clockOutBufferAfter = null;
                if (scheduleType != ScheduleType.Flexible && scheduleType != ScheduleType.SplitShift)
                {
                    clockInBufferBefore = ReadBuffer(ws, row, 9);
                    clockInBufferAfter = ReadBuffer(ws, row, 10);
                    clockOutBufferBefore = ReadBuffer(ws, row, 11);
                    clockOutBufferAfter = ReadBuffer(ws, row, 12);
                }

                var newEntries = new List<ScheduleEntry>();
                for (var date = startDate; date <= endDate; date = date.AddDays(1))
                {
                    var newEntry = new ScheduleEntry
                    {
                        ScheduleType = scheduleType,
                        Date = date,
                        WorkTimeHours = workTime,
                        TimeIn = timeIn,
                        RestrictedTimeIn = restrictedTimeIn,
                        RestrictedTimeOut = restrictedTimeOut,
                        ClockInBufferBeforeHours = clockInBufferBefore,
                        ClockInBufferAfterHours = clockInBufferAfter,
                        ClockOutBufferBeforeHours = clockOutBufferBefore,
                        ClockOutBufferAfterHours = clockOutBufferAfter
                    };
                    employee.ScheduleEntries.Add(newEntry);
                    newEntries.Add(newEntry);
                }

                if (scheduleType == ScheduleType.SplitShift &&
                    TryReadSegment(ws, row, out var firstSegIn, out var firstSegOut, out var firstSegInBuffer, out var firstSegOutBuffer))
                {
                    foreach (var entry in newEntries)
                        entry.FlexibleSegments.Add(new FlexibleSegment
                        {
                            TimeIn = firstSegIn,
                            TimeOut = firstSegOut,
                            ClockInBufferHours = firstSegInBuffer,
                            ClockOutBufferHours = firstSegOutBuffer,
                        });
                }

                previousRunKey = runKey;
                previousRunEntries = newEntries;
            }

            departments.Add(department);
        }

        return departments;
    }

    /// <summary>Reads columns 7/8 (TimeIn/TimeOut) as one SplitShift segment, along
    /// with that segment's own optional buffer overrides from columns 9/11 (each
    /// falling back to its "After" twin in column 10/12 when blank -- the pair is a
    /// single symmetric value per segment, see the class doc comment). False (with
    /// no out params set) when either time cell is blank -- a SplitShift row with no
    /// window bounds contributes no segment, matching how ExcelScheduleExporter
    /// leaves both cells blank for a segment-less run; its buffer cells are blank
    /// there too, and would have nothing to hang off of regardless. Only invoked for
    /// SplitShift rows -- Flexible's own RestrictedTimeIn/RestrictedTimeOut are read
    /// directly off columns 7/8 in the caller instead, since that's a single optional
    /// window, not a segment to accumulate.</summary>
    private static bool TryReadSegment(ExcelWorksheet ws, int row, out TimeOnly timeIn, out TimeOnly timeOut,
        out double? clockInBufferHours, out double? clockOutBufferHours)
    {
        timeIn = default;
        timeOut = default;
        clockInBufferHours = null;
        clockOutBufferHours = null;

        if (ws.Cells[row, 7].Value is null || ws.Cells[row, 8].Value is null)
            return false;

        timeIn = TimeOnly.FromTimeSpan(ws.Cells[row, 7].GetValue<DateTime>().TimeOfDay);
        timeOut = TimeOnly.FromTimeSpan(ws.Cells[row, 8].GetValue<DateTime>().TimeOfDay);
        clockInBufferHours = ReadBuffer(ws, row, 9) ?? ReadBuffer(ws, row, 10);
        clockOutBufferHours = ReadBuffer(ws, row, 11) ?? ReadBuffer(ws, row, 12);
        return true;
    }

    /// <summary>Reads one buffer-override cell as hours. Null for a blank cell --
    /// "inherit the employee/policy default", deliberately distinct from a 0 that
    /// really is in the cell, which is a genuine override meaning "no buffer at
    /// all" (see ExcelScheduleExporter.WriteBuffer for the writing half of the same
    /// convention). A cell holding text rather than a number (e.g. a stray note in
    /// a hand-edited file) throws, same as every other malformed cell in this
    /// importer -- ScheduleImportExportViewModel turns that into the generic
    /// "check that it matches the expected column layout" message.</summary>
    private static double? ReadBuffer(ExcelWorksheet ws, int row, int column)
    {
        var value = ws.Cells[row, column].Value;
        return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private readonly record struct RunKey(int Pin, ScheduleType ScheduleType, DateOnly Start, DateOnly End, decimal? WorkTime);

    private static (string LastName, string FirstName) SplitName(string fullName)
    {
        var parts = fullName.Split(';', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (fullName, string.Empty);
    }
}
