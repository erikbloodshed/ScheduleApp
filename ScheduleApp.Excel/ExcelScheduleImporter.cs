using OfficeOpenXml;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Reads a workbook in the legacy layout (one worksheet per department; columns
/// Id, FirstName, ScheduleType, StartDate, EndDate, WorkTime, TimeIn, TimeOut)
/// into transient objects ready to hand to IScheduleRepository.ImportAsync.
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
/// re-export fresh copies rather than re-importing them.
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

                var pin = Convert.ToInt32(idValue);
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
                decimal? workTime = workTimeValue is null ? null : Convert.ToDecimal(workTimeValue);

                var runKey = new RunKey(pin, scheduleType, startDate, endDate, workTime);

                if (scheduleType == ScheduleType.SplitShift && previousRunKey is { } prevKey && runKey.Equals(prevKey) && previousRunEntries is not null)
                {
                    // Continuation of the same SplitShift run -- add this row's segment
                    // to every day already created for it, rather than creating a
                    // second, competing set of entries for the same dates (which
                    // ScheduleRepository.ImportAsync would treat as a later row
                    // overwriting an earlier one, silently dropping the first segment).
                    if (TryReadSegment(ws, row, out var segIn, out var segOut))
                    {
                        foreach (var entry in previousRunEntries)
                            entry.FlexibleSegments.Add(new FlexibleSegment { TimeIn = segIn, TimeOut = segOut });
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
                        RestrictedTimeOut = restrictedTimeOut
                    };
                    employee.ScheduleEntries.Add(newEntry);
                    newEntries.Add(newEntry);
                }

                if (scheduleType == ScheduleType.SplitShift && TryReadSegment(ws, row, out var firstSegIn, out var firstSegOut))
                {
                    foreach (var entry in newEntries)
                        entry.FlexibleSegments.Add(new FlexibleSegment { TimeIn = firstSegIn, TimeOut = firstSegOut });
                }

                previousRunKey = runKey;
                previousRunEntries = newEntries;
            }

            departments.Add(department);
        }

        return departments;
    }

    /// <summary>Reads columns 7/8 (TimeIn/TimeOut) as one SplitShift segment. False
    /// (with no out params set) when either cell is blank -- a SplitShift row with no
    /// window bounds contributes no segment, matching how ExcelScheduleExporter
    /// leaves both cells blank for a segment-less run. Only invoked for SplitShift
    /// rows -- Flexible's own RestrictedTimeIn/RestrictedTimeOut are read directly
    /// off columns 7/8 in the caller instead, since that's a single optional window,
    /// not a segment to accumulate.</summary>
    private static bool TryReadSegment(ExcelWorksheet ws, int row, out TimeOnly timeIn, out TimeOnly timeOut)
    {
        timeIn = default;
        timeOut = default;

        if (ws.Cells[row, 7].Value is null || ws.Cells[row, 8].Value is null)
            return false;

        timeIn = TimeOnly.FromTimeSpan(ws.Cells[row, 7].GetValue<DateTime>().TimeOfDay);
        timeOut = TimeOnly.FromTimeSpan(ws.Cells[row, 8].GetValue<DateTime>().TimeOfDay);
        return true;
    }

    private readonly record struct RunKey(int Pin, ScheduleType ScheduleType, DateOnly Start, DateOnly End, decimal? WorkTime);

    private static (string LastName, string FirstName) SplitName(string fullName)
    {
        var parts = fullName.Split(';', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (fullName, string.Empty);
    }
}
