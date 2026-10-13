using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Syncfusion.Windows.Shared;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// Time-of-day entry: a Syncfusion DateTimeEdit holding only a time, as "9:30 AM". Each part
/// (hour, minute, AM/PM) is typed into or stepped with Up/Down, and the drop-down clock picks a
/// time with the mouse. The field is masked, so it can't hold something that isn't a time.
///
/// It replaces a hand-built Material-style hour/minute/AM-PM control and keeps that control's
/// API, which the dialogs using it (ApplyScheduleDialog, ManualLogEntryDialog,
/// PunchTimeEntryDialog, SettingsDialog) rely on:
///   - <see cref="SelectedTime"/> is a two-way DependencyProperty; null means "not filled in
///     yet", which is what those dialogs test for before saving, and setting null clears the
///     field. Any minute can be entered -- values loaded from the database show as they are.
///   - <see cref="SelectedTimeChanged"/> fires once per actual change, typed or assigned.
///   - <see cref="Use24HourClock"/> and <see cref="FocusHour"/>.
/// It is one ordinary form field, sized like every other (Themes/ControlDefaults.xaml).
///
/// A blank field starts at <see cref="BlankStartsAt"/> once it's edited. Left to itself the
/// editor has nothing to edit while blank: digits typed into it stayed as loose text that
/// never became a time, and its clock opened on the time of day the dialog happened to be
/// opened at, which OK then wrote into the field.
/// </summary>
public partial class TimeInput : UserControl
{
    /// <summary>Where a blank field starts when it's typed into or its clock is opened: the
    /// time in a new schedule starts with (ApplyScheduleViewModel).</summary>
    public static readonly TimeOnly BlankStartsAt = new(5, 0);

    /// <summary>True while the editor is being written from <see cref="SelectedTime"/>, so its
    /// DateTimeChanged doesn't write the same value straight back.</summary>
    private bool _syncingEditorFromValue;

    public TimeInput()
    {
        InitializeComponent();
        ApplyClockConvention();
    }

    /// <summary>Raised whenever <see cref="SelectedTime"/> changes, including when it moves
    /// between a real time and null. Fires for a programmatic assignment as well as for typing.</summary>
    public event EventHandler? SelectedTimeChanged;

    public static readonly DependencyProperty SelectedTimeProperty = DependencyProperty.Register(
        nameof(SelectedTime),
        typeof(TimeOnly?),
        typeof(TimeInput),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedTimeChanged));

    /// <summary>The time in the field, or null while it's blank. Setting null clears it.</summary>
    public TimeOnly? SelectedTime
    {
        get => (TimeOnly?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public static readonly DependencyProperty Use24HourClockProperty = DependencyProperty.Register(
        nameof(Use24HourClock),
        typeof(bool),
        typeof(TimeInput),
        new PropertyMetadata(false, (d, _) => ((TimeInput)d).ApplyClockConvention()));

    /// <summary>Shows and takes the hour as 0-23 with no AM/PM. Off by default: every time
    /// this app displays is 12-hour (see TimeDisplayFormat), and the stored value is a plain
    /// TimeOnly either way, so this only changes how the time is typed and shown.</summary>
    public bool Use24HourClock
    {
        get => (bool)GetValue(Use24HourClockProperty);
        set => SetValue(Use24HourClockProperty, value);
    }

    /// <summary>Puts keyboard focus in the field, on its hour, so a dialog can open with the
    /// time ready to type rather than the person having to click into it first.</summary>
    public void FocusHour() => Editor.Focus();

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var input = (TimeInput)d;
        var time = (TimeOnly?)e.NewValue;

        if (TimeOf(input.Editor.DateTime) != time)
        {
            input._syncingEditorFromValue = true;
            try
            {
                input.Editor.DateTime = time is { } t ? DateTime.Today.Add(t.ToTimeSpan()) : null;
            }
            finally
            {
                input._syncingEditorFromValue = false;
            }
        }

        input.SelectedTimeChanged?.Invoke(input, EventArgs.Empty);
    }

    private void Editor_DateTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (_syncingEditorFromValue) return;

        var time = TimeOf(Editor.DateTime);
        if (time != SelectedTime)
            SetCurrentValue(SelectedTimeProperty, time);
    }

    /// <summary>
    /// The first digit or Up/Down in a blank field: the field takes <see cref="BlankStartsAt"/>
    /// with its hour selected, so a digit goes on to replace the hour ("7", "30", "p" reads
    /// 7:30 PM, as it does in a field that already had a time) and an arrow shows the start
    /// itself rather than stepping away from a time nobody has seen yet. Any other key leaves
    /// a blank field blank -- tabbing through one doesn't fill it in.
    ///
    /// Here rather than on the editor, so it runs first: the key tunnels down through this
    /// control, and the editor marks an arrow handled (having done nothing with it, blank)
    /// before a handler of its own would see it. Not while the clock is open, whose keys come
    /// through here too.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (Editor.DateTime is not null || Editor.IsDropDownOpen || Keyboard.Modifiers != ModifierKeys.None)
            return;

        var isDigit = e.Key is (>= Key.D0 and <= Key.D9) or (>= Key.NumPad0 and <= Key.NumPad9);
        if (!isDigit && e.Key is not (Key.Up or Key.Down))
            return;

        Editor.DateTime = BlankStart();
        Editor.Select(0, 1);
        e.Handled = !isDigit;
    }

    /// <summary>The clock opened on a blank field shows <see cref="BlankStartsAt"/> instead
    /// of the editor's own stand-in for "no time", the current one. Only the clock is set: OK
    /// takes what it shows, and Cancel or Escape leaves the field blank.</summary>
    private void Editor_ClockPopupOpened(object sender, RoutedEventArgs e)
    {
        if (Editor.DateTime is not null)
            return;

        if (Editor.Template?.FindName("PART_Popup", Editor) is Popup { Child: { } content } && ClockIn(content) is { } clock)
            clock.DateTime = BlankStart();
    }

    private static DateTime BlankStart() => DateTime.Today.Add(BlankStartsAt.ToTimeSpan());

    private static Clock? ClockIn(DependencyObject root)
    {
        if (root is Clock clock)
            return clock;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (ClockIn(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>The time of day the editor holds, to the minute -- the editor carries a date
    /// too (today's), which a time field ignores.</summary>
    private static TimeOnly? TimeOf(DateTime? value) =>
        value is { } dateTime ? new TimeOnly(dateTime.Hour, dateTime.Minute) : null;

    private void ApplyClockConvention() => Editor.CustomPattern = Use24HourClock ? "HH:mm" : "h:mm tt";
}
