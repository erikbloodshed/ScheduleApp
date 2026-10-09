using OfficeOpenXml;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Excel;

/// <summary>
/// Writes the employee roster in exactly the layout EmployeeRosterImporter reads
/// back: a single sheet named "Employees" whose first row carries the importer's
/// own header names (shared via its constants, so the two can't drift apart), one
/// row per employee below. Every recognized column is written, required and
/// optional alike, so an exported file round-trips through Import Employees
/// without losing any field the importer knows about.
///
/// Department is the department's name (blank for an unassigned employee), and
/// the Yes/No columns are written as real Excel booleans -- ReadOptionalBool
/// accepts those directly. Rows are ordered by department (SortOrder, the same
/// order the Employees page shows), then last/first name, with unassigned
/// employees last.
/// </summary>
public static class EmployeeRosterExporter
{
    private static readonly string[] Headers =
    [
        EmployeeRosterImporter.EmployeeIdHeader,
        EmployeeRosterImporter.LastNameHeader,
        EmployeeRosterImporter.FirstNameHeader,
        EmployeeRosterImporter.DepartmentHeader,
        EmployeeRosterImporter.DailyRateHeader,
        EmployeeRosterImporter.IsOvertimeEligibleHeader,
        EmployeeRosterImporter.HasOvertimePremiumHeader,
        EmployeeRosterImporter.HasNightDiffHeader,
        EmployeeRosterImporter.SssHeader,
        EmployeeRosterImporter.PhilHealthHeader,
        EmployeeRosterImporter.PagIbigHeader,
        EmployeeRosterImporter.HasLeaveWithPayHeader,
    ];

    private const string MoneyFormat = "#,##0.00";

    public static void Export(IEnumerable<Department> departments, IEnumerable<Employee> unassignedEmployees, string filePath)
    {
        ExcelLicense.EnsureConfigured();

        using var package = new ExcelPackage();
        var ws = package.Workbook.Worksheets.Add("Employees");

        for (var c = 0; c < Headers.Length; c++)
            ws.Cells[1, c + 1].Value = Headers[c];
        ws.Cells[1, 1, 1, Headers.Length].Style.Font.Bold = true;

        var rows = departments
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .SelectMany(d => OrderByName(d.Employees).Select(e => (Employee: e, DepartmentName: (string?)d.Name)))
            .Concat(OrderByName(unassignedEmployees).Select(e => (Employee: e, DepartmentName: (string?)null)));

        var row = 2;
        foreach (var (employee, departmentName) in rows)
        {
            ws.Cells[row, 1].Value = employee.Pin;
            ws.Cells[row, 2].Value = employee.LastName;
            ws.Cells[row, 3].Value = employee.FirstName;
            ws.Cells[row, 4].Value = departmentName;
            ws.Cells[row, 5].Value = employee.DailyRate;
            ws.Cells[row, 6].Value = employee.QualifiesForOvertime;
            ws.Cells[row, 7].Value = employee.ApplyOvertimeRatePercentageByDefault;
            ws.Cells[row, 8].Value = employee.QualifiesForNightDiff;
            ws.Cells[row, 9].Value = employee.DefaultSss;
            ws.Cells[row, 10].Value = employee.DefaultPhilHealth;
            ws.Cells[row, 11].Value = employee.DefaultPagIbig;
            ws.Cells[row, 12].Value = employee.DefaultLeaveIsPaid;
            row++;
        }

        if (row > 2)
        {
            foreach (var col in new[] { 5, 9, 10, 11 })
                ws.Cells[2, col, row - 1, col].Style.Numberformat.Format = MoneyFormat;
        }

        ws.View.FreezePanes(2, 1);
        ws.Cells[ws.Dimension.Address].AutoFitColumns();

        package.SaveAs(new FileInfo(filePath));
    }

    private static IEnumerable<Employee> OrderByName(IEnumerable<Employee> employees)
        => employees.OrderBy(e => e.LastName).ThenBy(e => e.FirstName);
}
