using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Desktop.Converters;

/// <summary>
/// Lights up whichever status tile in the Summary tab's counts strip currently
/// narrows SummaryRowsView (see ReportViewModel.SelectedStatusFilter/
/// FilterSummaryRow) -- value is SelectedStatusFilter itself, parameter is the
/// one fixed PunchStatus this particular tile's wrapping Border stands for (set
/// via ConverterParameter in AttendanceView.xaml, one x:Static per tile, same
/// pattern as each tile's own CommandParameter). Returns Transparent for every
/// tile except the active one, so at most one tile is ever lit at a time.
///
/// Same slate tone as CardBorder's own BorderBrush (see AttendanceView.xaml),
/// deliberately -- an active filter should read as "this tile is now part of
/// the card's surface", not introduce a brand new color the six status tiles
/// didn't already use elsewhere.
/// </summary>
public class StatusFilterHighlightConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromRgb(0xE2, 0xE8, 0xF0));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PunchStatus selected && parameter is PunchStatus target && selected == target
            ? ActiveBrush
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
