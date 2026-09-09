using System.Drawing;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Payroll;

namespace ScheduleApp.Excel;

/// <summary>
/// Writes the Payroll tab's roster export: one row per employee's PayrollResult
/// for a period, sorted Department then Last Name then First Name, with a Grand
/// Total row underneath. Every payment/deduction figure is written as the literal
/// value PayrollCalculator already computed, not a live formula; only the Grand
/// Total row uses SUM() across the data rows above it, the same "literal rows,
/// formula totals" split PayrollSummaryView itself follows on screen.
/// </summary>
public static class PayrollExcelExporter
{
    /// <summary>Plain thousands-separated number format, no currency symbol -- e.g.
    /// "1,234.56", "-1,234.56". Used for every payment/deduction/Net Pay column;
    /// Excel's own default negative-number rendering (a leading minus) is enough
    /// on its own now that there's no symbol to place around it, unlike the old
    /// two-section peso format this replaced, which needed a second section only
    /// to keep the sign in front of a "₱" prefix.</summary>
    private const string AmountFormat = "#,##0.00";

    private const string HoursFormat = "0.00";
    private const string IntegerFormat = "0";

    private static readonly Color RoseColor = Color.FromArgb(1, 243, 58, 106);
    private static readonly Color LightPinkColor = Color.FromArgb(1, 255, 182, 193);
    private static readonly Color NegativeRed = Color.FromArgb(1, 192, 0, 0);

    /// <summary>
    /// Exports one employee per row for the given period. Employees are matched to
    /// results by Employee.Pin, not Employee.Id — PayrollResult.EmployeeId is
    /// populated from employee.Pin in PayrollCalculator.Calculate, the same
    /// convention AttendanceExcelExporter's own log/employee join already follows.
    /// </summary>
    /// <param name="filePath">Destination .xlsx path; overwritten if it already exists.</param>
    /// <param name="results">One PayrollResult per employee in scope, e.g. straight off
    /// PayrollViewModel's own batch ComputeOneAsync loop.</param>
    /// <param name="employees">Every employee the results might reference, for the Last
    /// Name/First Name/Department columns — PayrollResult itself only carries
    /// EmployeeId and the combined DisplayName, not these split out.</param>
    /// <param name="periodStart">Inclusive; used only for the worksheet name.</param>
    /// <param name="periodEnd">Inclusive; used only for the worksheet name.</param>
    public static void ExportRosterToExcel(
        string filePath,
        IReadOnlyList<PayrollResult> results,
        IReadOnlyList<Employee> employees,
        DateOnly periodStart,
        DateOnly periodEnd)
    {
        ExcelLicense.EnsureConfigured();

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists)
            fileInfo.Delete();

        using var package = new ExcelPackage(fileInfo);

        var sheetName = ExcelSheetNaming.SanitizeSheetName($"Salary {periodStart:MM.dd}-{periodEnd:MM.dd}");
        var worksheet = package.Workbook.Worksheets.Add(sheetName);
        worksheet.Cells.Style.Font.Size = 12;
        worksheet.View.ShowGridLines = false;

        WriteHeaders(worksheet);
        ApplyColumnFormats(worksheet);

        var rows = results
            .GroupJoin(
                employees,
                r => r.EmployeeId,
                e => e.Pin,
                (result, emps) => (Result: result, Employee: emps.FirstOrDefault()))
            .OrderBy(x => x.Employee?.Department?.Name)
            .ThenBy(x => x.Employee?.LastName)
            .ThenBy(x => x.Employee?.FirstName);

        int row = 2;
        foreach (var (result, employee) in rows)
        {
            WriteRow(worksheet, row, result, employee);
            row++;
        }

        int lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            WriteGrandTotalRow(worksheet, row, lastDataRow);
            row++;
        }

        worksheet.View.FreezePanes(2, 1);
        if (worksheet.Dimension is not null)
            worksheet.Cells[worksheet.Dimension.Address].AutoFilter = true;

        package.Save();
    }

    private static void WriteHeaders(ExcelWorksheet ws)
    {
        ws.Cells[1, PayrollColumns.EmployeeId].Value = "Employee ID";
        ws.Cells[1, PayrollColumns.LastName].Value = "Last Name";
        ws.Cells[1, PayrollColumns.FirstName].Value = "First Name";
        ws.Cells[1, PayrollColumns.Department].Value = "Department";
        ws.Cells[1, PayrollColumns.WorkDays].Value = "Work Days";
        ws.Cells[1, PayrollColumns.BasicPay].Value = "Basic Pay";
        ws.Cells[1, PayrollColumns.OvertimeHours].Value = "Overtime Hours";
        ws.Cells[1, PayrollColumns.Overtime].Value = "Overtime";
        ws.Cells[1, PayrollColumns.NightDiffHours].Value = "Night Diff Hours";
        ws.Cells[1, PayrollColumns.NightDiff].Value = "Night Diff";
        ws.Cells[1, PayrollColumns.RestDayHours].Value = "Rest Day Hours";
        ws.Cells[1, PayrollColumns.RestDayPay].Value = "Rest Day Pay";
        ws.Cells[1, PayrollColumns.PremiumPay].Value = "Premium Pay";
        ws.Cells[1, PayrollColumns.Allowance].Value = "Allowance";
        ws.Cells[1, PayrollColumns.Incentive].Value = "Incentive";
        ws.Cells[1, PayrollColumns.TotalGrossPay].Value = "Total Gross Pay";
        ws.Cells[1, PayrollColumns.UndertimeHours].Value = "Undertime Hours";
        ws.Cells[1, PayrollColumns.Undertime].Value = "Undertime";
        ws.Cells[1, PayrollColumns.UndertimeWaived].Value = "Undertime Waived";
        ws.Cells[1, PayrollColumns.Sss].Value = "SSS";
        ws.Cells[1, PayrollColumns.PhilHealth].Value = "PhilHealth";
        ws.Cells[1, PayrollColumns.PagIbig].Value = "Pag-IBIG";
        ws.Cells[1, PayrollColumns.CashAdvance].Value = "Cash Advance";
        ws.Cells[1, PayrollColumns.Charges].Value = "Charges";
        ws.Cells[1, PayrollColumns.TotalDeductions].Value = "Total Deductions";
        ws.Cells[1, PayrollColumns.NetPay].Value = "Net Pay";

        using var headerRange = ws.Cells[1, PayrollColumns.EmployeeId, 1, PayrollColumns.NetPay];
        headerRange.Style.Font.Size = 12;
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
        headerRange.Style.Fill.BackgroundColor.SetColor(RoseColor);
        headerRange.Style.Font.Color.SetColor(Color.White);
        headerRange.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        headerRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
        headerRange.Style.Border.Left.Color.SetColor(LightPinkColor);
        headerRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        headerRange.Style.Border.Right.Color.SetColor(LightPinkColor);
    }

    /// <summary>Width, number format, alignment, and whether Total Gross Pay/Total
    /// Deductions/Net Pay's bold carries down into every data row, not just the
    /// header (WriteHeaders already bolds the header separately).</summary>
    private static void ApplyColumnFormats(ExcelWorksheet ws)
    {
        (int Col, double Width, string? Format, ExcelHorizontalAlignment Align, bool Bold)[] columns =
        [
            (PayrollColumns.EmployeeId, 10, IntegerFormat, ExcelHorizontalAlignment.Center, false),
            (PayrollColumns.LastName, 18, null, ExcelHorizontalAlignment.Left, false),
            (PayrollColumns.FirstName, 16, null, ExcelHorizontalAlignment.Left, false),
            (PayrollColumns.Department, 18, null, ExcelHorizontalAlignment.Left, false),
            (PayrollColumns.WorkDays, 10, IntegerFormat, ExcelHorizontalAlignment.Center, false),
            (PayrollColumns.BasicPay, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.OvertimeHours, 11, HoursFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.Overtime, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.NightDiffHours, 11, HoursFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.NightDiff, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.RestDayHours, 11, HoursFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.RestDayPay, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.PremiumPay, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.Allowance, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.Incentive, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.TotalGrossPay, 14, AmountFormat, ExcelHorizontalAlignment.Right, true),
            (PayrollColumns.UndertimeHours, 11, HoursFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.Undertime, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.UndertimeWaived, 12, null, ExcelHorizontalAlignment.Center, false),
            (PayrollColumns.Sss, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.PhilHealth, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.PagIbig, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.CashAdvance, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.Charges, 13, AmountFormat, ExcelHorizontalAlignment.Right, false),
            (PayrollColumns.TotalDeductions, 14, AmountFormat, ExcelHorizontalAlignment.Right, true),
            (PayrollColumns.NetPay, 14, AmountFormat, ExcelHorizontalAlignment.Right, true),
        ];

        foreach (var (col, width, format, align, bold) in columns)
        {
            var c = ws.Column(col);
            c.Width = width;
            c.Style.HorizontalAlignment = align;
            if (format is not null)
                c.Style.Numberformat.Format = format;
            if (bold)
                c.Style.Font.Bold = true;
        }
    }

    /// <summary>One data row. employee is null only if results somehow references an
    /// EmployeeId (Pin) not present in the employees list handed in — shouldn't happen
    /// given both come from the same batch scope, but falls back the same way
    /// AttendanceExcelExporter's own log/employee join does rather than throwing.</summary>
    private static void WriteRow(ExcelWorksheet ws, int row, PayrollResult result, Employee? employee)
    {
        ws.Cells[row, PayrollColumns.EmployeeId].Value = result.EmployeeId;
        ws.Cells[row, PayrollColumns.LastName].Value = employee?.LastName ?? "Unknown";
        ws.Cells[row, PayrollColumns.FirstName].Value = employee?.FirstName ?? string.Empty;
        ws.Cells[row, PayrollColumns.Department].Value = employee?.Department?.Name ?? "(Unassigned)";

        ws.Cells[row, PayrollColumns.WorkDays].Value = result.WorkDays;
        ws.Cells[row, PayrollColumns.BasicPay].Value = result.ComputedGrossPay[0].Amount;
        ws.Cells[row, PayrollColumns.OvertimeHours].Value = result.OvertimeHours;
        ws.Cells[row, PayrollColumns.Overtime].Value = result.ComputedGrossPay[1].Amount;
        ws.Cells[row, PayrollColumns.NightDiffHours].Value = result.NightDiffHours;
        ws.Cells[row, PayrollColumns.NightDiff].Value = result.ComputedGrossPay[2].Amount;
        ws.Cells[row, PayrollColumns.RestDayHours].Value = result.RestDayHours;
        ws.Cells[row, PayrollColumns.RestDayPay].Value = result.RestDayPayAmount;

        ws.Cells[row, PayrollColumns.PremiumPay].Value =
            Subtotal(result.GrossPayAdjustmentGroups, PayrollAdjustmentType.PremiumHoliday);
        ws.Cells[row, PayrollColumns.Allowance].Value =
            Subtotal(result.GrossPayAdjustmentGroups, PayrollAdjustmentType.Allowance);
        ws.Cells[row, PayrollColumns.Incentive].Value =
            Subtotal(result.GrossPayAdjustmentGroups, PayrollAdjustmentType.Incentive);
        ws.Cells[row, PayrollColumns.TotalGrossPay].Value = result.TotalGrossPay;

        ws.Cells[row, PayrollColumns.UndertimeHours].Value = result.UndertimeHours;
        // UndertimePayAmount/UndertimeWaived, not ComputedDeductions[0] -- that line
        // is omitted entirely for an Employee.ExemptFromUndertimeDeduction employee
        // (see PayrollCalculator.Calculate), which would throw here otherwise; see
        // those two properties' own doc comments for why they carry the same figures
        // regardless of whether the line itself is present.
        ws.Cells[row, PayrollColumns.Undertime].Value = result.UndertimePayAmount;
        ws.Cells[row, PayrollColumns.UndertimeWaived].Value = result.UndertimeWaived ? "Yes" : "No";

        ws.Cells[row, PayrollColumns.Sss].Value =
            Subtotal(result.DeductionAdjustmentGroups, PayrollAdjustmentType.SSS);
        ws.Cells[row, PayrollColumns.PhilHealth].Value =
            Subtotal(result.DeductionAdjustmentGroups, PayrollAdjustmentType.PhilHealth);
        ws.Cells[row, PayrollColumns.PagIbig].Value =
            Subtotal(result.DeductionAdjustmentGroups, PayrollAdjustmentType.PagIbig);
        ws.Cells[row, PayrollColumns.CashAdvance].Value =
            Subtotal(result.DeductionAdjustmentGroups, PayrollAdjustmentType.CashAdvance);
        ws.Cells[row, PayrollColumns.Charges].Value =
            Subtotal(result.DeductionAdjustmentGroups, PayrollAdjustmentType.Charge);
        ws.Cells[row, PayrollColumns.TotalDeductions].Value = result.TotalDeductions;

        ws.Cells[row, PayrollColumns.NetPay].Value = result.NetPay;
        if (result.NetPay < 0)
            ws.Cells[row, PayrollColumns.NetPay].Style.Font.Color.SetColor(NegativeRed);
    }

    /// <summary>Looks a group up by its Type rather than assuming position within the
    /// list, so this stays correct even if PayrollAdjustmentType's declaration order
    /// (which PayrollCalculator.Calculate currently mirrors 1:1 into list order) ever
    /// changes. FirstOrDefault, not First -- found while confirming Pay Eligibility
    /// Flags plan Phase 6: PremiumHoliday's group is omitted from
    /// result.GrossPayAdjustmentGroups entirely (not left present with an empty/zero
    /// Subtotal) for an employee with Employee.QualifiesForPremiumPay == false, the
    /// default for every employee since that plan's migration -- First would throw
    /// "Sequence contains no matching element" for every such employee the moment an
    /// export ran. 0m for a type whose group is absent reads the same as any other
    /// type's genuinely-zero Subtotal, which is the correct Excel figure either
    /// way -- nothing was earned/withheld under that type this period.</summary>
    private static decimal Subtotal(IReadOnlyList<PayrollAdjustmentGroup> groups, PayrollAdjustmentType type) =>
        groups.FirstOrDefault(g => g.Type == type)?.Subtotal ?? 0m;

    /// <summary>SUM() across every numeric column, including the five hours/days
    /// columns, not just the amount ones — a roster's grand total is more useful
    /// with total OT/ND/Undertime hours included alongside the amount figures.
    /// Undertime Waived (Yes/No, non-numeric) is skipped, the same way
    /// AttendanceExcelExporter's own totals skip its Status column.</summary>
    private static void WriteGrandTotalRow(ExcelWorksheet ws, int totalRow, int lastDataRow)
    {
        const int firstDataRow = 2;

        ws.Cells[totalRow, PayrollColumns.EmployeeId].Value = "GRAND TOTAL";
        ws.Cells[totalRow, PayrollColumns.EmployeeId, totalRow, PayrollColumns.Department].Merge = true;

        SumColumn(ws, totalRow, PayrollColumns.WorkDays, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.BasicPay, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.OvertimeHours, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.Overtime, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.NightDiffHours, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.NightDiff, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.RestDayHours, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.RestDayPay, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.PremiumPay, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.Allowance, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.Incentive, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.TotalGrossPay, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.UndertimeHours, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.Undertime, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.Sss, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.PhilHealth, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.PagIbig, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.CashAdvance, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.Charges, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.TotalDeductions, firstDataRow, lastDataRow);
        SumColumn(ws, totalRow, PayrollColumns.NetPay, firstDataRow, lastDataRow);

        using var totalRange = ws.Cells[totalRow, PayrollColumns.EmployeeId, totalRow, PayrollColumns.NetPay];
        totalRange.Style.Font.Bold = true;
        totalRange.Style.Font.Color.SetColor(Color.White);
        totalRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
        totalRange.Style.Fill.BackgroundColor.SetColor(RoseColor);
        totalRange.Style.Border.Top.Style = ExcelBorderStyle.Medium;
        totalRange.Style.Border.Top.Color.SetColor(RoseColor);
        totalRange.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
        totalRange.Style.Border.Bottom.Color.SetColor(RoseColor);
    }

    private static void SumColumn(ExcelWorksheet ws, int totalRow, int col, int startRow, int endRow)
    {
        string colLetter = ExcelCellAddress.GetColumnLetter(col);
        ws.Cells[totalRow, col].Formula = $"SUM({colLetter}{startRow}:{colLetter}{endRow})";
    }

    /// <summary>Fixed column indices for the roster layout (A through Z). Unlike
    /// AttendanceExcelExporter's SummaryColumns, there is no conditional offset here;
    /// Department is always present, so every index is a plain constant. RestDayHours/
    /// RestDayPay sit right after NightDiff/NightDiffHours, matching ComputedGrossPay's
    /// own fixed "Basic Pay, Overtime, Night Diff, Rest Day Pay" display order (see
    /// PayrollResult.ComputedGrossPay's doc comment) rather than being tacked on at
    /// the end.</summary>
    private static class PayrollColumns
    {
        public const int EmployeeId = 1;
        public const int LastName = 2;
        public const int FirstName = 3;
        public const int Department = 4;
        public const int WorkDays = 5;
        public const int BasicPay = 6;
        public const int OvertimeHours = 7;
        public const int Overtime = 8;
        public const int NightDiffHours = 9;
        public const int NightDiff = 10;
        public const int RestDayHours = 11;
        public const int RestDayPay = 12;
        public const int PremiumPay = 13;
        public const int Allowance = 14;
        public const int Incentive = 15;
        public const int TotalGrossPay = 16;
        public const int UndertimeHours = 17;
        public const int Undertime = 18;
        public const int UndertimeWaived = 19;
        public const int Sss = 20;
        public const int PhilHealth = 21;
        public const int PagIbig = 22;
        public const int CashAdvance = 23;
        public const int Charges = 24;
        public const int TotalDeductions = 25;
        public const int NetPay = 26;
    }
}
