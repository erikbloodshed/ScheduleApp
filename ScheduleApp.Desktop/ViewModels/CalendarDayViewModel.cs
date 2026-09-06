using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

public partial class CalendarDayViewModel : ObservableObject
{
    public DateOnly Date { get; init; }
    public bool IsCurrentMonth { get; init; }

    /// <summary>Drives the red day-number styling for the Sunday column. Deliberately
    /// separate from Entry/DisplayText/Background -- this is about which day it is,
    /// not what's scheduled that day.</summary>
    public bool IsSunday => Date.DayOfWeek == DayOfWeek.Sunday;

    /// <summary>
    /// At most one entry per day now (enforced by a unique EmployeeId+Date index),
    /// so there's no override/priority to resolve like there used to be.
    /// </summary>
    public ScheduleEntry? Entry { get; init; }

    /// <summary>True when this calendar date is on file in the company-wide Holidays
    /// table (see Holiday/IHolidayRepository). Company-wide, not per-employee -- the
    /// same value on this date's cell regardless of which employee's schedule the
    /// calendar is currently showing. Shown as a red day number, like the Sunday
    /// column (see MonthCalendarControl.xaml's IsHoliday trigger). Set fresh every
    /// RebuildCalendar from ScheduleCalendarViewModel's own loaded holiday set;
    /// init-only like Entry, so a holiday toggle rebuilds the grid rather than
    /// mutating a cell in place.</summary>
    public bool IsHoliday { get; init; }

    /// <summary>The holiday's display label (e.g. "New Year's Day"), shown as the
    /// cell's tooltip -- empty string when IsHoliday is false (the tooltip is
    /// suppressed there via ToolTipService.IsEnabled).</summary>
    public string HolidayName { get; init; } = string.Empty;

    /// <summary>Set by MonthCalendarControl's click/drag selection; read by the
    /// "set/clear schedule for selection" commands on MainViewModel.</summary>
    [ObservableProperty]
    private bool isSelected;

    /// <summary>The schedule-vs-punches result for this day, for the little
    /// completion marker in the cell's bottom-left corner (see
    /// ScheduleCalendarViewModel.RefreshCalendarAttendanceStatusesAsync). Null
    /// means "don't draw a marker" -- either nothing's been computed yet, the
    /// day has no ScheduleEntry to compare against, or the day is Leave
    /// (already called out by the cell's own background color, see
    /// CalendarDayToBrushConverter, so a second marker for it would just be
    /// noise). Not a straight copy of the day's AttendanceSummary.Status: a
    /// Rest Day with a fulfilled duty is promoted to PunchStatus.Complete here
    /// even though its own Status stays PunchStatus.RestDay everywhere else
    /// (payroll, reports, Excel export) -- see
    /// RefreshCalendarAttendanceStatusesAsync's own doc comment.</summary>
    [ObservableProperty]
    private PunchStatus? attendanceStatus;

    public int DayNumber => Date.Day;

    public string DisplayText => Entry?.DisplayText ?? string.Empty;
}
