using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// The two-column (Time In / Time Out), arbitrary-row punch grid behind the Day
/// Punch Pairing editor -- see <see cref="DayPunchPairingEditorViewModel"/> for
/// what the layout means and what saving it does.
///
/// The drag gesture is plain WPF <see cref="DragDrop"/>: a slot Border is both the
/// drag source (started from PreviewMouseMove once the pointer has passed the
/// system drag threshold with the left button down) and the drop target
/// (AllowDrop plus the Drop handler below). The Border's DataContext is the row it
/// belongs to and its Tag is "In"/"Out", which together identify the slot; the
/// dragged <see cref="DayPunchPairingCellViewModel"/> travels in the DataObject.
/// All this code-behind does is turn the gesture into a single
/// <see cref="DayPunchPairingEditorViewModel.MoveCell"/> call -- the swap itself,
/// and the recompute that follows it, live in the view model.
/// </summary>
public partial class DayPunchPairingEditor : UserControl
{
    private static readonly Brush DefaultSlotBorder = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEA));
    private static readonly Brush HoverSlotBorder = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
    private static readonly Brush HoverSlotFill = new SolidColorBrush(Color.FromArgb(0x22, 0x2D, 0x6C, 0xDF));

    private Point _pressPosition;
    private DayPunchPairingCellViewModel? _dragCandidate;

    public DayPunchPairingEditor()
    {
        InitializeComponent();
    }

    private DayPunchPairingEditorViewModel? ViewModel => DataContext as DayPunchPairingEditorViewModel;

    // The (row, slot) a given slot Border stands for -- its DataContext is the
    // row, its Tag ("In"/"Out") the column. Null only if a handler somehow fires
    // on something that isn't a wired-up slot Border (shouldn't happen).
    private static (DayPunchPairingRowViewModel Row, ColumnSlot Slot)? ResolveSlot(object sender) =>
        sender is Border { DataContext: DayPunchPairingRowViewModel row, Tag: string tag }
            ? (row, tag == "In" ? ColumnSlot.In : ColumnSlot.Out)
            : null;

    private void Slot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A "View Punches…" open is read-only: no drag, no double-click-to-add.
        // Bailing here is what stops both -- nothing downstream captures a drag
        // candidate or dispatches AddManualPunchAsync.
        if (ViewModel?.IsReadOnly == true)
            return;

        var slot = ResolveSlot(sender);

        // Double-click mirrors the two enabled items on this slot's own right-click
        // menu (see Slot_MouseRightButtonUp): empty -> "Add Manual Punch…", occupied
        // by a manual entry -> "Edit Time…". A device punch's time can't be edited
        // (same rule the menu enforces with its disabled note), so double-clicking
        // one is left alone -- it just falls through to the drag-candidate tracking
        // below, same as it always has.
        //
        // Handled here rather than via MouseDoubleClick because that event is
        // declared on Control, and a Border is a Decorator. Dispatched rather than
        // awaited inline so this handler stays synchronous: it's also the start of
        // the drag gesture, and an async void in that path would let a drag begin
        // against a slot the dialog is concurrently filling.
        if (e.ClickCount == 2 && ViewModel is not null && slot is { } target)
        {
            var cell = target.Row[target.Slot];

            if (cell is null)
            {
                _dragCandidate = null;
                e.Handled = true;
                _ = Dispatcher.InvokeAsync(() => ViewModel.AddManualPunchAsync(target.Row, target.Slot));
                return;
            }

            if (cell.IsManual)
            {
                _dragCandidate = null;
                e.Handled = true;
                _ = Dispatcher.InvokeAsync(() => ViewModel.EditManualPunchAsync(cell));
                return;
            }
        }

        // Remember what's under the pointer, but don't start a drag yet -- a plain
        // click shouldn't move anything. Slot_PreviewMouseMove promotes this to a
        // real drag once the pointer has travelled past the drag threshold.
        _pressPosition = e.GetPosition(null);
        _dragCandidate = slot is { } s ? s.Row[s.Slot] : null;
    }

    private void Slot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var moved = e.GetPosition(null) - _pressPosition;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var cell = _dragCandidate;
        _dragCandidate = null; // consumed -- don't re-enter DoDragDrop from its own nested message loop
        cell.IsBeingDragged = true;
        try
        {
            var data = new DataObject(typeof(DayPunchPairingCellViewModel), cell);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }
        finally
        {
            cell.IsBeingDragged = false;
        }
    }

    /// <summary>
    /// Right-click a slot: type a missing punch into an empty one, or correct/remove
    /// one that was typed before. A device punch offers only a disabled note saying
    /// why nothing can be done to it -- AttendanceLogs is meant to stay an untouched
    /// record of what the clock reported (see ManualAttendanceLog's own doc comment),
    /// and a menu that simply didn't open would leave someone wondering whether they
    /// had missed a gesture rather than telling them the rule. A "View Punches…" open
    /// is read-only throughout, so the menu is just that one disabled note.
    ///
    /// Built in code-behind rather than declared in XAML for the same reason
    /// MonthCalendarControl.BuildDayContextMenu is: a XAML ContextMenu is a separate
    /// visual tree with no DataContext to inherit, so reaching both the clicked slot
    /// and the view model means exactly this kind of PlacementTarget tunnel anyway.
    /// Rebuilt on every click so the items always match what's in the slot now.
    /// </summary>
    private void Slot_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || sender is not Border border) return;
        if (ResolveSlot(sender) is not { } target) return;

        var menu = new ContextMenu();

        if (ViewModel.IsReadOnly)
        {
            // Same "say why rather than open nothing" reasoning as the device-punch
            // note below -- a punch is changed from the day's own menu, not here.
            menu.Items.Add(new MenuItem
            {
                Header = "Viewing only — use “Add Manual Entry…” on the day to change a punch",
                IsEnabled = false,
            });
        }
        else
        {
            var cell = target.Row[target.Slot];

            if (cell is null)
            {
                var add = new MenuItem { Header = "Add Manual Punch…" };
                add.Click += async (_, _) => await ViewModel.AddManualPunchAsync(target.Row, target.Slot);
                menu.Items.Add(add);
            }
            else if (cell.IsManual)
            {
                var edit = new MenuItem { Header = "Edit Time…" };
                edit.Click += async (_, _) => await ViewModel.EditManualPunchAsync(cell);
                menu.Items.Add(edit);

                var delete = new MenuItem { Header = "Delete Manual Punch" };
                delete.Click += async (_, _) => await ViewModel.DeleteManualPunchAsync(cell);
                menu.Items.Add(delete);
            }
            else
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "Device punch — time can't be edited",
                    IsEnabled = false,
                });
            }
        }

        border.ContextMenu = menu;
        border.ContextMenu.PlacementTarget = border;
        border.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void Slot_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border && e.Data.GetDataPresent(typeof(DayPunchPairingCellViewModel)))
        {
            border.BorderBrush = HoverSlotBorder;
            border.Background = HoverSlotFill;
        }
    }

    private void Slot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DayPunchPairingCellViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Slot_DragLeave(object sender, DragEventArgs e) => ClearHover(sender);

    private void Slot_Drop(object sender, DragEventArgs e)
    {
        ClearHover(sender);

        if (e.Data.GetData(typeof(DayPunchPairingCellViewModel)) is not DayPunchPairingCellViewModel dragged)
            return;
        if (ViewModel is null || ResolveSlot(sender) is not { } target)
            return;

        ViewModel.MoveCell(dragged, target.Row, target.Slot);
        e.Handled = true;
    }

    private static void ClearHover(object sender)
    {
        if (sender is Border border)
        {
            border.BorderBrush = DefaultSlotBorder;
            border.Background = Brushes.Transparent;
        }
    }
}
