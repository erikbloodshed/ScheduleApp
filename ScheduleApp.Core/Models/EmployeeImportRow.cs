namespace ScheduleApp.Core.Models;

/// <summary>
/// One validated row from a roster workbook like Employees.xlsx: a single sheet
/// with a header row naming each column, so columns can appear in any order and
/// optional ones can be left out of the sheet entirely. EmployeeId, LastName, and
/// FirstName are required -- EmployeeRosterImporter.Import throws
/// ScheduleApp.Core.Exceptions.EmployeeImportException rather than producing a row
/// missing any of them. Everything else is optional; a property here is null when
/// its column is either absent from the sheet or blank on this row, which
/// IScheduleRepository.ImportEmployeeRosterAsync then treats as "leave this field
/// alone" on an existing employee and "use the class default" on a new one --
/// same convention DepartmentName already followed before this type grew the rest
/// of these fields.
///
/// Named after the Employee properties each feeds (see ScheduleRepository.
/// ApplyOptionalImportFields), not the sheet's own header text -- e.g.
/// QualifiesForOvertime here corresponds to the sheet's "IsOvertimeEligible"
/// column; EmployeeRosterImporter owns that name mapping.
/// </summary>
public class EmployeeImportRow
{
    public int Pin { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Null/blank in the source file means "no department yet" (new
    /// employee) or "don't change the department" (existing employee) -- see
    /// ImportEmployeeRosterAsync.</summary>
    public string? DepartmentName { get; set; }

    /// <summary>Sheet column "DailyRate". See Employee.DailyRate.</summary>
    public decimal? DailyRate { get; set; }

    /// <summary>Sheet column "IsOvertimeEligible". See Employee.QualifiesForOvertime.</summary>
    public bool? QualifiesForOvertime { get; set; }

    /// <summary>Sheet column "HasOvertimePremium". See Employee.ApplyOvertimeRatePercentageByDefault.</summary>
    public bool? ApplyOvertimeRatePercentageByDefault { get; set; }

    /// <summary>Sheet column "HasNightDiff". See Employee.QualifiesForNightDiff.</summary>
    public bool? QualifiesForNightDiff { get; set; }

    /// <summary>Sheet column "SSS". See Employee.DefaultSss.</summary>
    public decimal? DefaultSss { get; set; }

    /// <summary>Sheet column "PhilHealth". See Employee.DefaultPhilHealth.</summary>
    public decimal? DefaultPhilHealth { get; set; }

    /// <summary>Sheet column "PagIBIG". See Employee.DefaultPagIbig.</summary>
    public decimal? DefaultPagIbig { get; set; }

    /// <summary>Sheet column "HasLeaveWithPay". See Employee.DefaultLeaveIsPaid.</summary>
    public bool? DefaultLeaveIsPaid { get; set; }
}
