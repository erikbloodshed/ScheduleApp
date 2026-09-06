using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Maps AttendanceSummary.Status to a light tint of the same colors
/// PunchStatusToBrushConverter uses for the Status column's text -- for
/// highlighting a whole row in the in-app Attendance Summary grid rather
/// than just that one cell. Used by SummaryRowBackgroundConverter, which
/// layers selection state on top of whatever this returns; not bound
/// directly in XAML.
/// Complete deliberately maps to the plain default background rather than a
/// tint of its own -- it's the expected outcome, so leaving it unhighlighted
/// is what makes the Partial/Absent/Leave/Official Business/Rest Day rows
/// stand out.
/// Those five are much lighter than PunchStatusToBrushConverter's colors --
/// those are meant to sit as bold text on a white cell, but spread across an
/// entire row as a solid fill they'd make the row's own black text
/// unreadable, so this uses ~10% tints of the same hues instead.
/// </summary>
public class PunchStatusToRowBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PartialBrush = new(Color.FromRgb(0xFE, 0xF3, 0xC7));
    private static readonly SolidColorBrush AbsentBrush = new(Color.FromRgb(0xFE, 0xE2, 0xE2));
    private static readonly SolidColorBrush LeaveBrush = new(Color.FromRgb(0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush OfficialBusinessBrush = new(Color.FromRgb(0xF3, 0xEE, 0xFF));

    // ~10% tint of RestDayBrush (0x0D9488) from PunchStatusToBrushConverter --
    // 10% teal blended with 90% white, the same blend-toward-white ratio the
    // four tints above approximate by eye.
    private static readonly SolidColorBrush RestDayBrush = new(Color.FromRgb(0xE7, 0xF4, 0xF3));

    // Complete's "plain default background" (see doc comment above) needs to actually be
    // the theme's default background, not a hardcoded light-only white -- resolved fresh
    // on every Convert() call rather than cached in a static field, same reasoning as
    // CalendarDayToBrushConverter's own EmptyBrush.
    private static SolidColorBrush DefaultBrush =>
        Application.Current?.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush
        ?? Brushes.White;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            // Complete is the expected, unremarkable case -- left at the
            // normal row background so only the statuses actually worth a
            // second look (Partial/Absent/Leave/Official Business/Rest Day)
            // draw the eye.
            PunchStatus.Partial => PartialBrush,
            PunchStatus.Absent => AbsentBrush,
            PunchStatus.Leave => LeaveBrush,
            PunchStatus.OfficialBusiness => OfficialBusinessBrush,
            PunchStatus.RestDay => RestDayBrush,
            _ => DefaultBrush,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
