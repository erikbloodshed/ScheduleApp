using System.Windows;
using System.Windows.Controls;

namespace ScheduleApp.Desktop.Controls;

/// <summary>How much room a <see cref="TimeInput"/> takes up.</summary>
public enum TimeInputDensity
{
    /// <summary>A roomy field with larger text, for where a time is the point of the screen.</summary>
    Comfortable,

    /// <summary>One ordinary form-field line, which is what fits in a dense dialog like
    /// ApplyScheduleDialog (two of these on a row).</summary>
    Compact,
}

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
///   - <see cref="Density"/>, <see cref="Use24HourClock"/> and <see cref="FocusHour"/>.
/// </summary>
public partial class TimeInput : UserControl
{
    /// <summary>True while the editor is being written from <see cref="SelectedTime"/>, so its
    /// DateTimeChanged doesn't write the same value straight back.</summary>
    private bool _syncingEditorFromValue;

    public TimeInput()
    {
        InitializeComponent();
        ApplyDensity();
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

    public static readonly DependencyProperty DensityProperty = DependencyProperty.Register(
        nameof(Density),
        typeof(TimeInputDensity),
        typeof(TimeInput),
        new PropertyMetadata(TimeInputDensity.Comfortable, (d, _) => ((TimeInput)d).ApplyDensity()));

    /// <summary>Comfortable (the default) or Compact -- see <see cref="TimeInputDensity"/>.</summary>
    public TimeInputDensity Density
    {
        get => (TimeInputDensity)GetValue(DensityProperty);
        set => SetValue(DensityProperty, value);
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

    /// <summary>The time of day the editor holds, to the minute -- the editor carries a date
    /// too (today's), which a time field ignores.</summary>
    private static TimeOnly? TimeOf(DateTime? value) =>
        value is { } dateTime ? new TimeOnly(dateTime.Hour, dateTime.Minute) : null;

    private void ApplyDensity()
    {
        var compact = Density == TimeInputDensity.Compact;
        Editor.MinWidth = compact ? 120 : 160;
        Editor.FontSize = compact ? 14 : 18;
        Editor.MinHeight = compact ? 28 : 40;
    }

    private void ApplyClockConvention() => Editor.CustomPattern = Use24HourClock ? "HH:mm" : "h:mm tt";
}
