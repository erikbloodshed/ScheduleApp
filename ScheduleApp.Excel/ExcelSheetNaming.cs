namespace ScheduleApp.Excel;

/// <summary>
/// Excel worksheet names can't exceed 31 characters or contain \ / ? * [ ] : --
/// this normalizes a department/employee name into something safe to hand to
/// ExcelWorksheets.Add. Shared by AttendanceExcelExporter and ExcelScheduleExporter
/// (each used to carry its own copy, and the two didn't even sanitize the same way --
/// see the review notes) so a name with the same invalid characters comes out
/// identical no matter which exporter touches it.
/// </summary>
internal static class ExcelSheetNaming
{
    private static readonly char[] InvalidSheetNameChars = { '\\', '/', '?', '*', '[', ']', ':' };

    public static string SanitizeSheetName(string name)
    {
        foreach (var ch in InvalidSheetNameChars)
            name = name.Replace(ch, ' ');

        return name.Length > 31 ? name[..31] : name;
    }
}
