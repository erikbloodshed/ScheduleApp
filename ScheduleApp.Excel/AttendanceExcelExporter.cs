using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Writes the Attendance tab's two report kinds: a per-employee summary
/// (ExportSummaryToExcel) and a raw punch log (ExportLogsToExcel).
///
/// WriteRows always writes exactly one row per AttendanceSummary -- there's
/// no per-segment row expansion here. A SplitShift day already arrives as
/// multiple AttendanceSummary rows, one per segment (see
/// SplitShiftCalculationStrategy), so the expansion has already happened
/// upstream by the time WriteRows sees them.
/// </summary>
public static class AttendanceExcelExporter
{
    private const string NumericFormat = "_* #,##0.00_-;_* (#,##0.00);;@";
    private const string TimeFormat1 = "h:mm AM/PM";
    private const string TimeFormat2 = "[h]:mm;-[h]:mm;;@";
    private const string DateFormat = "m/dd/yy ddd";

    private static readonly Color RoseColor = Color.FromArgb(1, 243, 58, 106);
    private static readonly Color LightPinkColor = Color.FromArgb(1, 255, 182, 193);
    private static readonly Color CoralPink = Color.FromArgb(1, 255, 127, 80);
    private static readonly Color RosyBrown = Color.FromArgb(1, 175, 143, 126);
    private static readonly Color OfficialBusinessPurple = Color.FromArgb(1, 124, 58, 237);
    private static readonly Color RestDayTeal = Color.FromArgb(1, 13, 148, 136);

    private static double ToExcelTime(TimeSpan ts) => ts.TotalHours / 24.0;

    private static double ToExcelTime(TimeOnly to) => ToExcelTime(to.ToTimeSpan());

    /// <summary>
    /// Exports the raw punch log. Employees are matched to logs by
    /// Employee.Pin (the punch clock's own employee code), not
    /// Employee.Id -- see AttendanceWorkflowService for why.
    ///
    /// sheetName defaults to "Punch Logs" (the Punch Records tab's own export),
    /// but PunchListDetailDialog's Export button passes "Orphaned" or
    /// "Unscheduled" instead, so the workbook's tab matches whichever subset
    /// of punches was actually exported rather than always reading "Punch
    /// Logs" regardless of content.
    /// </summary>
    public static void ExportLogsToExcel(
        string filePath,
        IEnumerable<AttendanceLog> logs,
        IEnumerable<Employee> employees,
        string sheetName = "Punch Logs"
    ) => WriteLogsToExcel(filePath, sheetName, logs, employees, includeManualColumns: false);

    /// <summary>
    /// Exports manual entries (ManualAttendanceLog) for the Manual Entries tab. Kept as
    /// its own public method rather than accepting ManualAttendanceLog in
    /// ExportLogsToExcel, since Reason and EnteredBy only exist on a manual entry and
    /// are the whole point of this export (see ManualAttendanceLog's doc comment) -- a
    /// real device punch export has no equivalent columns to show. Internally this just
    /// projects each entry through ManualAttendanceLog.ToAttendanceLog() (which already
    /// carries Reason/EnteredBy across) and shares WriteLogsToExcel with the device-punch
    /// export below.
    /// </summary>
    public static void ExportManualLogsToExcel(
        string filePath,
        IEnumerable<ManualAttendanceLog> logs,
        IEnumerable<Employee> employees
    ) => WriteLogsToExcel(filePath, "Manual Entries", logs.Select(l => l.ToAttendanceLog()), employees, includeManualColumns: true);

    /// <summary>
    /// Shared writer behind ExportLogsToExcel/ExportManualLogsToExcel -- the two differ
    /// only in sheet name and whether the Reason/Entered By columns are present, mirroring
    /// how SummaryColumns.IncludeDepartment drives the one summary-sheet layout below.
    /// </summary>
    private static void WriteLogsToExcel(
        string filePath,
        string sheetName,
        IEnumerable<AttendanceLog> logs,
        IEnumerable<Employee> employees,
        bool includeManualColumns
    )
    {
        ExcelLicense.EnsureConfigured();

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists)
            fileInfo.Delete();

        using var package = new ExcelPackage(fileInfo);
        var worksheet = package.Workbook.Worksheets.Add(sheetName);
        worksheet.Cells.Style.Font.Size = 12;

        string[] headers = includeManualColumns
            ? ["Id", "Employee Name", "Department", "Date", "Time", "Punch Type", "Reason", "Entered By"]
            : ["Id", "Employee Name", "Department", "Date", "Time", "Punch Type"];
        WriteLogHeaders(worksheet, headers);
        ApplyLogColumnFormats(worksheet, includeManualColumns);

        var logData = logs.GroupJoin(
                employees,
                log => log.EmployeeId,
                emp => emp.Pin,
                (log, emps) => new { Log = log, Employee = emps.FirstOrDefault() }
            )
            .OrderBy(x => x.Employee?.Department?.Name)
            .ThenBy(x => x.Log.EmployeeId)
            .ThenBy(x => x.Log.Timestamp);

        int row = 2;
        foreach (var item in logData)
        {
            worksheet.Cells[row, 1].Value = item.Log.EmployeeId;
            worksheet.Cells[row, 2].Value = item.Employee?.DisplayName ?? "Unknown";
            worksheet.Cells[row, 3].Value = item.Employee?.Department?.Name ?? "(Unassigned)";
            worksheet.Cells[row, 4].Value = item.Log.Timestamp.Date;
            worksheet.Cells[row, 5].Value = item.Log.Timestamp.TimeOfDay.TotalDays;
            worksheet.Cells[row, 6].Value = PunchTypeLabel.ToText(item.Log.PunchType);
            if (includeManualColumns)
            {
                worksheet.Cells[row, 7].Value = item.Log.Reason;
                worksheet.Cells[row, 8].Value = item.Log.EnteredBy;
            }
            row++;
        }

        worksheet.View.FreezePanes(2, 1);
        worksheet.Cells[worksheet.Dimension.Address].AutoFilter = true;
        package.Save();
    }

    private static void WriteLogHeaders(ExcelWorksheet ws, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
            ws.Cells[1, i + 1].Value = headers[i];

        using var range = ws.Cells[1, 1, 1, headers.Length];
        range.Style.Font.Bold = true;
        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
        range.Style.Fill.BackgroundColor.SetColor(RoseColor);
        range.Style.Font.Color.SetColor(Color.White);
        range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    }

    /// <summary>
    /// Column widths/formats for the punch-log sheets. Columns 1-6 (Id through Punch
    /// Type) are shared by both ExportLogsToExcel and ExportManualLogsToExcel; columns
    /// 7-8 (Reason, Entered By) only exist on the manual-entries sheet.
    /// </summary>
    private static void ApplyLogColumnFormats(ExcelWorksheet ws, bool includeManualColumns)
    {
        var columns = new List<(int Col, double Width, string? Format, ExcelHorizontalAlignment Align)>
        {
            (1, 5, null, ExcelHorizontalAlignment.Center),
            (2, 30, null, ExcelHorizontalAlignment.Left),
            (3, 20, null, ExcelHorizontalAlignment.Left),
            (4, 15, DateFormat, ExcelHorizontalAlignment.Right),
            (5, 15, TimeFormat2, ExcelHorizontalAlignment.Right),
            (6, 15, null, ExcelHorizontalAlignment.Left),
        };

        if (includeManualColumns)
        {
            columns.Add((7, 35, null, ExcelHorizontalAlignment.Left));
            columns.Add((8, 20, null, ExcelHorizontalAlignment.Left));
        }

        foreach (var (col, width, format, align) in columns)
        {
            var c = ws.Column(col);
            c.Width = width;
            c.Style.HorizontalAlignment = align;
            if (format is not null)
                c.Style.Numberformat.Format = format;
        }

        ws.Column(6).Style.Indent = 2;
    }

    /// <summary>
    /// Exports attendance summaries to Excel.
    /// Writes one worksheet per department when the summaries span more than
    /// one calendar day. When every summary falls on the same single day, a
    /// per-department breakdown isn't worth it, so everything is written to
    /// one "All Employees" worksheet (with an added Department column)
    /// instead -- this is decided automatically from the data, not by a
    /// caller-supplied flag.
    /// </summary>
    public static void ExportSummaryToExcel(
        string filePath,
        IEnumerable<AttendanceSummary> summaries,
        AttendancePolicy policy
    )
    {
        ExcelLicense.EnsureConfigured();

        // Materialize once: we need to inspect the data (distinct ShiftDates)
        // before deciding how to lay out the workbook, then iterate it again
        // to actually write rows. Re-enumerating a lazy IEnumerable here
        // would be wasteful at best and unsafe at worst.
        var summaryList = summaries as IReadOnlyCollection<AttendanceSummary> ?? [.. summaries];

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists)
            fileInfo.Delete();

        using var package = new ExcelPackage(fileInfo);

        // <= 1 rather than == 1 so an empty summary list also takes this
        // branch -- a run with zero summaries still needs at least one
        // worksheet, or EPPlus will refuse to save the workbook.
        bool singleSheet = summaryList.Select(s => s.ShiftDate).Distinct().Count() <= 1;
        var cols = new SummaryColumns(includeDepartment: singleSheet);

        if (singleSheet)
        {
            var worksheet = package.Workbook.Worksheets.Add("All Employees");
            WriteSummaryHeaders(worksheet, cols);
            ApplyColumnFormats(worksheet, cols);
            WriteRows(worksheet, summaryList, policy, cols, includeEmployeeTotals: false);
            FinalizeWorksheet(worksheet, cols);
        }
        else
        {
            var departments = summaryList.GroupBy(s => s.Department).OrderBy(g => g.Key);

            foreach (var department in departments)
            {
                var sheetName = ExcelSheetNaming.SanitizeSheetName(department.Key);
                var worksheet = package.Workbook.Worksheets.Add(sheetName);

                WriteSummaryHeaders(worksheet, cols);
                ApplyColumnFormats(worksheet, cols);
                WriteRows(worksheet, department, policy, cols, includeEmployeeTotals: true);
                FinalizeWorksheet(worksheet, cols);
            }
        }

        package.Save();
    }

    private static void FinalizeWorksheet(ExcelWorksheet ws, SummaryColumns cols)
    {
        ws.View.FreezePanes(2, cols.CheckIn);
        ws.Cells.Style.Font.Size = 12;
        ws.View.ShowGridLines = false;
    }

    private static void WriteSummaryHeaders(ExcelWorksheet ws, SummaryColumns cols)
    {
        ws.Cells[1, cols.Id].Value = "Id";
        ws.Cells[1, cols.Name].Value = "Name";
        if (cols.IncludeDepartment)
            ws.Cells[1, cols.Department].Value = "Department";
        ws.Cells[1, cols.Type].Value = "Type";
        ws.Cells[1, cols.ShiftDate].Value = "ShiftDate";
        ws.Cells[1, cols.CheckIn].Value = "CheckIn";
        ws.Cells[1, cols.CheckOut].Value = "CheckOut";
        ws.Cells[1, cols.Span].Value = "Span";
        ws.Cells[1, cols.ClockIn].Value = "ClockIn";
        ws.Cells[1, cols.ClockOut].Value = "ClockOut";
        ws.Cells[1, cols.WorkDay].Value = "WorkDay";
        ws.Cells[1, cols.LateIn_T].Value = "LateIn_T";
        ws.Cells[1, cols.EarlyOut_T].Value = "EarlyOut_T";
        ws.Cells[1, cols.Remain_T].Value = "Remain_T";
        ws.Cells[1, cols.Remain_H].Value = "Remain_H";
        ws.Cells[1, cols.Overtime_T].Value = "Overtime_T";
        ws.Cells[1, cols.Overtime_H].Value = "Overtime_H";
        ws.Cells[1, cols.NightDiff_T].Value = "NightDiff_T";
        ws.Cells[1, cols.NightDiff_H].Value = "NightDiff_H";
        ws.Cells[1, cols.Status].Value = "Status";

        using var headerRange = ws.Cells[1, cols.Id, 1, cols.Status];
        headerRange.Style.Font.Size = 12;
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
        headerRange.Style.Fill.BackgroundColor.SetColor(RoseColor);
        headerRange.Style.Font.Color.SetColor(Color.White);
        ws.Cells[1, cols.ShiftDate, 1, cols.Overtime_H].Style.HorizontalAlignment =
            ExcelHorizontalAlignment.Right;
        headerRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
        headerRange.Style.Border.Left.Color.SetColor(LightPinkColor);
        headerRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        headerRange.Style.Border.Right.Color.SetColor(LightPinkColor);
    }

    private static void ApplyColumnFormats(ExcelWorksheet ws, SummaryColumns cols)
    {
        ws.Column(cols.Id).Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        ws.Column(cols.Type).Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
        ws.Column(cols.ShiftDate).Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;

        // Number formatting
        ws.Column(cols.ShiftDate).Style.Numberformat.Format = DateFormat;
        ws.Column(cols.Span).Style.Numberformat.Format = NumericFormat;
        ws.Column(cols.CheckIn).Style.Numberformat.Format = TimeFormat1;
        ws.Column(cols.CheckOut).Style.Numberformat.Format = TimeFormat1;
        ws.Column(cols.ClockIn).Style.Numberformat.Format = TimeFormat1;
        ws.Column(cols.ClockOut).Style.Numberformat.Format = TimeFormat1;
        ws.Column(cols.WorkDay).Style.Numberformat.Format = NumericFormat;
        ws.Column(cols.LateIn_T).Style.Numberformat.Format = TimeFormat2;
        ws.Column(cols.EarlyOut_T).Style.Numberformat.Format = TimeFormat2;
        ws.Column(cols.Remain_T).Style.Numberformat.Format = TimeFormat2;
        ws.Column(cols.Remain_H).Style.Numberformat.Format = NumericFormat;
        ws.Column(cols.Overtime_T).Style.Numberformat.Format = TimeFormat2;
        ws.Column(cols.Overtime_H).Style.Numberformat.Format = NumericFormat;
        ws.Column(cols.NightDiff_T).Style.Numberformat.Format = TimeFormat2;
        ws.Column(cols.NightDiff_H).Style.Numberformat.Format = NumericFormat;

        // Column width
        ws.Column(cols.Id).Width = 3.44;
        ws.Column(cols.Name).Width = 20.33;
        if (cols.IncludeDepartment)
            ws.Column(cols.Department).Width = 18.0;
        ws.Column(cols.Type).Width = 12.0;
        for (int col = cols.ShiftDate; col <= cols.Status; col++)
            ws.Column(col).Width = 13.0;
    }

    private static int WriteRows(
        ExcelWorksheet ws,
        IEnumerable<AttendanceSummary> summaries,
        AttendancePolicy policy,
        SummaryColumns cols,
        bool includeEmployeeTotals
    )
    {
        var summaryList = summaries as IReadOnlyCollection<AttendanceSummary> ?? [.. summaries];

        int row = 2;
        int firstDataRow = row;
        int lastDataRow = row - 1;

        var byEmployee = summaryList
            .GroupBy(s => s.EmployeeId)
            .OrderBy(g => g.First().Department)
            .ThenBy(g => g.First().EmployeeName);

        foreach (var employeeGroup in byEmployee)
        {
            int employeeStartRow = row;
            var employeeSummaries = employeeGroup.ToList();

            foreach (var s in employeeSummaries)
            {
                WriteIdentityColumns(ws, row, s, cols);
                WriteScheduleColumns(ws, row, s, cols);
                WriteActualPunchColumns(ws, row, s, cols);
                WriteHoursAndTimeColumns(ws, row, s, policy, cols);
                WriteStatusColumn(ws, row, s, cols);
                row++;
            }

            int employeeEndRow = row - 1;
            lastDataRow = employeeEndRow;
            ApplyBlockConditionalFormatting(ws, employeeStartRow, employeeEndRow, cols);

            if (includeEmployeeTotals)
            {
                ApplyGroupBorder(ws, employeeStartRow, row, cols);
                WriteEmployeeTotalRow(ws, row, employeeSummaries, employeeStartRow, employeeEndRow, cols);
                row += 2;
            }
            else
            {
                ApplyGroupBorder(ws, employeeStartRow, employeeEndRow, cols);
            }
        }

        if (!includeEmployeeTotals && lastDataRow >= firstDataRow)
        {
            WriteOverallTotalRow(ws, row + 1, summaryList, firstDataRow, lastDataRow, cols);
            row += 2;
        }

        return row - 1;
    }

    private static void ApplyGroupBorder(ExcelWorksheet ws, int startRow, int endRow, SummaryColumns cols)
    {
        using var groupRange = ws.Cells[startRow, cols.Id, endRow, cols.Status];

        groupRange.Style.Border.Top.Style = ExcelBorderStyle.Thin;
        groupRange.Style.Border.Top.Color.SetColor(LightPinkColor);
        groupRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
        groupRange.Style.Border.Left.Color.SetColor(LightPinkColor);
        groupRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        groupRange.Style.Border.Right.Color.SetColor(LightPinkColor);
        groupRange.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
        groupRange.Style.Border.Bottom.Color.SetColor(LightPinkColor);
    }

    private static void WriteIdentityColumns(ExcelWorksheet ws, int row, AttendanceSummary s, SummaryColumns cols)
    {
        ws.Cells[row, cols.Id].Value = s.EmployeeId;
        ws.Cells[row, cols.Name].Value = s.EmployeeName;

        if (cols.IncludeDepartment)
            ws.Cells[row, cols.Department].Value = s.Department;

        ws.Cells[row, cols.Type].Value = s.ScheduleType.ToText();
    }

    private static void WriteScheduleColumns(ExcelWorksheet ws, int row, AttendanceSummary s, SummaryColumns cols)
    {
        ws.Cells[row, cols.ShiftDate].Value = s.ShiftDate.ToDateTime(TimeOnly.MinValue);

        if (s.Status is PunchStatus.Leave)
            return;

        // Flexible never has a single scheduled window (it's a merge-of-
        // arbitrary-pairs shape, whether or not RestrictedTimeIn/Out narrow
        // it -- see FlexibleShiftCalculationStrategy), so it never sets
        // HasScheduledWindow. A SplitShift row (one per segment -- see
        // SplitShiftCalculationStrategy) and a Normal/OfficialBusiness row
        // with a scheduled TimeIn/WorkTimeHours (see
        // OfficialBusinessShiftCalculationStrategy) both *do* have a real
        // window -- that's what HasScheduledWindow is checking below, not
        // ScheduleType directly.
        if (s.HasScheduledWindow)
        {
            ws.Cells[row, cols.CheckIn].Value = ToExcelTime(s.CheckIn);
            ws.Cells[row, cols.CheckOut].Value = ToExcelTime(s.CheckOut);
        }
        else if (s.ScheduleType == ScheduleType.Flexible && s.CheckIn != default)
        {
            // Not a real window -- CheckOut is deliberately left blank here,
            // since RestrictedTimeOut is a search-boundary filter, not a
            // window edge, and so has no CheckOut-shaped value to show (see
            // ScheduleEntry.RestrictedTimeOut). FlexibleShiftCalculationStrategy
            // copies RestrictedTimeIn into CheckIn purely for export
            // visibility -- see its own doc comment -- so the restricted
            // boundary still shows up next to ClockIn even though this row
            // uses a literal value here, not the live formula (see
            // WriteHoursAndTimeColumns below).
            ws.Cells[row, cols.CheckIn].Value = ToExcelTime(s.CheckIn);
        }

        if (s.Span.HasValue)
            ws.Cells[row, cols.Span].Value = s.Span.Value;
    }

    private static void WriteActualPunchColumns(ExcelWorksheet ws, int row, AttendanceSummary s, SummaryColumns cols)
    {
        if (s.ClockIn.HasValue)
        {
            ws.Cells[row, cols.ClockIn].Value = ToExcelTime(s.ClockIn.Value);
            if (s.ClockInIsManual)
                MarkManualCell(ws.Cells[row, cols.ClockIn]);
        }

        if (s.ClockOut.HasValue)
        {
            ws.Cells[row, cols.ClockOut].Value = ToExcelTime(s.ClockOut.Value);
            if (s.ClockOutIsManual)
                MarkManualCell(ws.Cells[row, cols.ClockOut]);
        }
    }

    /// <summary>Flags a ClockIn/ClockOut cell that came from a manual entry
    /// (AttendanceSummary.ClockInIsManual/ClockOutIsManual) -- italic plus a
    /// cell comment, rather than a new column, since SummaryColumns' fixed
    /// column indices ripple through headers, formats, formulas, merges, and
    /// conditional formatting ranges everywhere else in this file; a comment
    /// carries the same "this wasn't from the device" information without
    /// touching any of that. See ManualAttendanceLog for the underlying
    /// entry -- its Reason is visible in the Punch Records export instead of
    /// repeated here.</summary>
    private static void MarkManualCell(ExcelRange cell)
    {
        cell.Style.Font.Italic = true;
        cell.Style.Font.Color.SetColor(RosyBrown);
        cell.AddComment("Manually entered -- see Punch Records for who and why.", "ScheduleApp");
    }

    private static void WriteHoursAndTimeColumns(
        ExcelWorksheet ws,
        int row,
        AttendanceSummary s,
        AttendancePolicy policy,
        SummaryColumns cols
    )
    {
        if (s.Status is PunchStatus.Leave)
            return;

        // The live formula below reconstructs WorkDay etc. from exactly one CheckIn/
        // CheckOut/ClockIn/ClockOut pair per row -- it has no way to represent a
        // Flexible day's sum of an arbitrary number of in/out pairs (see
        // FlexibleShiftCalculationStrategy.CalculateUnrestrictedDay, which never
        // sets HasScheduledWindow even when RestrictedTimeIn/Out narrow the day),
        // so those rows always get the literal values C# already computed,
        // regardless of policy.UseExcelFormula. A SplitShift row (HasScheduledWindow
        // true, one per segment) is a single CheckIn/CheckOut/ClockIn/ClockOut window
        // computed with the exact same CapEarlyClockIn/ClockOutGracePeriod/
        // StrictOvertimeFromShiftEnd math as a Normal row (see
        // SplitShiftCalculationStrategy.CalculateSegment vs.
        // SingleWindowShiftCalculationStrategy), so it gets the live formula too --
        // HasScheduledWindow is the right discriminator here, not ScheduleType.
        // An OfficialBusiness row with a scheduled window is exactly such a case:
        // ClockIn/ClockOut equal CheckIn/CheckOut exactly (see
        // OfficialBusinessShiftCalculationStrategy), so either path -- literal or
        // live formula -- lands on WorkDay == 1.0 with zero late/early/overtime,
        // same result either way.
        //
        // RestDay is the one exception where HasScheduledWindow alone isn't enough,
        // so it's forced onto the literal path explicitly below rather than falling
        // through to the formula. Unlike SplitShift/OfficialBusiness above,
        // RestDayShiftCalculationStrategy's windowed mode (see its own doc comment)
        // does not mirror SingleWindowShiftCalculationStrategy: a duty needs *both*
        // sides found (no partial credit), there's no clock-out grace-period
        // clipping, hours past the scheduled window's end extend Worked_H instead of
        // splitting into Overtime_H, and LateIn_T/EarlyOut_T are never populated at
        // all. ClockIn/ClockOut on a recognized duty are genuine punch timestamps
        // (not a copy of CheckIn/CheckOut the way OfficialBusiness's are), so the
        // generic formula would compute real, nonzero LateIn/EarlyOut/Overtime
        // figures off them -- figures the app itself never reports for a Rest Day.
        if (!policy.UseExcelFormula || !s.HasScheduledWindow || s.ScheduleType == ScheduleType.RestDay)
        {
            // Blank when WorkDay is null (no Span to measure against -- see
            // AttendanceSummary.WorkDay) or exactly 0, same zero-skip
            // convention every other column in this branch already follows.
            if (s.WorkDay is { } workDay && workDay > 0)
                ws.Cells[row, cols.WorkDay].Value = workDay;

            if (s.LateIn_T.TotalMinutes > 0)
                ws.Cells[row, cols.LateIn_T].Value = s.LateIn_T;

            if (s.EarlyOut_T.TotalMinutes > 0)
                ws.Cells[row, cols.EarlyOut_T].Value = s.EarlyOut_T;

            if (s.Remain_H > 0)
            {
                ws.Cells[row, cols.Remain_T].Value = s.Remain_T;
                ws.Cells[row, cols.Remain_H].Value = s.Remain_H;
            }

            if (s.Overtime_H > 0)
            {
                ws.Cells[row, cols.Overtime_T].Value = s.Overtime_T;
                ws.Cells[row, cols.Overtime_H].Value = s.Overtime_H;
            }

            // s.NightDiff_H is always 0 for Official Business (see
            // OfficialBusinessShiftCalculationStrategy), so this naturally leaves
            // an OB row's night-diff cells blank without a separate ScheduleType
            // check here -- unlike the live-formula branch below, which derives
            // night diff from ClockIn/ClockOut directly and so does need one.
            if (s.NightDiff_H > 0)
            {
                ws.Cells[row, cols.NightDiff_T].Value = s.NightDiff_T;
                ws.Cells[row, cols.NightDiff_H].Value = s.NightDiff_H;
            }

            return;
        }

        string D = ExcelCellAddress.GetColumnLetter(cols.CheckIn);
        string E = ExcelCellAddress.GetColumnLetter(cols.CheckOut);
        string F = ExcelCellAddress.GetColumnLetter(cols.Span);
        string G = ExcelCellAddress.GetColumnLetter(cols.ClockIn);
        string H = ExcelCellAddress.GetColumnLetter(cols.ClockOut);
        string K = ExcelCellAddress.GetColumnLetter(cols.LateIn_T);
        string L = ExcelCellAddress.GetColumnLetter(cols.EarlyOut_T);
        string M = ExcelCellAddress.GetColumnLetter(cols.Remain_T);
        string O = ExcelCellAddress.GetColumnLetter(cols.Overtime_T);

        string eAdj = $"IF({E}{row}<{D}{row},{E}{row}+1,{E}{row})";
        string clockInNorm = $"IF({G}{row}-{D}{row}>0.75, {G}{row}-1, {G}{row})";
        string inRef = $"IF({G}{row}<>\"\",{clockInNorm},{D}{row})";
        string hAdj = $"IF({H}{row}<{inRef},{H}{row}+1,{H}{row})";
        string grace = FormattableString.Invariant($"{policy.ClockOutGracePeriod / 24.0:R}");

        // Same idea as `grace` above, but for LateIn_T/EarlyOut_T rather than
        // overtime -- policy.LateInEarlyOutGraceMinutes converted from minutes to
        // Excel's day-fraction time unit (1440 minutes/day, vs. `grace`'s 24
        // hours/day) so it can be compared directly against the day-fraction
        // expressions below. See AttendancePolicy.LateInEarlyOutGraceMinutes.
        string lateEarlyGrace = FormattableString.Invariant($"{policy.LateInEarlyOutGraceMinutes / 1440.0:R}");

        string effIn = policy.CapEarlyClockIn ? $"MAX({clockInNorm},{D}{row})" : $"{clockInNorm}";
        string effOut = $"IF(AND({hAdj}>={eAdj},{hAdj}<{eAdj}+{grace}),{eAdj},{hAdj})";

        // Same effIn/effOut interval the old Worked_T formula used, converted
        // to hours and divided by this row's own Span (column F) -- i.e. the
        // live-formula mirror of AttendanceSummary.WorkDay. MIN(...,1) is the
        // same cap. Two different "nothing to show" cases, matching WorkDay's
        // own null-vs-zero distinction: no Span at all (nothing to divide by)
        // resolves to "" so the cell renders truly blank, same as WorkDay
        // being null; Span present but no punches resolves to a real 0, same
        // as every other zero result in this formula branch (e.g. LateIn_T),
        // which this workbook deliberately shows rather than blanks.
        string workedHoursExpr = $"MAX({effOut}-{effIn},0)*24";
        string hasSpan = $"AND({F}{row}<>\"\",{F}{row}>0)";
        string hasPunches = $"AND({G}{row}<>\"\",{H}{row}<>\"\")";
        ws.Cells[row, cols.WorkDay].Formula =
            $"IF({hasSpan},IF({hasPunches},MIN({workedHoursExpr}/{F}{row},1),0),\"\")";

        // Grace-period carve-out: a difference at or below lateEarlyGrace is exactly
        // on time (0), not just "small" -- past it, the entire raw difference counts,
        // same all-or-nothing shape SingleWindowShiftCalculationStrategy/
        // SplitShiftCalculationStrategy apply in C#, which this has to agree with
        // (see this method's own doc comment).
        string lateInRaw = $"MAX({effIn}-{D}{row},0)";
        string earlyOutRaw = $"MAX({eAdj}-{effOut},0)";
        ws.Cells[row, cols.LateIn_T].Formula =
            $"IF({G}{row}<>\"\",IF({lateInRaw}>{lateEarlyGrace},{lateInRaw},0),0)";
        ws.Cells[row, cols.EarlyOut_T].Formula =
            $"IF({H}{row}<>\"\",IF({earlyOutRaw}>{lateEarlyGrace},{earlyOutRaw},0),0)";

        ws.Cells[row, cols.Remain_T].Formula = $"SUM({K}{row},{L}{row})";
        ws.Cells[row, cols.Remain_H].Formula = $"{M}{row}*24";

        string otHours = policy.StrictOvertimeFromShiftEnd
            ? $"({hAdj}-{eAdj})*24"
            : $"MAX(({effOut}-{effIn})*24-{F}{row},0)";

        string otExpr = $"IF({hAdj}<{eAdj}+{grace},0,{otHours})";

        ws.Cells[row, cols.Overtime_T].Formula = $"IF({H}{row}<>\"\",{otExpr}/24,0)";
        ws.Cells[row, cols.Overtime_H].Formula = $"{O}{row}*24";

        // Official Business deliberately never gets night diff (see
        // OfficialBusinessShiftCalculationStrategy) -- its ClockIn/ClockOut equal
        // CheckIn/CheckOut exactly, so unlike Worked_T/Overtime_T above (where
        // that equality happens to land on the right answer either way), a
        // night-diff formula here would wrongly compute hours whenever the
        // scheduled window itself overlaps the night-diff range. Skipped
        // entirely -- not written as a 0 formula -- so the cell stays blank,
        // same as an OB row's CheckIn/CheckOut stay blank when there's no
        // scheduled window at all (see WriteScheduleColumns).
        if (s.ScheduleType == ScheduleType.OfficialBusiness)
            return;

        double nightStartFrac = policy.NightDiffStart.ToTimeSpan().TotalDays;
        double nightEndFracRaw = policy.NightDiffEnd.ToTimeSpan().TotalDays;
        double nightEndFrac = nightEndFracRaw <= nightStartFrac ? nightEndFracRaw + 1 : nightEndFracRaw;
        string nightStart = FormattableString.Invariant($"{nightStartFrac:R}");
        string nightEnd = FormattableString.Invariant($"{nightEndFrac:R}");

        // Same window, shifted back one full day -- covers a shift like
        // 12:00 AM-8:00 AM, which is entirely worked *before* this row's own
        // nightStart/nightEnd window even opens (that window doesn't start
        // until tonight at 10 PM) but falls squarely inside *last* night's
        // 10 PM-6 AM window instead. Without this, effIn/effOut for such a
        // shift never overlap nightStart/nightEnd at all and the single-window
        // formula below silently clamps to 0 -- see NightDifferentialCalculator,
        // whose day-1-to-lastDay loop this two-window formula is mirroring.
        string nightStartPrev = FormattableString.Invariant($"{nightStartFrac - 1:R}");
        string nightEndPrev = FormattableString.Invariant($"{nightEndFrac - 1:R}");
        string ndCol = ExcelCellAddress.GetColumnLetter(cols.NightDiff_T);

        // Same shape as the Worked_T formula above -- overlap of the effective
        // (capped/graced) worked interval with the night-diff window, anchored
        // to this row's CheckIn day, not the raw punch times or the scheduled
        // window. Two MIN/MAX terms (this row's own night window, plus the
        // previous day's) rather than a general day-before/day-after loop --
        // sufficient here because effIn/effOut are themselves already anchored
        // to at most one day past CheckIn's day (see eAdj/hAdj above), so the
        // interval can never touch more than these two candidate windows.
        string windowThisDay = $"MAX(ROUND(MIN({effOut},{nightEnd})-MAX({effIn},{nightStart}),8),0)";
        string windowPrevDay = $"MAX(ROUND(MIN({effOut},{nightEndPrev})-MAX({effIn},{nightStartPrev}),8),0)";
        ws.Cells[row, cols.NightDiff_T].Formula =
               $"IF(AND({G}{row}<>\"\",{H}{row}<>\"\"),{windowThisDay}+{windowPrevDay},0)";
        ws.Cells[row, cols.NightDiff_H].Formula = $"{ndCol}{row}*24";
    }

    private static void WriteStatusColumn(ExcelWorksheet ws, int row, AttendanceSummary s, SummaryColumns cols)
    {
        ws.Cells[row, cols.Status].Value = s.Status switch
        {
            PunchStatus.Complete => "Complete",
            PunchStatus.Partial => "Partial",
            PunchStatus.Absent => "Absent",
            PunchStatus.Leave => "Leave",
            PunchStatus.OfficialBusiness => "Official Business",
            PunchStatus.RestDay => "Rest Day",
            _ => s.Status.ToString(),
        };
    }

    /// <summary>
    /// Writes the "XC | YP | ZA | WOB | VRD" status breakdown as a literal value
    /// computed from AttendanceDayStatus, not a live COUNTIFS formula over the
    /// Status column like before -- a formula counting rows can't tell a
    /// SplitShift day's multiple rows apart from multiple actual days
    /// (see AttendanceDayStatus's doc comment), so it would silently
    /// over-count exactly the case that type exists to handle correctly.
    /// Matches the grouping AttendanceViewModel's results panel uses on the
    /// WPF side, so the two always agree. Absent and Leave are still combined
    /// into "A", same as the formula this replaced -- Official Business and
    /// Rest Day each get their own "OB"/"RD" letter instead, since (unlike
    /// Leave) both are meant to be visible as their own line item on the
    /// report rather than folded into the days-away total -- same call
    /// ReportViewModel.RestDayCount already makes as its own tile alongside
    /// OfficialBusinessCount on the Desktop side (see PHASE4-STATUS.md Row 6);
    /// kept consistent with that decision rather than making it independently
    /// here. Both letters are only appended when their count is nonzero, same
    /// as OB always was, so a report with no Rest Days looks exactly as it did
    /// before this letter existed.
    /// </summary>
    private static void WriteDayStatusBreakdown(
        ExcelWorksheet ws, int row, SummaryColumns cols, IEnumerable<AttendanceSummary> summaries)
    {
        var dayStatuses = AttendanceDayStatus.ByDay(summaries).ToList();

        int complete = dayStatuses.Count(s => s == PunchStatus.Complete);
        int partial = dayStatuses.Count(s => s == PunchStatus.Partial);
        int absentOrLeave = dayStatuses.Count(s => s is PunchStatus.Absent or PunchStatus.Leave);
        int officialBusiness = dayStatuses.Count(s => s == PunchStatus.OfficialBusiness);
        int restDay = dayStatuses.Count(s => s == PunchStatus.RestDay);

        var breakdown = $"{complete}C | {partial}P | {absentOrLeave}A";
        if (officialBusiness > 0) breakdown += $" | {officialBusiness}OB";
        if (restDay > 0) breakdown += $" | {restDay}RD";

        ws.Cells[row, cols.Status].Value = breakdown;
    }

    private static void WriteEmployeeTotalRow(
        ExcelWorksheet ws,
        int totalRow,
        IReadOnlyCollection<AttendanceSummary> employeeSummaries,
        int startRow,
        int endRow,
        SummaryColumns cols
    )
    {
        var first = employeeSummaries.First();

        ws.Cells[totalRow, cols.Id].Value = first.EmployeeId;
        ws.Cells[totalRow, cols.Name].Value = $"TOTAL — {first.EmployeeName}";

        WriteDayStatusBreakdown(ws, totalRow, cols, employeeSummaries);

        SumColumn(ws, totalRow, cols.WorkDay, startRow, endRow);
        SumColumn(ws, totalRow, cols.LateIn_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.EarlyOut_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.Remain_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.Remain_H, startRow, endRow);
        SumColumn(ws, totalRow, cols.Overtime_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.Overtime_H, startRow, endRow);
        SumColumn(ws, totalRow, cols.NightDiff_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.NightDiff_H, startRow, endRow);

        using var totalRange = ws.Cells[totalRow, cols.Id, totalRow, cols.Status];
        totalRange.Style.Font.Bold = true;
        totalRange.Style.Font.Color.SetColor(RoseColor);
        totalRange.Style.Border.Top.Style = ExcelBorderStyle.Double;
        totalRange.Style.Border.Top.Color.SetColor(LightPinkColor);
        totalRange.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
        totalRange.Style.Border.Bottom.Color.SetColor(RoseColor);

        List<int> durationCols =
        [
            cols.LateIn_T,
            cols.EarlyOut_T,
            cols.Remain_T,
            cols.Overtime_T,
            cols.NightDiff_T,
        ];
        foreach (var col in durationCols)
            ws.Cells[totalRow, col].Style.Numberformat.Format = TimeFormat2;

        // Merges the Name cell through ShiftDate into one "TOTAL — Name" label.
        // Type (and Department, when present) sit between Name and ShiftDate, so
        // both get absorbed into this same merged label cell.
        ws.Cells[totalRow, cols.Name, totalRow, cols.ShiftDate].Merge = true;
    }

    private static void WriteOverallTotalRow(
        ExcelWorksheet ws,
        int totalRow,
        IReadOnlyCollection<AttendanceSummary> summaries,
        int startRow,
        int endRow,
        SummaryColumns cols
    )
    {
        ws.Cells[totalRow, cols.Id].Value = "GRAND TOTAL — All Employees";

        WriteDayStatusBreakdown(ws, totalRow, cols, summaries);

        SumColumn(ws, totalRow, cols.WorkDay, startRow, endRow);
        SumColumn(ws, totalRow, cols.LateIn_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.EarlyOut_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.Remain_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.Remain_H, startRow, endRow);
        SumColumn(ws, totalRow, cols.Overtime_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.Overtime_H, startRow, endRow);
        SumColumn(ws, totalRow, cols.NightDiff_T, startRow, endRow);
        SumColumn(ws, totalRow, cols.NightDiff_H, startRow, endRow);

        using var totalRange = ws.Cells[totalRow, cols.Id, totalRow, cols.Status];
        totalRange.Style.Font.Bold = true;
        totalRange.Style.Font.Color.SetColor(Color.White);
        totalRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
        totalRange.Style.Fill.BackgroundColor.SetColor(RoseColor);
        totalRange.Style.Border.Top.Style = ExcelBorderStyle.Medium;
        totalRange.Style.Border.Top.Color.SetColor(RoseColor);
        totalRange.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
        totalRange.Style.Border.Bottom.Color.SetColor(RoseColor);

        List<int> durationCols =
        [
            cols.LateIn_T,
            cols.EarlyOut_T,
            cols.Remain_T,
            cols.Overtime_T,
            cols.NightDiff_T,
        ];
        foreach (var col in durationCols)
            ws.Cells[totalRow, col].Style.Numberformat.Format = TimeFormat2;

        // Merges Id through ShiftDate (absorbing Type and Department, if present)
        // into one wide label cell for "GRAND TOTAL — All Employees".
        ws.Cells[totalRow, cols.Id, totalRow, cols.ShiftDate].Merge = true;
    }

    private static void SumColumn(ExcelWorksheet ws, int totalRow, int col, int startRow, int endRow)
    {
        string colLetter = ExcelCellAddress.GetColumnLetter(col);
        ws.Cells[totalRow, col].Formula = $"SUM({colLetter}{startRow}:{colLetter}{endRow})";
    }

    private static void ApplyBlockConditionalFormatting(
        ExcelWorksheet ws,
        int startRow,
        int endRow,
        SummaryColumns cols
    )
    {
        if (endRow < startRow)
            return;

        var dataRange = ws.Cells[startRow, cols.Id, endRow, cols.Status];
        string statusCol = ExcelCellAddress.GetColumnLetter(cols.Status);

        var absentRule = ws.ConditionalFormatting.AddExpression(dataRange);
        absentRule.Formula = $"${statusCol}{startRow}=\"Absent\"";
        absentRule.Style.Font.Color.SetColor(Color.FromArgb(192, 0, 0));

        var partialRule = ws.ConditionalFormatting.AddExpression(dataRange);
        partialRule.Formula = $"${statusCol}{startRow}=\"Partial\"";
        partialRule.Style.Font.Color.SetColor(CoralPink);

        var leaveRule = ws.ConditionalFormatting.AddExpression(dataRange);
        leaveRule.Formula = $"${statusCol}{startRow}=\"Leave\"";
        leaveRule.Style.Font.Color.SetColor(RosyBrown);

        var officialBusinessRule = ws.ConditionalFormatting.AddExpression(dataRange);
        officialBusinessRule.Formula = $"${statusCol}{startRow}=\"Official Business\"";
        officialBusinessRule.Style.Font.Color.SetColor(OfficialBusinessPurple);

        // Same teal (#0D9488) PunchStatusToBrushConverter uses for Rest Day on the
        // Desktop side's Status column, kept consistent here rather than picking a
        // new color independently -- see PHASE4-STATUS.md's Row 5 notes for the
        // other two surfaces (row background, Summary tile) that already match it.
        var restDayRule = ws.ConditionalFormatting.AddExpression(dataRange);
        restDayRule.Formula = $"${statusCol}{startRow}=\"Rest Day\"";
        restDayRule.Style.Font.Color.SetColor(RestDayTeal);
    }

    /// <summary>
    /// Computes column indices for the summary sheet layout. When
    /// <see cref="IncludeDepartment"/> is true, a Department column is inserted
    /// right after Name, shifting every subsequent column over by one -- this is
    /// the only thing that changes between the per-department-sheet layout and
    /// the single-sheet layout, so all the writer methods above stay identical
    /// regardless of which mode is active.
    /// </summary>
    private sealed class SummaryColumns
    {
        private readonly int _offset;

        public SummaryColumns(bool includeDepartment)
        {
            IncludeDepartment = includeDepartment;
            _offset = includeDepartment ? 1 : 0;
        }

        public bool IncludeDepartment { get; }

        // CA1822 (mark as static) is suppressed here deliberately: these three are
        // constant only because they sit at or left of the Department insertion point,
        // so _offset never reaches them. They are still read as `columns.Id` alongside
        // the _offset-dependent members below, and making just these three static would
        // break that uniform call shape.
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Kept as instance members for symmetry with the offset-dependent columns below.")]
        public int Id => 1;
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Kept as instance members for symmetry with the offset-dependent columns below.")]
        public int Name => 2;
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Kept as instance members for symmetry with the offset-dependent columns below.")]
        public int Department => 3; // only meaningful when IncludeDepartment is true

        /// <summary>Normal/Leave/Flexible/Official Business/Split Shift/Rest Day, written as plain text (see WriteIdentityColumns).
        /// Exists so a mixed report can't silently mislead: Remain_T/Remain_H and
        /// Overtime_T/Overtime_H mean different things depending on ScheduleType (late+early
        /// vs. hours-short-of-required-total -- see FlexibleShiftCalculationStrategy), and
        /// without this column there'd be nothing on the sheet itself to tell those apart.</summary>
        public int Type => 3 + _offset;
        public int ShiftDate => 4 + _offset;
        public int CheckIn => 5 + _offset;
        public int CheckOut => 6 + _offset;
        public int Span => 7 + _offset;
        public int ClockIn => 8 + _offset;
        public int ClockOut => 9 + _offset;

        /// <summary>Worked_H expressed as a fraction of the day's own Span --
        /// see AttendanceSummary.WorkDay. Replaces the old separate Worked_T/
        /// Worked_H columns; Worked_T/Worked_H themselves are still computed
        /// internally (WorkDay is derived from them) but no longer get their
        /// own columns.</summary>
        public int WorkDay => 10 + _offset;
        public int LateIn_T => 11 + _offset;
        public int EarlyOut_T => 12 + _offset;
        public int Remain_T => 13 + _offset;
        public int Remain_H => 14 + _offset;
        public int Overtime_T => 15 + _offset;
        public int Overtime_H => 16 + _offset;

        /// <summary>Blank (no formula, no literal value) on an Official Business row --
        /// see OfficialBusinessShiftCalculationStrategy and WriteHoursAndTimeColumns.</summary>
        public int NightDiff_T => 17 + _offset;
        public int NightDiff_H => 18 + _offset;
        public int Status => 19 + _offset;
    }
}