using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.Grid;

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
    /// the row off the clicked element and the command off this control's own DataContext
    /// here is simpler. The grid's own PreviewMouseRightButtonDown, so it fires for every
    /// row, not just ones the person happens to have selected first; a click anywhere but
    /// on a data row (a header, the empty space below) finds no row and does nothing.
    ///
    /// Offers "Add Manual Entry…" only for a Partial/Absent row -- same restriction,
    /// and same reasoning (nothing else has a missing punch worth filling in), as the
    /// calendar's identical item -- plus a punch view/editor on every row: "Edit Punch
    /// Pairing…" for a Flexible row (re-pair by hand, saved); "Edit Punches…" for a
    /// non-Flexible row that's Partial/Absent, or Complete with a hand-entered punch in
    /// it (row.HasManualClockPunch) that may still need fixing; "View Punches…"
    /// (read-only) for anything else. Mirrors MonthCalendarControl.BuildDayContextMenu;
    /// see DayPunchPairingEditorViewModel.IsReadOnly for how the dialog decides its
    /// mode. TypeText, not a ScheduleType field -- AttendanceSummaryRow is a
    /// display-only projection and carries the label, not the enum.</summary>
    private void SummaryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource as DependencyObject) is not { DataContext: AttendanceSummaryRow row }) return;
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

        string punchHeader;
        if (row.TypeText == ScheduleType.Flexible.ToText())
            punchHeader = "Edit Punch Pairing…";
        else if (row.Status is PunchStatus.Partial or PunchStatus.Absent || row.HasManualClockPunch)
            punchHeader = "Edit Punches…";
        else
            punchHeader = "View Punches…";

        menu.Items.Add(new MenuItem
        {
            Header = punchHeader,
            Command = viewModel.EditPunchPairingForRowCommand,
            CommandParameter = row,
        });

        // The punch item is always added, so this never fires now -- kept as a guard
        // in case a future gate removes it.
        if (menu.Items.Count == 0) return;

        menu.PlacementTarget = SummaryGrid;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The grid row (VirtualizingCellsControl) the clicked element sits in, or null
    /// when the click wasn't on one.</summary>
    private static VirtualizingCellsControl? RowAt(DependencyObject? element)
    {
        while (element is not null and not VirtualizingCellsControl)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

        return element as VirtualizingCellsControl;
    }
}
