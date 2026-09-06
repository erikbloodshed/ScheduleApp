using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Maps AttendanceSummary.Status to the same six colors already used for the
/// Complete/Partial/Absent/Leave/Official Business/Rest Day counts on the
/// Attendance tab's Results panel (see AttendanceView.xaml), so the per-row
/// Status text in the in-app Attendance Summary grid reads consistently with
/// those counts.
/// </summary>
public class PunchStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CompleteBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush PartialBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush AbsentBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush LeaveBrush = new(Color.FromRgb(0x47, 0x55, 0x69));
    private static readonly SolidColorBrush OfficialBusinessBrush = new(Color.FromRgb(0x7C, 0x3A, 0xED));

    // Teal -- a fresh hue rather than a shade already claimed by one of the
    // five above, since Rest Day is its own status, not a variant of any of
    // them (see PunchStatus.RestDay's own doc comment: it still reflects
    // whether the day was actually worked, unlike Leave/Official Business).
    private static readonly SolidColorBrush RestDayBrush = new(Color.FromRgb(0x0D, 0x94, 0x88));
    private static readonly SolidColorBrush DefaultBrush = Brushes.Black;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PunchStatus.Complete => CompleteBrush,
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
