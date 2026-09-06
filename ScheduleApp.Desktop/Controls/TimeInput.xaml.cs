using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScheduleApp.Desktop.Controls;

/// <summary>How much room a <see cref="TimeInput"/> takes up.</summary>
public enum TimeInputDensity
{
    /// <summary>Material's own proportions - big 96x64 number fields with "Hour"/"Minute"
    /// captions under them and the AM/PM segments stacked vertically alongside. Roughly
    /// 270x90; use it where a time is the point of the screen.</summary>
    Comfortable,

    /// <summary>The same control shrunk to one form-field line - 48x40 number fields, no
    /// captions, AM/PM segments side by side. Roughly 190x40, which is what fits in a
    /// dense dialog like ApplyScheduleDialog (fixed 560x600, two of these on a row).</summary>
    Compact,
}

/// <summary>
/// Time-of-day entry modelled on Material 3's "time input" picker
/// (https://m3.material.io/components/time-pickers/specs): an hour field and a minute
/// field either side of a colon, with an AM/PM segmented toggle. Material's geometry and
/// behavior, drawn in this app's own WPF-UI theme brushes rather than an M3 palette, so it
/// sits next to the Fluent controls around it and follows a theme change.
///
/// Replaces the older three-ComboBox TimePicker, and differs from it in three ways that
/// matter to callers:
///   - <see cref="SelectedTime"/> is a real DependencyProperty (two-way by default), so it
///     can be bound rather than only read from code-behind.
///   - Any minute can be entered, not just :00/:30. The old control silently snapped
///     whatever it was given to the nearest half hour, including values loaded from the
///     database; this one shows what it was given.
///   - Up/Down and the mouse wheel step the value -- the hour by one, the minute to the
///     next whole multiple of <see cref="MinuteStep"/> (5 by default, matching Material's
///     dial). Typing is not restricted to those multiples.
///
/// Null still means "not filled in yet" exactly as it did before, which is what callers
/// like ApplyScheduleDialog test for before saving.
/// </summary>
public partial class TimeInput : UserControl
{
    /// <summary>Corner rounding on the outer AM/PM shell. The segments inside get this
    /// minus the shell's 1px stroke, so their fill doesn't poke out past the curve.</summary>
    private const double PeriodCornerRadius = 8d;

    /// <summary>True while <see cref="SelectedTime"/> is being recomputed from what's in
    /// the fields (i.e. someone is typing), so the property's change handler leaves the
    /// fields alone instead of rewriting the text mid-keystroke.</summary>
    private bool _syncingValueFromFields;

    /// <summary>Mirror image of the above: true while the fields are being written from
    /// <see cref="SelectedTime"/>, so the TextChanged/Checked handlers don't parse their
    /// own output straight back.</summary>
    private bool _syncingFieldsFromValue;

    public TimeInput()
    {
        InitializeComponent();

        DataObject.AddPastingHandler(HourBox, OnPaste);
        DataObject.AddPastingHandler(MinuteBox, OnPaste);

        ApplyDensity();
        ApplyClockConvention();
    }

    /// <summary>Raised whenever <see cref="SelectedTime"/> changes, including when it moves
    /// between a real time and null (see that property for what null means). Fires for a
    /// programmatic assignment as well as for typing, and once per actual change rather
    /// than once per field touched.</summary>
    public event EventHandler? SelectedTimeChanged;

    public static readonly DependencyProperty SelectedTimeProperty = DependencyProperty.Register(
        nameof(SelectedTime),
        typeof(TimeOnly?),
        typeof(TimeInput),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedTimeChanged));

    /// <summary>The composed time, or null whenever the control doesn't currently hold one
    /// -- an empty or out-of-range hour or minute, or (in 12-hour mode) no AM/PM picked
    /// yet. Callers rely on that null to mean "not set", so a half-typed value reads as
    /// unset rather than as some arbitrary in-between time. Setting null clears every
    /// field back to blank.</summary>
    public TimeOnly? SelectedTime
    {
        get => (TimeOnly?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public static readonly DependencyProperty MinuteStepProperty = DependencyProperty.Register(
        nameof(MinuteStep),
        typeof(int),
        typeof(TimeInput),
        new PropertyMetadata(5),
        step => step is int and >= 1 and <= 30);

    /// <summary>How far Up/Down and the mouse wheel move the minute field: to the next
    /// whole multiple of this, so 9:07 stepped up by the default 5 lands on 9:10 rather
    /// than 9:12. Typed entry is deliberately not held to it -- an exact 9:07 is still
    /// enterable, the step only decides where the shortcut keys stop.</summary>
    public int MinuteStep
    {
        get => (int)GetValue(MinuteStepProperty);
        set => SetValue(MinuteStepProperty, value);
    }

    public static readonly DependencyProperty DensityProperty = DependencyProperty.Register(
        nameof(Density),
        typeof(TimeInputDensity),
        typeof(TimeInput),
        new PropertyMetadata(TimeInputDensity.Comfortable, OnDensityChanged));

    /// <summary>Comfortable (the default, Material's own size) or Compact. See
    /// <see cref="TimeInputDensity"/> for the measurements of each.</summary>
    public TimeInputDensity Density
    {
        get => (TimeInputDensity)GetValue(DensityProperty);
        set => SetValue(DensityProperty, value);
    }

    public static readonly DependencyProperty Use24HourClockProperty = DependencyProperty.Register(
        nameof(Use24HourClock),
        typeof(bool),
        typeof(TimeInput),
        new PropertyMetadata(false, OnUse24HourClockChanged));

    /// <summary>Hides the AM/PM toggle and takes the hour as 0-23 instead of 1-12. Off by
    /// default: every time this app displays is 12-hour (see TimeDisplayFormat), and the
    /// stored value is a plain 24-hour TimeOnly either way, so this only changes how the
    /// hour is typed and shown.</summary>
    public bool Use24HourClock
    {
        get => (bool)GetValue(Use24HourClockProperty);
        set => SetValue(Use24HourClockProperty, value);
    }

    private static readonly DependencyPropertyKey IsTimeValidPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsTimeValid),
            typeof(bool),
            typeof(TimeInput),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsTimeValidProperty = IsTimeValidPropertyKey.DependencyProperty;

    /// <summary>False while a field holds something that can't be a time (an hour of 13, a
    /// minute of 75), which is also what turns the field red. Note this is not the same
    /// question as "is there a time yet": a blank control is perfectly valid and still has
    /// a null <see cref="SelectedTime"/>.</summary>
    public bool IsTimeValid => (bool)GetValue(IsTimeValidProperty);

    /// <summary>Internal plumbing for this control's own field style -- set on each field's
    /// Border to switch it to the error look. Attached rather than plain because a Style
    /// trigger can only test a property that lives on the element it targets.</summary>
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError",
        typeof(bool),
        typeof(TimeInput),
        new PropertyMetadata(false));

    public static bool GetHasError(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (bool)element.GetValue(HasErrorProperty);
    }

    public static void SetHasError(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(HasErrorProperty, value);
    }

    /// <summary>Puts keyboard focus on the hour field, so a dialog can open with the time
    /// ready to type rather than the person having to click into it first.</summary>
    public void FocusHour() => HourBox.Focus();

    // --- composing the value from the fields -------------------------------------------

    private void UpdateSelectedTimeFromFields()
    {
        _syncingValueFromFields = true;
        try
        {
            SetCurrentValue(SelectedTimeProperty, ComposeTime());
        }
        finally
        {
            _syncingValueFromFields = false;
        }

        RefreshErrorState();
    }

    /// <summary>The three parts read back as one time, or null if any of them isn't usable
    /// yet -- see <see cref="SelectedTime"/> for why that's null rather than a guess.</summary>
    private TimeOnly? ComposeTime()
    {
        if (!TryReadHour(out var hour) || !TryReadMinute(out var minute))
            return null;

        if (Use24HourClock)
            return new TimeOnly(hour, minute);

        if (AmButton.IsChecked != true && PmButton.IsChecked != true)
            return null;

        // 12 is the odd one out at both ends: 12 AM is hour 0, 12 PM is hour 12.
        var hour24 = PmButton.IsChecked == true
            ? (hour == 12 ? 12 : hour + 12)
            : (hour == 12 ? 0 : hour);

        return new TimeOnly(hour24, minute);
    }

    private bool TryReadHour(out int hour)
    {
        hour = 0;

        if (!int.TryParse(HourBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;

        hour = parsed;
        return Use24HourClock ? parsed is >= 0 and <= 23 : parsed is >= 1 and <= 12;
    }

    private bool TryReadMinute(out int minute)
    {
        minute = 0;

        if (!int.TryParse(MinuteBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;

        minute = parsed;
        return parsed is >= 0 and <= 59;
    }

    private void UpdateFieldsFromSelectedTime()
    {
        _syncingFieldsFromValue = true;
        try
        {
            if (SelectedTime is not { } time)
            {
                HourBox.Text = string.Empty;
                MinuteBox.Text = string.Empty;

                // Both explicitly, since unchecking one of a pair doesn't check the other.
                AmButton.IsChecked = false;
                PmButton.IsChecked = false;
                return;
            }

            var hour = Use24HourClock
                ? time.Hour
                : (time.Hour % 12 == 0 ? 12 : time.Hour % 12);

            HourBox.Text = hour.ToString("00", CultureInfo.InvariantCulture);
            MinuteBox.Text = time.Minute.ToString("00", CultureInfo.InvariantCulture);
            AmButton.IsChecked = time.Hour < 12;
            PmButton.IsChecked = time.Hour >= 12;
        }
        finally
        {
            _syncingFieldsFromValue = false;
        }

        RefreshErrorState();
    }

    private void RefreshErrorState()
    {
        var hourError = HasFieldError(HourBox, TryReadHour(out _));
        var minuteError = HasFieldError(MinuteBox, TryReadMinute(out _));

        SetHasError(HourField, hourError);
        SetHasError(MinuteField, minuteError);
        SetValue(IsTimeValidPropertyKey, !hourError && !minuteError);
    }

    /// <summary>Whether a field should be shown as wrong. A single digit is left alone even
    /// when it isn't a valid value on its own, because it can still be the start of one
    /// ("0" on its way to "09"); it only counts as an error once there's a second digit
    /// behind it or the person has moved off the field and left it that way.</summary>
    private static bool HasFieldError(TextBox box, bool isValid)
    {
        if (isValid || box.Text.Length == 0)
            return false;

        return box.Text.Length >= 2 || !box.IsKeyboardFocused;
    }

    // --- stepping ----------------------------------------------------------------------

    /// <summary>Steps the whole time rather than just the hour digits, so 11 AM stepped up
    /// lands on 12 PM and midnight stepped down lands on 11 PM -- the AM/PM toggle follows
    /// along on its own instead of needing its own wrap-around rule.</summary>
    private void StepHour(int direction) => CommitStep(StepStartingPoint().AddHours(direction));

    /// <summary>Moves to the next (or previous) whole multiple of <see cref="MinuteStep"/>,
    /// carrying into the hour when it passes the top of one.</summary>
    private void StepMinute(int direction)
    {
        var time = StepStartingPoint();
        var step = MinuteStep;
        var offset = time.Minute % step;

        var delta = direction > 0
            ? step - offset
            : -(offset == 0 ? step : offset);

        CommitStep(time.AddMinutes(delta));
    }

    /// <summary>Midnight when there's no time yet, so Up on an empty control is a quick way
    /// to a first value instead of doing nothing.</summary>
    private TimeOnly StepStartingPoint() => SelectedTime ?? TimeOnly.MinValue;

    private void CommitStep(TimeOnly value)
    {
        // The fields are rewritten by OnSelectedTimeChanged, which then leaves the caret at
        // the end of the text -- reselect so a second step (or typing over it) still acts
        // on the whole field.
        SetCurrentValue(SelectedTimeProperty, value);

        if (HourBox.IsKeyboardFocused)
            HourBox.SelectAll();
        else if (MinuteBox.IsKeyboardFocused)
            MinuteBox.SelectAll();
    }

    // --- field event handlers ----------------------------------------------------------

    private void OnFieldTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingFieldsFromValue) return;

        UpdateSelectedTimeFromFields();

        // Material's auto-advance: a complete hour hands the caret to the minutes, so a
        // time is four keystrokes with no Tab in the middle.
        if (ReferenceEquals(sender, HourBox) &&
            HourBox.IsKeyboardFocused &&
            HourBox.Text.Length == 2 &&
            TryReadHour(out _))
        {
            MinuteBox.Focus();
        }
    }

    private void OnFieldPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;

        // Typing the separator is the other way to cross from hours to minutes.
        if (ReferenceEquals(box, HourBox) && e.Text is ":")
        {
            MinuteBox.Focus();
            e.Handled = true;
            return;
        }

        if (!IsDigits(e.Text) || !Fits(box, e.Text))
            e.Handled = true;
    }

    private void OnFieldPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEnabled) return;

        var isHour = ReferenceEquals(sender, HourBox);

        switch (e.Key)
        {
            case Key.Up:
                if (isHour) StepHour(1); else StepMinute(1);
                e.Handled = true;
                return;

            case Key.Down:
                if (isHour) StepHour(-1); else StepMinute(-1);
                e.Handled = true;
                return;

            // Would be rejected by OnFieldPreviewTextInput as " " anyway; blocking the key
            // outright keeps it from arriving as a keystroke at all.
            case Key.Space:
                e.Handled = true;
                return;

            default:
                return;
        }
    }

    /// <summary>Wheel stepping, but only once the field already has keyboard focus. These
    /// controls sit inside ApplyScheduleDialog's ScrollViewer, and a wheel handler that
    /// fired on hover alone would let someone scrolling the form past a picker silently
    /// change a schedule's time on the way.</summary>
    private void OnFieldPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var box = (TextBox)sender;

        if (!IsEnabled || !box.IsKeyboardFocused || e.Delta == 0) return;

        var direction = e.Delta > 0 ? 1 : -1;

        if (ReferenceEquals(box, HourBox)) StepHour(direction); else StepMinute(direction);

        e.Handled = true;
    }

    /// <summary>A click into an unfocused field would place the caret and leave nothing
    /// selected, defeating the select-all below. Swallowing that first click and focusing
    /// by hand lets GotKeyboardFocus do the selecting; once focused, clicks behave
    /// normally.</summary>
    private void OnFieldPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;

        if (!IsEnabled || box.IsKeyboardFocusWithin) return;

        e.Handled = true;
        box.Focus();
    }

    private void OnFieldGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ((TextBox)sender).SelectAll();

        // Focus is one of the two things HasFieldError looks at: a lone "0" stops being an
        // error again while it's being typed into.
        RefreshErrorState();
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        PadField((TextBox)sender);
        RefreshErrorState();
    }

    /// <summary>Zero-pads a single digit left behind on a field, so "9:5" settles as
    /// "09:05" rather than staying ragged next to the other field.</summary>
    private void PadField(TextBox box)
    {
        if (box.Text.Length != 1) return;

        var isValid = ReferenceEquals(box, HourBox) ? TryReadHour(out _) : TryReadMinute(out _);
        if (!isValid) return;

        _syncingFieldsFromValue = true;
        try
        {
            box.Text = "0" + box.Text;
        }
        finally
        {
            _syncingFieldsFromValue = false;
        }
    }

    private void OnPeriodChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingFieldsFromValue) return;

        UpdateSelectedTimeFromFields();
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;

        var pasted = e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)
            ? e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
            : null;

        // A non-text payload (an image, a file drop) has nothing sensible to insert, so
        // it's refused outright rather than left to WPF's own default handling.
        if (pasted is null || !IsDigits(pasted) || !Fits(box, pasted))
            e.CancelCommand();
    }

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    /// <summary>Whether <paramref name="input"/> still leaves the field two digits or
    /// fewer once it has replaced whatever is selected. MaxLength would truncate instead,
    /// which silently drops half a pasted value.</summary>
    private static bool Fits(TextBox box, string input) =>
        box.Text.Length - box.SelectionLength + input.Length <= 2;

    // --- layout ------------------------------------------------------------------------

    /// <summary>The whole of the difference between the two densities. Sizes live here
    /// rather than in the XAML because the AM/PM segments also change orientation, which a
    /// style trigger can't express as cleanly as six lines of grid setup.</summary>
    private void ApplyDensity()
    {
        var compact = Density == TimeInputDensity.Compact;

        var fieldWidth = compact ? 48d : 96d;
        var fieldHeight = compact ? 40d : 64d;
        var fontSize = compact ? 15d : 32d;
        var inner = PeriodCornerRadius - 1d;

        HourField.Width = MinuteField.Width = fieldWidth;
        HourField.Height = MinuteField.Height = fieldHeight;
        HourBox.FontSize = MinuteBox.FontSize = fontSize;

        TimeSeparator.FontSize = fontSize;
        TimeSeparator.Margin = new Thickness(compact ? 4d : 8d, 0, compact ? 4d : 8d, 0);

        HourLabel.Visibility = MinuteLabel.Visibility =
            compact ? Visibility.Collapsed : Visibility.Visible;

        PeriodField.Margin = new Thickness(compact ? 8d : 12d, 0, 0, 0);
        PeriodField.Height = fieldHeight;
        PeriodField.Width = compact ? 76d : 52d;

        AmButton.FontSize = PmButton.FontSize = compact ? 12d : 14d;

        PeriodLayout.RowDefinitions.Clear();
        PeriodLayout.ColumnDefinitions.Clear();

        if (compact)
        {
            // AM | PM
            PeriodLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PeriodLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PeriodLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            PlaceSegment(AmButton, row: 0, column: 0);
            PlaceSegment(PeriodDivider, row: 0, column: 1);
            PlaceSegment(PmButton, row: 0, column: 2);

            PeriodDivider.Width = 1d;
            PeriodDivider.Height = double.NaN;
            PeriodDivider.HorizontalAlignment = HorizontalAlignment.Center;
            PeriodDivider.VerticalAlignment = VerticalAlignment.Stretch;

            AmButton.Tag = new CornerRadius(inner, 0, 0, inner);
            PmButton.Tag = new CornerRadius(0, inner, inner, 0);
        }
        else
        {
            // AM
            // --
            // PM
            PeriodLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PeriodLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PeriodLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            PlaceSegment(AmButton, row: 0, column: 0);
            PlaceSegment(PeriodDivider, row: 1, column: 0);
            PlaceSegment(PmButton, row: 2, column: 0);

            PeriodDivider.Width = double.NaN;
            PeriodDivider.Height = 1d;
            PeriodDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
            PeriodDivider.VerticalAlignment = VerticalAlignment.Center;

            AmButton.Tag = new CornerRadius(inner, inner, 0, 0);
            PmButton.Tag = new CornerRadius(0, 0, inner, inner);
        }
    }

    private static void PlaceSegment(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void ApplyClockConvention()
    {
        PeriodField.Visibility = Use24HourClock ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- property change callbacks -----------------------------------------------------

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var input = (TimeInput)d;

        // Mid-typing the fields are already right by definition; rewriting them here is
        // what would jump the caret to the end after every keystroke.
        if (!input._syncingValueFromFields)
            input.UpdateFieldsFromSelectedTime();

        input.SelectedTimeChanged?.Invoke(input, EventArgs.Empty);
    }

    private static void OnDensityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimeInput)d).ApplyDensity();

    private static void OnUse24HourClockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var input = (TimeInput)d;

        input.ApplyClockConvention();

        // Same time, different way of writing the hour (13 vs. 1 PM).
        input.UpdateFieldsFromSelectedTime();
    }
}
