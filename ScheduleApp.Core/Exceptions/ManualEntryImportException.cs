namespace ScheduleApp.Core.Exceptions;

/// <summary>
/// Thrown by ManualEntryImporter.Import (ScheduleApp.Excel) when the workbook is
/// missing a required column (Id, Date, Time, Punch Type, or Reason), or when one
/// or more data rows fail validation -- an Id that doesn't match any employee, a
/// blank required field, a Date/Time cell that can't be parsed, a Punch Type value
/// that isn't "Clock In"/"Clock Out", or a Reason/Entered By value longer than the
/// database column allows.
///
/// Carries every problem found across the whole sheet, not just the first -- the
/// importer keeps reading past a bad row instead of stopping there, so someone
/// fixing a large workbook can address every problem in one pass instead of
/// re-importing repeatedly to discover them one at a time. Rows with no problems
/// are still discarded when this is thrown -- Import is all-or-nothing, same
/// convention EmployeeImportException already established for the employee
/// roster (see its own doc comment).
/// </summary>
public class ManualEntryImportException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ManualEntryImportException(IReadOnlyList<string> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyList<string> errors)
        => errors.Count == 1
            ? errors[0]
            : $"{errors.Count} problems found:\n{string.Join("\n", errors)}";
}
