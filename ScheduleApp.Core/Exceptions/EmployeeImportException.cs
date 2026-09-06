namespace ScheduleApp.Core.Exceptions;

/// <summary>
/// Thrown by EmployeeRosterImporter.Import when the workbook is missing a required
/// column (EmployeeId, LastName, or FirstName), or when one or more data rows fail
/// validation -- a blank required field, an EmployeeId reused by more than one row,
/// or an optional field whose cell value can't be parsed as the expected type.
///
/// Carries every problem found across the whole sheet, not just the first -- the
/// importer keeps reading past a bad row instead of stopping there, so someone
/// fixing a large workbook can address every problem in one pass instead of
/// re-importing repeatedly to discover them one at a time. Rows with no problems
/// are still discarded when this is thrown -- Import is all-or-nothing, matching
/// the pre-existing convention for schedule-workbook import (ExcelScheduleImporter/
/// MainViewModel.ImportScheduleAsync).
/// </summary>
public class EmployeeImportException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public EmployeeImportException(IReadOnlyList<string> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyList<string> errors)
        => errors.Count == 1
            ? errors[0]
            : $"{errors.Count} problems found:\n{string.Join("\n", errors)}";
}
