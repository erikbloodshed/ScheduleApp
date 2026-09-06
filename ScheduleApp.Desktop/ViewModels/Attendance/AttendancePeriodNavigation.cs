namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Shared by ReportViewModel, PunchRecordsViewModel, and ManualEntriesViewModel's
/// own "◀"/"▶" period-nav buttons -- each tab keeps its own pair of date properties
/// (PeriodStart/PeriodEnd, LogViewStart/LogViewEnd, ManualEntriesStart/ManualEntriesEnd)
/// and its own reload trigger, but all three step through the exact same semi-monthly
/// cut-off shape, so the actual date math lives here once rather than three
/// near-identical copies drifting apart. Same day-16 split AttendanceViewModel's own
/// initialPeriodStart/initialPeriodEnd default uses.</summary>
internal static class AttendancePeriodNavigation
{
    /// <summary>Given any date, treats it as falling in the semi-monthly cut-off
    /// containing it (day &lt; 16 = 1st-15th, otherwise 16th-end-of-month) and returns the
    /// full Start/End pair for the cut-off immediately before or after that one. An
    /// arbitrary/manually-typed range is still handled sensibly -- reference only needs to
    /// fall somewhere in the month/half being stepped from, not land on an exact
    /// 1/15/16/EOM boundary itself.</summary>
    public static (DateTime Start, DateTime End) AdjacentCutoffPeriod(DateTime reference, bool forward)
    {
        bool isFirstHalf = reference.Day < 16;

        if (forward)
        {
            if (isFirstHalf)
            {
                int daysInMonth = DateTime.DaysInMonth(reference.Year, reference.Month);
                return (new DateTime(reference.Year, reference.Month, 16), new DateTime(reference.Year, reference.Month, daysInMonth));
            }

            DateTime nextMonth = reference.AddMonths(1);
            return (new DateTime(nextMonth.Year, nextMonth.Month, 1), new DateTime(nextMonth.Year, nextMonth.Month, 15));
        }

        if (isFirstHalf)
        {
            DateTime prevMonth = reference.AddMonths(-1);
            int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
            return (new DateTime(prevMonth.Year, prevMonth.Month, 16), new DateTime(prevMonth.Year, prevMonth.Month, daysInPrevMonth));
        }

        return (new DateTime(reference.Year, reference.Month, 1), new DateTime(reference.Year, reference.Month, 15));
    }
}
