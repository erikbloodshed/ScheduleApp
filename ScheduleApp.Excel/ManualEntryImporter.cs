using System.Globalization;
using OfficeOpenXml;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Reads a manual-entries workbook like the one ExportManualLogsToExcel produces
/// (see AttendanceExcelExporter): a single sheet (any name, preferring one
/// literally named "Manual Entries" if there's more than one -- same convention
/// EmployeeRosterImporter uses for "Employees") whose first row names each column
/// by header text, so columns can appear in any order and optional ones can be
/// left out entirely. Recognized headers (matched case-insensitively, leading/
/// trailing whitespace trimmed):
///
///   Required: Id, Date, Time, Punch Type, Reason
///   Optional: Entered By, Employee Name, Department
///
/// See ManualEntryImportRow's own doc comment for what each column means and why
/// "Employee Name"/"Department" -- present on an exported file, so it can be
/// reused as an import template -- are accepted but otherwise ignored. Any other
/// column is ignored too. A data row that's entirely blank is skipped rather than
/// reported as an error, same as EmployeeRosterImporter.
///
/// Import validates the whole sheet before returning anything: a missing required
/// column, or any data row with a blank required field, an Id that doesn't match
/// any employee, an unparseable Date/Time, a Punch Type that isn't "Clock In"/
/// "Clock Out", or a Reason/Entered By longer than the database allows, adds to a
/// running list of problems rather than stopping at the first one. If that list
/// ends up non-empty, Import throws ManualEntryImportException carrying all of
/// them -- same shape as EmployeeRosterImporter.Import/EmployeeImportException,
/// just for manual attendance entries instead of the employee roster.
/// </summary>
public static class ManualEntryImporter
{
    private const string IdHeader = "Id";
    private const string DateHeader = "Date";
    private const string TimeHeader = "Time";
    private const string PunchTypeHeader = "Punch Type";
    private const string ReasonHeader = "Reason";
    private const string EnteredByHeader = "Entered By";

    /// <summary>Pass the full roster -- every employee has a Pin now (see Employee.Pin's
    /// own doc comment), so there's no one left to filter out here, same as
    /// ManualLogEntryDialog's own constructor.</summary>
    public static List<ManualEntryImportRow> Import(string filePath, IReadOnlyList<Employee> employees)
    {
        ExcelLicense.EnsureConfigured();

        using var package = new ExcelPackage(new FileInfo(filePath));
        var ws = package.Workbook.Worksheets
            .FirstOrDefault(w => w.Name.Equals("Manual Entries", StringComparison.OrdinalIgnoreCase))
            ?? package.Workbook.Worksheets.First();

        var lastCol = ws.Dimension?.End.Column ?? 0;
        var headerMap = BuildHeaderMap(ws, lastCol);

        var missingRequired = new[] { IdHeader, DateHeader, TimeHeader, PunchTypeHeader, ReasonHeader }
            .Where(header => !headerMap.ContainsKey(header))
            .ToList();
        if (missingRequired.Count > 0)
        {
            throw new ManualEntryImportException(new[]
            {
                $"The workbook is missing required column(s): {string.Join(", ", missingRequired)}."
            });
        }

        var idCol = headerMap[IdHeader];
        var dateCol = headerMap[DateHeader];
        var timeCol = headerMap[TimeHeader];
        var punchTypeCol = headerMap[PunchTypeHeader];
        var reasonCol = headerMap[ReasonHeader];
        var enteredByCol = Col(headerMap, EnteredByHeader);

        // Only the Pin matters for validation -- every employee has one now (see
        // Employee.Pin's own doc comment).
        var validPins = employees.Select(e => e.Pin).ToHashSet();

        var rows = new List<ManualEntryImportRow>();
        var errors = new List<string>();

        var lastRow = ws.Dimension?.End.Row ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            if (IsRowBlank(ws, row, lastCol)) continue;

            var rowErrors = new List<string>();

            var employeeId = ReadRequiredEmployeeId(ws, row, idCol, row, validPins, rowErrors);
            var date = ReadRequiredDate(ws, row, dateCol, "Date", row, rowErrors);
            var time = ReadRequiredTime(ws, row, timeCol, "Time", row, rowErrors);
            var punchType = ReadRequiredPunchType(ws, row, punchTypeCol, row, rowErrors);

            var reason = ReadRequiredText(ws, row, reasonCol, "Reason", row, rowErrors);
            if (reason is { Length: > 500 })
                rowErrors.Add($"Row {row}: Reason is too long ({reason.Length} characters; max 500).");

            // Blank/absent Entered By defaults to the current Windows username --
            // same fallback ManualLogEntryDialog's own Save handler applies when
            // EnteredByBox is left blank.
            var enteredBy = ReadOptionalText(ws, row, enteredByCol) ?? Environment.UserName;
            if (enteredBy.Length > 100)
                rowErrors.Add($"Row {row}: Entered By is too long ({enteredBy.Length} characters; max 100).");

            if (rowErrors.Count > 0)
            {
                errors.AddRange(rowErrors);
                continue;
            }

            rows.Add(new ManualEntryImportRow
            {
                EmployeeId = employeeId!.Value,
                Timestamp = date!.Value.ToDateTime(time!.Value),
                PunchType = punchType!.Value,
                Reason = reason!,
                EnteredBy = enteredBy,
            });
        }

        if (errors.Count > 0)
            throw new ManualEntryImportException(errors);

        return rows;
    }

    /// <summary>Maps each non-blank header in row 1 to its column index, matched
    /// case-insensitively with whitespace trimmed. The first column wins when a
    /// header name is repeated -- later duplicate columns are silently ignored
    /// rather than treated as a second, competing source for the same field. Same
    /// implementation as EmployeeRosterImporter.BuildHeaderMap.</summary>
    private static Dictionary<string, int> BuildHeaderMap(ExcelWorksheet ws, int lastCol)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var col = 1; col <= lastCol; col++)
        {
            var header = ws.Cells[1, col].Text.Trim();
            if (string.IsNullOrEmpty(header) || map.ContainsKey(header))
                continue;

            map[header] = col;
        }

        return map;
    }

    private static int? Col(Dictionary<string, int> headerMap, string header)
        => headerMap.TryGetValue(header, out var col) ? col : null;

    private static bool IsRowBlank(ExcelWorksheet ws, int row, int lastCol)
    {
        for (var col = 1; col <= lastCol; col++)
            if (ws.Cells[row, col].Value is not null) return false;

        return true;
    }

    /// <summary>Reads the Id column and resolves it against the employee roster --
    /// unlike EmployeeRosterImporter's ReadRequiredInt (which only checks that the
    /// cell parses as a whole number), this also has to confirm the number
    /// actually belongs to someone: a manual entry's employee has to already
    /// exist, it's never created by this import the way EmployeeRosterImporter
    /// creates new employees.</summary>
    private static int? ReadRequiredEmployeeId(ExcelWorksheet ws, int row, int col, int rowNumber,
        HashSet<int> validPins, List<string> errors)
    {
        var value = ws.Cells[row, col].Value;
        if (value is null)
        {
            errors.Add($"Row {rowNumber}: Id is required.");
            return null;
        }

        int id;
        try
        {
            id = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            errors.Add($"Row {rowNumber}: Id value \"{ws.Cells[row, col].Text}\" is not a valid whole number.");
            return null;
        }

        if (!validPins.Contains(id))
        {
            errors.Add($"Row {rowNumber}: Id {id} does not match any employee.");
            return null;
        }

        return id;
    }

    /// <summary>A real Excel date cell (what ExportManualLogsToExcel writes) comes
    /// back through GetValue&lt;DateTime&gt; the same way ExcelScheduleImporter
    /// already reads StartDate/EndDate. Text typed straight into the cell (e.g.
    /// "8/23/2026") won't convert that way, so this falls back to parsing .Text
    /// directly for anyone building an import file by hand rather than starting
    /// from an export.</summary>
    private static DateOnly? ReadRequiredDate(ExcelWorksheet ws, int row, int col, string fieldName, int rowNumber, List<string> errors)
    {
        var value = ws.Cells[row, col].Value;
        var text = ws.Cells[row, col].Text.Trim();
        if (value is null && text.Length == 0)
        {
            errors.Add($"Row {rowNumber}: {fieldName} is required.");
            return null;
        }

        try
        {
            return DateOnly.FromDateTime(ws.Cells[row, col].GetValue<DateTime>());
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            errors.Add($"Row {rowNumber}: {fieldName} value \"{text}\" is not a valid date.");
            return null;
        }
    }

    /// <summary>Same GetValue-then-text-fallback shape as ReadRequiredDate above --
    /// a real Excel time cell (what ExportManualLogsToExcel writes as
    /// Timestamp.TimeOfDay.TotalDays) reads through GetValue&lt;DateTime&gt;.
    /// TimeOfDay; free-typed text (e.g. "5:01 PM") falls back to a direct
    /// parse.</summary>
    private static TimeOnly? ReadRequiredTime(ExcelWorksheet ws, int row, int col, string fieldName, int rowNumber, List<string> errors)
    {
        var value = ws.Cells[row, col].Value;
        var text = ws.Cells[row, col].Text.Trim();
        if (value is null && text.Length == 0)
        {
            errors.Add($"Row {rowNumber}: {fieldName} is required.");
            return null;
        }

        try
        {
            return TimeOnly.FromTimeSpan(ws.Cells[row, col].GetValue<DateTime>().TimeOfDay);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            errors.Add($"Row {rowNumber}: {fieldName} value \"{text}\" is not a valid time.");
            return null;
        }
    }

    /// <summary>Matches the same "Clock In"/"Clock Out" text
    /// ExportManualLogsToExcel writes via PunchTypeLabel.ToText -- a manual entry
    /// can only ever carry one of these two (see ManualEntryImportRow.PunchType's
    /// own doc comment), so the wider device-only status set PunchTypeLabel also
    /// knows about (Break In/Out, Overtime In/Out) is deliberately rejected here,
    /// same restriction ManualLogEntryDialog's PunchTypeCombo already enforces
    /// with just two items.</summary>
    private static int? ReadRequiredPunchType(ExcelWorksheet ws, int row, int col, int rowNumber, List<string> errors)
    {
        var text = ws.Cells[row, col].Text.Trim();
        if (text.Length == 0)
        {
            errors.Add($"Row {rowNumber}: Punch Type is required.");
            return null;
        }

        if (text.Equals("Clock In", StringComparison.OrdinalIgnoreCase)) return 0;
        if (text.Equals("Clock Out", StringComparison.OrdinalIgnoreCase)) return 1;

        errors.Add($"Row {rowNumber}: Punch Type value \"{text}\" is not \"Clock In\" or \"Clock Out\".");
        return null;
    }

    /// <summary>Same implementation as EmployeeRosterImporter.ReadRequiredText.</summary>
    private static string? ReadRequiredText(ExcelWorksheet ws, int row, int col, string fieldName, int rowNumber, List<string> errors)
    {
        var text = ws.Cells[row, col].Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            errors.Add($"Row {rowNumber}: {fieldName} is required.");
            return null;
        }

        return text;
    }

    /// <summary>Same implementation as EmployeeRosterImporter.ReadOptionalText.</summary>
    private static string? ReadOptionalText(ExcelWorksheet ws, int row, int? col)
    {
        if (col is not int c) return null;

        var text = ws.Cells[row, c].Text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
