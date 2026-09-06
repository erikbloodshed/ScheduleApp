using System.Globalization;
using OfficeOpenXml;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Reads a roster workbook like Employees.xlsx: a single sheet (any name,
/// preferring one literally named "Employees" if there's more than one) whose
/// first row names each column by header text, so columns can appear in any
/// order and optional ones can be left out entirely. Recognized headers (matched
/// case-insensitively, leading/trailing whitespace trimmed):
///
///   Required: EmployeeId, LastName, FirstName
///   Optional: Department, DailyRate, IsOvertimeEligible, HasOvertimePremium,
///             HasNightDiff, SSS, PhilHealth, PagIBIG, HasLeaveWithPay
///
/// Any other column is ignored. A data row that's entirely blank is skipped
/// rather than reported as an error, so a stray blank row before the sheet's
/// real end doesn't block an otherwise-clean import.
///
/// Import validates the whole sheet before returning anything: a missing
/// required column, or any data row with a blank required field, a duplicate
/// EmployeeId, or an optional value that can't be parsed as its expected type,
/// adds to a running list of problems rather than stopping at the first one.
/// If that list ends up non-empty, Import throws EmployeeImportException
/// carrying all of them -- so a person fixing a large workbook sees every
/// problem in one pass instead of re-importing repeatedly to find them one at
/// a time.
/// </summary>
public static class EmployeeRosterImporter
{
    private const string EmployeeIdHeader = "EmployeeId";
    private const string LastNameHeader = "LastName";
    private const string FirstNameHeader = "FirstName";
    private const string DepartmentHeader = "Department";
    private const string DailyRateHeader = "DailyRate";
    private const string IsOvertimeEligibleHeader = "IsOvertimeEligible";
    private const string HasOvertimePremiumHeader = "HasOvertimePremium";
    private const string HasNightDiffHeader = "HasNightDiff";
    private const string SssHeader = "SSS";
    private const string PhilHealthHeader = "PhilHealth";
    private const string PagIbigHeader = "PagIBIG";
    private const string HasLeaveWithPayHeader = "HasLeaveWithPay";

    public static List<EmployeeImportRow> Import(string filePath)
    {
        ExcelLicense.EnsureConfigured();

        using var package = new ExcelPackage(new FileInfo(filePath));
        var ws = package.Workbook.Worksheets
            .FirstOrDefault(w => w.Name.Equals("Employees", StringComparison.OrdinalIgnoreCase))
            ?? package.Workbook.Worksheets.First();

        var lastCol = ws.Dimension?.End.Column ?? 0;
        var headerMap = BuildHeaderMap(ws, lastCol);

        var missingRequired = new[] { EmployeeIdHeader, LastNameHeader, FirstNameHeader }
            .Where(header => !headerMap.ContainsKey(header))
            .ToList();
        if (missingRequired.Count > 0)
        {
            throw new EmployeeImportException(new[]
            {
                $"The workbook is missing required column(s): {string.Join(", ", missingRequired)}."
            });
        }

        var employeeIdCol = headerMap[EmployeeIdHeader];
        var lastNameCol = headerMap[LastNameHeader];
        var firstNameCol = headerMap[FirstNameHeader];
        var departmentCol = Col(headerMap, DepartmentHeader);
        var dailyRateCol = Col(headerMap, DailyRateHeader);
        var isOvertimeEligibleCol = Col(headerMap, IsOvertimeEligibleHeader);
        var hasOvertimePremiumCol = Col(headerMap, HasOvertimePremiumHeader);
        var hasNightDiffCol = Col(headerMap, HasNightDiffHeader);
        var sssCol = Col(headerMap, SssHeader);
        var philHealthCol = Col(headerMap, PhilHealthHeader);
        var pagIbigCol = Col(headerMap, PagIbigHeader);
        var hasLeaveWithPayCol = Col(headerMap, HasLeaveWithPayHeader);

        var rows = new List<EmployeeImportRow>();
        var errors = new List<string>();

        // Tracks which row an EmployeeId was first seen on, so a value reused by a
        // later row is reported as a validation error here instead of surfacing as
        // an opaque unique-index SQL failure once ImportEmployeeRosterAsync tries to
        // save two different new employees with the same Pin.
        var firstRowForPin = new Dictionary<int, int>();

        var lastRow = ws.Dimension?.End.Row ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            if (IsRowBlank(ws, row, lastCol)) continue;

            var rowErrors = new List<string>();

            var pin = ReadRequiredInt(ws, row, employeeIdCol, "EmployeeId", row, rowErrors);
            var lastName = ReadRequiredText(ws, row, lastNameCol, "LastName", row, rowErrors);
            var firstName = ReadRequiredText(ws, row, firstNameCol, "FirstName", row, rowErrors);

            if (pin is int pinValue)
            {
                if (firstRowForPin.TryGetValue(pinValue, out var firstRow))
                    rowErrors.Add($"Row {row}: EmployeeId {pinValue} is also used by row {firstRow}. Employee IDs must be unique within the file.");
                else
                    firstRowForPin[pinValue] = row;
            }

            var departmentName = ReadOptionalText(ws, row, departmentCol);
            var dailyRate = ReadOptionalDecimal(ws, row, dailyRateCol, "DailyRate", row, rowErrors);
            var isOvertimeEligible = ReadOptionalBool(ws, row, isOvertimeEligibleCol, "IsOvertimeEligible", row, rowErrors);
            var hasOvertimePremium = ReadOptionalBool(ws, row, hasOvertimePremiumCol, "HasOvertimePremium", row, rowErrors);
            var hasNightDiff = ReadOptionalBool(ws, row, hasNightDiffCol, "HasNightDiff", row, rowErrors);
            var sss = ReadOptionalDecimal(ws, row, sssCol, "SSS", row, rowErrors);
            var philHealth = ReadOptionalDecimal(ws, row, philHealthCol, "PhilHealth", row, rowErrors);
            var pagIbig = ReadOptionalDecimal(ws, row, pagIbigCol, "PagIBIG", row, rowErrors);
            var hasLeaveWithPay = ReadOptionalBool(ws, row, hasLeaveWithPayCol, "HasLeaveWithPay", row, rowErrors);

            if (rowErrors.Count > 0)
            {
                errors.AddRange(rowErrors);
                continue;
            }

            rows.Add(new EmployeeImportRow
            {
                Pin = pin!.Value,
                LastName = lastName!,
                FirstName = firstName!,
                DepartmentName = departmentName,
                DailyRate = dailyRate,
                QualifiesForOvertime = isOvertimeEligible,
                ApplyOvertimeRatePercentageByDefault = hasOvertimePremium,
                QualifiesForNightDiff = hasNightDiff,
                DefaultSss = sss,
                DefaultPhilHealth = philHealth,
                DefaultPagIbig = pagIbig,
                DefaultLeaveIsPaid = hasLeaveWithPay
            });
        }

        if (errors.Count > 0)
            throw new EmployeeImportException(errors);

        return rows;
    }

    /// <summary>Maps each non-blank header in row 1 to its column index, matched
    /// case-insensitively with whitespace trimmed. The first column wins when a
    /// header name is repeated -- later duplicate columns are silently ignored
    /// rather than treated as a second, competing source for the same field.</summary>
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

    private static int? Col(IReadOnlyDictionary<string, int> headerMap, string header)
        => headerMap.TryGetValue(header, out var col) ? col : null;

    private static bool IsRowBlank(ExcelWorksheet ws, int row, int lastCol)
    {
        for (var col = 1; col <= lastCol; col++)
            if (ws.Cells[row, col].Value is not null) return false;

        return true;
    }

    private static int? ReadRequiredInt(ExcelWorksheet ws, int row, int col, string fieldName, int rowNumber, List<string> errors)
    {
        var value = ws.Cells[row, col].Value;
        if (value is null)
        {
            errors.Add($"Row {rowNumber}: {fieldName} is required.");
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            errors.Add($"Row {rowNumber}: {fieldName} value \"{ws.Cells[row, col].Text}\" is not a valid whole number.");
            return null;
        }
    }

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

    private static string? ReadOptionalText(ExcelWorksheet ws, int row, int? col)
    {
        if (col is not int c) return null;

        var text = ws.Cells[row, c].Text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Null when the column doesn't exist in this workbook or the cell is
    /// blank on this row -- see EmployeeImportRow's own doc comment for what that
    /// then means to ImportEmployeeRosterAsync. Rejects a negative value the same
    /// way EmployeeDialog's own money-field validation does (DailyRate/SSS/
    /// PhilHealth/Pag-IBIG are all "0 or greater" there too).</summary>
    private static decimal? ReadOptionalDecimal(ExcelWorksheet ws, int row, int? col, string fieldName, int rowNumber, List<string> errors)
    {
        if (col is not int c) return null;

        var value = ws.Cells[row, c].Value;
        if (value is null) return null;

        decimal parsed;
        try
        {
            parsed = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            errors.Add($"Row {rowNumber}: {fieldName} value \"{ws.Cells[row, c].Text}\" is not a valid number.");
            return null;
        }

        if (parsed < 0)
        {
            errors.Add($"Row {rowNumber}: {fieldName} cannot be negative.");
            return null;
        }

        return parsed;
    }

    /// <summary>Null when the column doesn't exist in this workbook or the cell is
    /// blank on this row. Accepts a real Excel boolean cell directly, plus the usual
    /// text spellings (TRUE/FALSE, Yes/No, 1/0) for a workbook where the column was
    /// typed or pasted in as text instead.</summary>
    private static bool? ReadOptionalBool(ExcelWorksheet ws, int row, int? col, string fieldName, int rowNumber, List<string> errors)
    {
        if (col is not int c) return null;

        var value = ws.Cells[row, c].Value;
        if (value is null) return null;
        if (value is bool b) return b;

        var text = ws.Cells[row, c].Text.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (bool.TryParse(text, out var parsedBool)) return parsedBool;
        if (text == "1") return true;
        if (text == "0") return false;
        if (text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;

        errors.Add($"Row {rowNumber}: {fieldName} value \"{text}\" is not a valid Yes/No value.");
        return null;
    }
}
