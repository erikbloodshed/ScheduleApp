using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Controls;

public partial class MonthCalendarControl : UserControl
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days),
        typeof(ObservableCollection<CalendarDayViewModel>),
        typeof(MonthCalendarControl),
        new PropertyMetadata(null));

    public ObservableCollection<CalendarDayViewModel> Days
    {
        get => (ObservableCollection<CalendarDayViewModel>)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    private bool _isDragging;
    private CalendarDayViewModel? _dragAnchor;

    public MonthCalendarControl()
    {
        InitializeComponent();
    }

    // Click on a specific day cell: plain click selects just that day, Ctrl+click
    // toggles it in/out of the current selection, Shift+click extends a range from
    // the first currently-selected day. Also starts a possible drag (see below).
    private void DayBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarDayViewModel day }) return;

        _isDragging = true;
        _dragAnchor = day;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            day.IsSelected = !day.IsSelected;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var anchor = Days?.FirstOrDefault(d => d.IsSelected) ?? day;
            SelectRange(anchor.Date, day.Date);
        }
        else
        {
            ClearSelection();
            day.IsSelected = true;
        }

        DaysList.CaptureMouse();
        e.Handled = true;
    }

    // Mouse capture is held by DaysList (not the individual Border under the pointer),
    // so dragging across cells is detected here via hit-testing rather than per-cell
    // MouseMove -- that's the only way to know which cell the pointer is over now.
    private void DaysList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragAnchor is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return; // don't range-drag while ctrl-toggling

        var day = HitTestDay(e.GetPosition(DaysList));
        if (day is not null)
            SelectRange(_dragAnchor.Date, day.Date);
    }

    private void DaysList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragAnchor = null;
        DaysList.ReleaseMouseCapture();
    }

    // Right-click brings up a menu for setting/editing/removing the schedule on
    // whatever's currently selected, same as the buttons above the calendar --
    // this is just a faster path to the same MainViewModel commands, not a
    // separate feature. Built in code rather than declared in XAML: a XAML
    // ContextMenu on a per-day DataTemplate would need its DataContext to reach
    // past the individual CalendarDayViewModel up to MainViewModel, which is
    // exactly the kind of binding ContextMenu (a separate visual tree) makes
    // awkward -- reading MainViewModel directly off this control's own
    // DataContext here is simpler and doesn't depend on any WPF NameScope quirks.
    private void DayBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarDayViewModel day } border) return;
        if (DataContext is not MainViewModel viewModel) return;

        // Right-clicking a day outside the current selection replaces the
        // selection with just that day, matching plain left-click -- so the menu
        // always acts on "the day(s) you're pointing at," never a stale leftover
        // selection. Right-clicking a day that's already part of a multi-day
        // selection leaves the whole selection alone, so the menu applies to all
        // of it (e.g. right-click one day of a Shift+click range to bulk-set it).
        if (!day.IsSelected)
        {
            ClearSelection();
            day.IsSelected = true;
        }

        border.ContextMenu = BuildDayContextMenu(viewModel, day);
        border.ContextMenu.PlacementTarget = border;
        border.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// "Set Schedule As" submenu (one item per ScheduleType, each pre-selecting
    /// that type in ApplyScheduleDialog -- see MainViewModel.SetScheduleForSelectionAsync's
    /// presetType), then the same Set/Edit and Remove actions as the buttons above
    /// the calendar, and finally -- only for a single Partial/Absent tile -- "Add
    /// Manual Entry…" (see MainViewModel.AddManualEntryForDayCommand). Rebuilt fresh on
    /// every right-click rather than cached, so SetScheduleButtonText's "Set..."/
    /// "Edit..." wording and every item's enabled state (via each command's own
    /// CanExecute) are always current for whatever's selected right now.
    ///
    /// day is the specific tile that was right-clicked (see DayBorder_MouseRightButtonDown
    /// above), not necessarily the only one selected -- right-clicking inside an existing
    /// multi-day selection leaves that whole selection alone, so "Add Manual Entry…" needs
    /// its own, separate single-day check (via Days, this control's own bound collection)
    /// rather than assuming day is the only thing IsSelected. No longer static, now that
    /// it reads Days directly instead of taking every piece of context as a parameter.
    /// </summary>
    private ContextMenu BuildDayContextMenu(MainViewModel viewModel, CalendarDayViewModel day)
    {
        var menu = new ContextMenu();

        var setAsMenu = new MenuItem { Header = "Set Schedule As" };
        foreach (var scheduleType in Enum.GetValues<ScheduleType>())
        {
            // Leave needs no extra data (no hours, no time-in, no segments), so
            // picking it here applies it immediately via its own dedicated command
            // instead of opening ApplyScheduleDialog just to immediately OK it with
            // nothing filled in -- see MainViewModel.SetLeaveForSelectionAsync. The
            // other three types still need the dialog (Normal/Official Business need
            // hours + time-in, Flexible needs a required-hours total), so they keep
            // going through SetScheduleForSelectionCommand with the type as a preset.
            var item = scheduleType == ScheduleType.Leave
                ? new MenuItem { Header = scheduleType.ToText(), Command = viewModel.SetLeaveForSelectionCommand }
                : new MenuItem
                {
                    Header = scheduleType.ToText(),
                    Command = viewModel.SetScheduleForSelectionCommand,
                    CommandParameter = scheduleType
                };
            setAsMenu.Items.Add(item);
        }
        menu.Items.Add(setAsMenu);

        menu.Items.Add(new Separator());

        menu.Items.Add(new MenuItem
        {
            Header = viewModel.SetScheduleButtonText,
            Command = viewModel.SetScheduleForSelectionCommand,
            CommandParameter = null
        });

        menu.Items.Add(new MenuItem
        {
            Header = "Remove Schedule for Selected Days",
            Command = viewModel.ClearScheduleForSelectionCommand
        });

        // Holiday items -- company-wide (see Holiday's own doc comment), so unlike every
        // item above they don't depend on an employee being selected; the calendar's
        // per-employee schedule view is just a convenient place to reach them from.
        // "Mark as Holiday…" is offered only for exactly one selected, not-yet-holiday day
        // -- a holiday needs its own name, and marking a whole range under one shared name
        // is rarely wanted (same one-at-a-time shape as ManageHolidaysDialog's Add). Any
        // multi-day holiday work goes through that dialog. "Remove Holiday(s)" has no such
        // limit (no name involved) and is offered whenever the selection includes at least
        // one holiday. Selected days outside the displayed month count for removal too, the
        // same way the schedule items above act on GetSelectedDates() wholesale.
        var selectedDays = Days?.Where(d => d.IsSelected).ToList() ?? [];
        var selectedHolidayCount = selectedDays.Count(d => d.IsHoliday);
        var canMark = selectedDays.Count == 1 && selectedHolidayCount == 0;
        var canRemove = selectedHolidayCount > 0;

        if (canMark || canRemove)
        {
            menu.Items.Add(new Separator());
            if (canMark)
                menu.Items.Add(new MenuItem
                {
                    Header = "Mark as Holiday…",
                    Command = viewModel.ToggleHolidayForSelectionCommand
                });
            if (canRemove)
                menu.Items.Add(new MenuItem
                {
                    Header = selectedHolidayCount > 1 ? "Remove Holidays for Selected Days" : "Remove Holiday",
                    Command = viewModel.ToggleHolidayForSelectionCommand
                });
        }

        // Grouped at the bottom alongside Set/Remove Schedule, per the improvement
        // plan's own menu-placement decision -- only offered for a single tile (no
        // single AttendanceStatus to key off across a multi-day selection) whose
        // computed status is Partial or Absent (see
        // MainViewModel.RefreshCalendarAttendanceStatusesAsync for how
        // AttendanceStatus gets set -- Complete/Leave/Official Business tiles, and any
        // tile before that method has run at all, leave this null, so both cases are
        // already covered by the same pattern match without listing them out).
        if (Days?.Count(d => d.IsSelected) == 1
            && day.AttendanceStatus is PunchStatus.Partial or PunchStatus.Absent
            && viewModel.SelectedEmployee is not null)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Add Manual Entry…",
                Command = viewModel.AddManualEntryForDayCommand,
                CommandParameter = day
            });
        }

        // Sits alongside "Add Manual Entry…" -- the two are the pair of answers to a
        // day whose punches don't add up: add the punch that's missing, or re-pair
        // the ones already there. Deliberately not gated on ScheduleType or
        // AttendanceStatus: the dialog behind this is also the only place a single
        // day's punches are laid out in order, in/out roles and device-vs-manual
        // badges included, so it's worth opening on any day -- to read the day as
        // much as to change it. What it can *do* varies, and the dialog says so
        // itself rather than this menu having to guess: only a Flexible day's pairing
        // is read back by the calculation (see AttendanceCalculator.CalculateShift's
        // pairingOverride parameter), so on every other type it drops its Save button
        // and its footer shows a punch count instead of a verdict -- see
        // DayPunchPairingEditorViewModel.PairingAffectsResult. Hence the two headers.
        //
        // Single-tile only (a pairing is per-day, so there's no bulk case), and
        // day.Entry -- the day's own ScheduleEntry, see CalendarDayViewModel -- must
        // be non-null: the editor is built from one (its search window, required
        // hours, and overtime/night-diff eligibility all come off it), so a day with
        // no schedule at all has nothing to open. Raw punches on such a day are still
        // reachable through the Attendance page's Punch Records grid.
        if (Days?.Count(d => d.IsSelected) == 1
            && day.Entry is not null
            && viewModel.SelectedEmployee is not null)
        {
            menu.Items.Add(new MenuItem
            {
                Header = day.Entry.ScheduleType == ScheduleType.Flexible
                    ? "Edit Punch Pairing…"
                    : "View Punches…",
                Command = viewModel.EditPunchPairingForDayCommand,
                CommandParameter = day
            });
        }

        return menu;
    }

    private void SelectRange(DateOnly a, DateOnly b)
    {
        if (Days is null) return;

        var start = a <= b ? a : b;
        var end = a <= b ? b : a;

        foreach (var day in Days)
            day.IsSelected = day.Date >= start && day.Date <= end;
    }

    private void ClearSelection()
    {
        if (Days is null) return;
        foreach (var day in Days)
            day.IsSelected = false;
    }

    private CalendarDayViewModel? HitTestDay(Point position)
    {
        var result = VisualTreeHelper.HitTest(DaysList, position);
        var element = result?.VisualHit as DependencyObject;

        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: CalendarDayViewModel day })
                return day;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }
}
