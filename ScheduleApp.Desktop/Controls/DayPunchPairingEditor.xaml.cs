using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Converters;
using ScheduleApp.Desktop.Reactive;
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
/// All this code-behind does is turn gestures into ViewModel calls -- the move and
/// the recompute that follows it live in the ViewModel.
/// </summary>
public partial class DayPunchPairingEditor
{
    private static readonly Brush DefaultSlotBorder = Frozen(Color.FromRgb(0xE2, 0xE5, 0xEA));
    private static readonly Brush HoverSlotBorder = Frozen(Color.FromRgb(0x2D, 0x6C, 0xDF));
    private static readonly Brush HoverSlotFill = Frozen(Color.FromArgb(0x22, 0x2D, 0x6C, 0xDF));

    private Point _pressPosition;
    private DayPunchPairingCellViewModel? _dragCandidate;

    public DayPunchPairingEditor()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            this.OneWayBind(ViewModel, vm => vm.EmployeeName, v => v.EmployeeNameText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.DateText, v => v.DateText.Text).DisposeWith(d);

            // Every toolbar button changes the grid, so the strip is gone in a read-only open.
            this.OneWayBind(ViewModel, vm => vm.IsReadOnly, v => v.Toolbar.Visibility, readOnly => VisibleWhen(!readOnly)).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.AddRowCommand, v => v.AddSegmentButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RemoveEmptySegmentsCommand, v => v.RemoveEmptyButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.UndoCommand, v => v.UndoButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Rows, v => v.RowsList.ItemsSource).DisposeWith(d);

            // Footer: the live preview where the pairing is read back, the punch count and a
            // note saying why there's no verdict everywhere else.
            this.OneWayBind(ViewModel, vm => vm.PairingAffectsResult, v => v.PreviewFooter.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PairingAffectsResult, v => v.PunchCountFooter.Visibility, affects => VisibleWhen(!affects)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchCountText, v => v.PunchCountText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PairingNote, v => v.PairingNoteText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.WorkedText, v => v.WorkedText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RequiredText, v => v.RequiredText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RemainderLabel, v => v.RemainderLabel.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RemainderText, v => v.RemainderText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PreviewStatusText, v => v.PreviewStatusText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PreviewStatus, v => v.PreviewStatusText.Foreground, status => PunchStatusToBrushConverter.For(status)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PreviewStatus, v => v.StatusDot.Fill, status => PunchStatusToBrushConverter.For(status)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.UnpairedCount, v => v.UnpairedText.Text,
                count => string.Format(CultureInfo.CurrentCulture, "{0} punch still unpaired", count)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HasUnpairedPunches, v => v.UnpairedText.Visibility, VisibleWhen).DisposeWith(d);
        });
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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
        if (ViewModel is not { IsReadOnly: false } viewModel)
            return;

        var slot = ResolveSlot(sender);

        // Double-click mirrors the slot's right-click menu: empty -> "Add Manual Punch…", a
        // manual entry -> "Edit Time…". A device punch's time can't be edited, so
        // double-clicking one falls through to the drag tracking below. Handled here rather
        // than via MouseDoubleClick, which is declared on Control (a Border is a Decorator).
        // Through the commands' ICommand.Execute, which reports a failure rather than
        // throwing -- and doesn't await, so this handler, also the start of the drag gesture,
        // stays synchronous.
        if (e.ClickCount == 2 && slot is { } target)
        {
            var cell = target.Row[target.Slot];

            if (cell is null)
            {
                _dragCandidate = null;
                e.Handled = true;
                ((ICommand)viewModel.AddManualPunchCommand).Execute(target);
                return;
            }

            if (cell.IsManual)
            {
                _dragCandidate = null;
                e.Handled = true;
                ((ICommand)viewModel.EditManualPunchCommand).Execute(cell);
                return;
            }
        }

        // Remember what's under the pointer, but don't start a drag yet -- a plain click
        // shouldn't move anything. Slot_PreviewMouseMove promotes this to a real drag once the
        // pointer has travelled past the drag threshold.
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
    /// Right-click a slot: type a missing punch into an empty one, or correct/remove one that
    /// was typed before. A device punch offers only a disabled note saying why nothing can be
    /// done to it -- AttendanceLogs stays an untouched record of what the clock reported --
    /// rather than a menu that silently didn't open. A "View Punches…" open is read-only
    /// throughout, so the menu is just that one note.
    ///
    /// Built here, like MonthCalendarControl.BuildDayContextMenu, because a XAML ContextMenu
    /// is a separate visual tree with no DataContext to inherit; rebuilt on every click so the
    /// items match what's in the slot now.
    /// </summary>
    private void Slot_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel || sender is not Border border) return;
        if (ResolveSlot(sender) is not { } target) return;

        var menu = new ContextMenu();

        if (viewModel.IsReadOnly)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Viewing only — use “Add Manual Entry…” on the day to change a punch",
                IsEnabled = false,
            });
        }
        else if (target.Row[target.Slot] is not { } cell)
        {
            menu.Items.Add(new MenuItem { Header = "Add Manual Punch…", Command = viewModel.AddManualPunchCommand, CommandParameter = target });
        }
        else if (cell.IsManual)
        {
            menu.Items.Add(new MenuItem { Header = "Edit Time…", Command = viewModel.EditManualPunchCommand, CommandParameter = cell });
            menu.Items.Add(new MenuItem { Header = "Delete Manual Punch", Command = viewModel.DeleteManualPunchCommand, CommandParameter = cell });
        }
        else
        {
            menu.Items.Add(new MenuItem { Header = "Device punch — time can't be edited", IsEnabled = false });
        }

        border.ContextMenu = menu;
        menu.PlacementTarget = border;
        menu.IsOpen = true;
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
