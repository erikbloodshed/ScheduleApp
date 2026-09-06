using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Converters;

public class CalendarDayToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0xDD, 0xEE, 0xFF));
    private static readonly SolidColorBrush LeaveBrush = new(Color.FromRgb(0xFF, 0xE0, 0xB2));
    private static readonly SolidColorBrush FlexibleBrush = new(Color.FromRgb(0xDC, 0xF5, 0xE0));
    private static readonly SolidColorBrush SplitShiftBrush = new(Color.FromRgb(0xB2, 0xE5, 0xEF));
    private static readonly SolidColorBrush OfficialBusinessBrush = new(Color.FromRgb(0xE5, 0xDB, 0xFF));

    // Pink, deliberately not a shade of the cyan already claimed by
    // SplitShiftBrush above -- the two need to read apart at a glance on the
    // same calendar grid, not just under a color picker.
    private static readonly SolidColorBrush RestDayBrush = new(Color.FromRgb(0xF8, 0xC6, 0xDC));
    private static readonly SolidColorBrush OutOfMonthBrush = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private static readonly SolidColorBrush EmptyBrush = Brushes.White;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CalendarDayViewModel day) return EmptyBrush;
        if (!day.IsCurrentMonth) return OutOfMonthBrush;

        // A holiday doesn't tint the cell -- it shows as a red day number (like the Sunday
        // column), see MonthCalendarControl.xaml's IsHoliday trigger. Only the schedule
        // entry drives the fill here.
        return day.Entry?.ScheduleType switch
        {
            ScheduleType.Leave => LeaveBrush,
            ScheduleType.Normal => NormalBrush,
            ScheduleType.Flexible => FlexibleBrush,
            ScheduleType.SplitShift => SplitShiftBrush,
            ScheduleType.OfficialBusiness => OfficialBusinessBrush,
            ScheduleType.RestDay => RestDayBrush,
            _ => EmptyBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
