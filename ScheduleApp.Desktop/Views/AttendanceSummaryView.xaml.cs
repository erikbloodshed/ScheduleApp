using System.Windows.Controls;
using System.Windows.Input;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

public partial class AttendanceSummaryView : UserControl
{
    public AttendanceSummaryView()
    {
        InitializeComponent();
    }

    /// <summary>Right-click on a Summary grid row -- the same "Add Manual Entry…"
    /// feature the Schedule page's calendar already offers on a right-clicked tile
    /// (see MonthCalendarControl.DayBorder_MouseRightButtonDown/BuildDayContextMenu),
    /// just for a Summary row instead of a calendar day. Built here in code-behind for
    /// the same reason that one is: a XAML-declared ContextMenu is a separate visual
    /// tree with no DataContext of its own to inherit, so it can't reach both "the row
    /// that was clicked" (for CommandParameter) and "the page's own AttendanceViewModel"
    /// (for Command) without exactly this kind of PlacementTarget/Tag tunnel -- reading
    /// both directly off the sender and this control's own DataContext here is simpler.
    /// Wired up via an EventSetter on the Summary DataGrid's own RowStyle in
    /// AttendanceSummaryView.xaml, so it fires for every row, not just ones the person
    /// happens to have selected first.
    ///
    /// Only ever offers "Add Manual Entry…" for a row whose Status is Partial or
    /// Absent -- same restriction, and same reasoning (nothing else has a missing
    /// punch worth filling in), as the calendar's identical item. Unlike
    /// BuildDayContextMenu, there's no "Set Schedule As"/"Remove Schedule" section this
    /// item sits alongside -- the Summary grid is read-only, a report result rather
    /// than something to edit directly -- so a row outside that status pair simply
    /// gets no context menu at all, rather than one with nothing useful in it.</summary>
    private void SummaryRow_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: AttendanceSummaryRow row } gridRow) return;
        if (DataContext is not AttendanceViewModel viewModel) return;

        var menu = new ContextMenu();

        // Same Partial-or-Absent restriction, and same reasoning (nothing else has a
        // missing punch worth filling in), as the calendar's identical item.
        if (row.Status is PunchStatus.Partial or PunchStatus.Absent)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Add Manual Entry…",
                Command = viewModel.AddManualEntryForRowCommand,
                CommandParameter = row,
            });
        }

        // Flexible only -- that's the type whose pairing is purely time-order and
        // therefore the one a missed or duplicated tap actually breaks (see
        // AttendanceCalculator.CalculateShift's pairingOverride parameter, which ignores
        // an override for any other type). Offered at any status, not just Partial: a day
        // that already reads Complete can still be paired wrongly (two taps merged into
        // one interval that should have been two), and re-pairing is also how someone
        // undoes an earlier edit. TypeText, not a ScheduleType field -- AttendanceSummaryRow
        // is a display-only projection and carries the label, not the enum (see that class).
        if (row.TypeText == ScheduleType.Flexible.ToText())
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Edit Punch Pairing…",
                Command = viewModel.EditPunchPairingForRowCommand,
                CommandParameter = row,
            });
        }

        // A row with nothing to offer gets no menu at all, rather than an empty one --
        // the Summary grid is a report result, not something to edit directly.
        if (menu.Items.Count == 0) return;

        gridRow.ContextMenu = menu;
        gridRow.ContextMenu.PlacementTarget = gridRow;
        gridRow.ContextMenu.IsOpen = true;
        e.Handled = true;
    }
}
