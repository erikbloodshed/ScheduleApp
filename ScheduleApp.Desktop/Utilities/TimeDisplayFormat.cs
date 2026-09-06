using System.Globalization;

namespace ScheduleApp.Desktop.Utilities;

/// <summary>
/// Centralizes the 12-hour time-of-day format shown throughout the Desktop UI
/// (combo box items, grid columns, dialog defaults, status bar messages). This
/// is a display/input concern only -- TimeOnly/DateTime have no "hour format"
/// of their own, and everything downstream (ScheduleApp.Core, the database,
/// Excel export) keeps working in plain 24-hour TimeOnly/DateTime values.
/// </summary>
public static class TimeDisplayFormat
{
    /// <summary>Composite format string, e.g. "5:00 PM" -- pass to
    /// TimeOnly/DateTime.ToString() or a XAML StringFormat.</summary>
    public const string Pattern = "hh:mm tt";

    public static string Format(TimeOnly time) => time.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>Parses user-typed time text back into a 24-hour TimeOnly for
    /// storage/calculation. Primarily expects the 12-hour display format
    /// ("5:00 PM"), but also accepts plain 24-hour text ("17:00") for anyone
    /// who types it that way out of habit, plus a final loose-parse fallback
    /// for other reasonable variations (e.g. "5:00pm", "17:00:00").</summary>
    public static bool TryParse(string? text, out TimeOnly result)
    {
        text = text?.Trim() ?? string.Empty;

        return TimeOnly.TryParseExact(
                   text,
                   new[] { "h:mm tt", "hh:mm tt", "h:mmtt", "H:mm", "HH:mm" },
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out result) ||
               TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out result);
    }
}
