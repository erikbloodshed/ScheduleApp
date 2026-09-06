using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// A TextBox that can only ever contain a number -- this codebase's own equivalent of
/// Syncfusion's DoubleTextBox, written against decimal rather than double because every
/// money value in this app (PayrollAdjustment.Amount, Employee.DailyRate, the Default*
/// contribution fields) is a decimal, and routing those through a binary floating point
/// control would round-trip 1234.56 into 1234.5599999999999 on its way back.
///
/// What it does that a plain TextBox doesn't:
///   - Rejects any keystroke or paste that wouldn't leave a valid number behind (letters,
///     a second decimal point, a minus sign when Minimum is 0, more decimals than
///     DecimalDigits), so a bad amount can't be typed at all rather than being caught by a
///     parse check after the fact.
///   - Exposes the typed <see cref="Value"/> (decimal?, two-way bindable by default)
///     alongside Text, kept in sync in both directions.
///   - Reformats to DecimalDigits places on commit (Enter or losing focus), so "1234.5"
///     settles as "1,234.50" the same way NumberConverter renders every other money figure
///     on the page.
///   - Clamps to Minimum/Maximum on commit -- Minimum defaults to 0, which is what every
///     payroll amount box wants (an allowance or a cash advance is never negative).
///   - Up/Down arrow keys step by <see cref="Increment"/>; Escape abandons the edit and puts
///     back whatever the box held when it was focused.
///   - Selects its whole contents when focused (see SelectAllOnFocus), including on a mouse
///     click, so an existing figure is typed straight over.
///   - Raises <see cref="ValueCommitted"/> once per actual change, on Enter or lost focus --
///     not on every keystroke, and not at all when the box is left holding what it started
///     with. That's the event to hook for "save this amount", instead of a LostFocus +
///     KeyDown pair that both have to guard against committing the same edit twice.
///
/// Styling note: WPF looks up an implicit style by the element's exact type, so the app-wide
/// implicit TextBox style (WPF-UI's, merged by App.xaml) does NOT reach a derived class on
/// its own. Every usage therefore names a style explicitly -- either one of the page's own
/// TextBox styles (PayrollSummaryView's EditableAmountTextBox) or plain
/// Style="{StaticResource {x:Type TextBox}}" -- which works because a Style whose TargetType
/// is a base type still applies to a derived element. Keep doing that on new markup; a box
/// without it renders as a bare, unstyled WPF TextBox next to its styled siblings.
/// </summary>
public class NumericTextBox : TextBox
{
    /// <summary>True while Value is being written from the text (i.e. someone is typing), so
    /// the Value change handler leaves Text alone instead of reformatting mid-keystroke --
    /// and so coercion skips its Minimum/Maximum clamp, which would otherwise fight the
    /// typing (with Minimum 10, the "1" of "100" would be snapped straight to 10). Clamping
    /// happens on commit instead, where it can't interrupt anything.</summary>
    private bool _syncingValueFromText;

    /// <summary>Mirror image of the above: true while Text is being rewritten from Value, so
    /// the TextChanged handler doesn't parse its own output straight back.</summary>
    private bool _syncingTextFromValue;

    /// <summary>What Value was when the box last took focus -- the baseline
    /// <see cref="ValueCommitted"/> compares against (so tabbing through a box without
    /// touching it commits nothing) and what Escape restores.</summary>
    private decimal? _valueOnFocus;

    public NumericTextBox()
    {
        DataObject.AddPastingHandler(this, OnPaste);
    }

    /// <summary>Raised on Enter or lost focus, and only when the committed value differs from
    /// what the box held when it was focused. Handlers can read <see cref="Value"/> (or
    /// <see cref="InvariantText"/> for the string-taking view model methods) knowing the text
    /// has already been parsed, clamped and reformatted.</summary>
    public event EventHandler? ValueCommitted;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(decimal?),
        typeof(NumericTextBox),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged,
            CoerceValueProperty));

    /// <summary>The box's contents as a number, or null when it's empty (or holds nothing
    /// parseable yet, mid-typing). Two-way by default, so Value="{Binding Amount}" is enough
    /// -- no UpdateSourceTrigger or converter needed.</summary>
    public decimal? Value
    {
        get => (decimal?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(decimal),
        typeof(NumericTextBox),
        new PropertyMetadata(0m, OnRangeOrFormatChanged));

    /// <summary>Lowest value a commit can produce; anything lower is clamped up to it.
    /// Defaults to 0 -- which also decides whether a minus sign can be typed at all (see
    /// IsAllowedText), so leaving it alone makes the box non-negative by construction. Set it
    /// to decimal.MinValue for a box that genuinely takes negatives.</summary>
    public decimal Minimum
    {
        get => (decimal)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(decimal),
        typeof(NumericTextBox),
        new PropertyMetadata(decimal.MaxValue, OnRangeOrFormatChanged));

    /// <summary>Highest value a commit can produce. Left wide open by default: a payroll
    /// amount has no natural ceiling, and a made-up one only ever shows up as a silently
    /// truncated figure.</summary>
    public decimal Maximum
    {
        get => (decimal)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty DecimalDigitsProperty = DependencyProperty.Register(
        nameof(DecimalDigits),
        typeof(int),
        typeof(NumericTextBox),
        new PropertyMetadata(2, OnRangeOrFormatChanged),
        digits => digits is int and >= 0 and <= 28);

    /// <summary>Digits allowed (and always shown) after the decimal point -- 2 for money. 0
    /// makes the box integer-only, decimal point included in what it refuses to accept.</summary>
    public int DecimalDigits
    {
        get => (int)GetValue(DecimalDigitsProperty);
        set => SetValue(DecimalDigitsProperty, value);
    }

    public static readonly DependencyProperty UseGroupSeparatorProperty = DependencyProperty.Register(
        nameof(UseGroupSeparator),
        typeof(bool),
        typeof(NumericTextBox),
        new PropertyMetadata(true, OnRangeOrFormatChanged));

    /// <summary>Whether a committed value is written back with thousands separators
    /// ("1,234.50" rather than "1234.50"). On by default to match NumberConverter, which is
    /// what every read-only money figure in the app is rendered through.</summary>
    public bool UseGroupSeparator
    {
        get => (bool)GetValue(UseGroupSeparatorProperty);
        set => SetValue(UseGroupSeparatorProperty, value);
    }

    public static readonly DependencyProperty AllowNullProperty = DependencyProperty.Register(
        nameof(AllowNull),
        typeof(bool),
        typeof(NumericTextBox),
        new PropertyMetadata(true));

    /// <summary>Whether an empty box is allowed to stay empty (Value null) on commit, or is
    /// filled in with 0 instead. True by default, because "blank" and "0.00" aren't the same
    /// thing everywhere in payroll: for SSS/PhilHealth/Pag-IBIG a cleared box means "don't
    /// write a row at all", while for Allowance/Premium Pay/Cash Advance it means zero -- and
    /// that distinction is the view model's to make (see
    /// PayrollAdjustmentType.BlankAmountMeansZero), not this control's to erase by helpfully
    /// typing a 0 into every box someone cleared.</summary>
    public bool AllowNull
    {
        get => (bool)GetValue(AllowNullProperty);
        set => SetValue(AllowNullProperty, value);
    }

    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
        nameof(Increment),
        typeof(decimal),
        typeof(NumericTextBox),
        new PropertyMetadata(1m));

    /// <summary>How much the Up/Down arrow keys move the value. 0 turns that off. Stepping
    /// only changes what's in the box -- the commit still happens on Enter or lost focus, so
    /// holding an arrow key down doesn't fire a write per repeat.</summary>
    public decimal Increment
    {
        get => (decimal)GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public static readonly DependencyProperty SelectAllOnFocusProperty = DependencyProperty.Register(
        nameof(SelectAllOnFocus),
        typeof(bool),
        typeof(NumericTextBox),
        new PropertyMetadata(true));

    /// <summary>Whether focusing the box selects everything in it, so the existing figure is
    /// replaced by whatever is typed next rather than appended to. On by default: these boxes
    /// are almost always overwritten wholesale, never edited a digit at a time.</summary>
    public bool SelectAllOnFocus
    {
        get => (bool)GetValue(SelectAllOnFocusProperty);
        set => SetValue(SelectAllOnFocusProperty, value);
    }

    public static readonly DependencyProperty ClearFocusOnEnterProperty = DependencyProperty.Register(
        nameof(ClearFocusOnEnter),
        typeof(bool),
        typeof(NumericTextBox),
        new PropertyMetadata(false));

    /// <summary>Whether Enter also drops keyboard focus after committing, so the box doesn't
    /// sit there still looking mid-edit -- and, with it, whether Enter stops at this box at
    /// all: on, the key is marked handled (Enter's whole job was finishing the edit); off, it
    /// commits and keeps bubbling, so a dialog's IsDefault button still receives it. Off by
    /// default, since a box on a form usually sits inside such a dialog;
    /// PayrollSummaryView's amount boxes (no default button anywhere above them) turn it on
    /// to keep the behavior their old KeyDown handler had.</summary>
    public bool ClearFocusOnEnter
    {
        get => (bool)GetValue(ClearFocusOnEnterProperty);
        set => SetValue(ClearFocusOnEnterProperty, value);
    }

    /// <summary>Value rendered with invariant separators and no grouping ("1234.56"), or ""
    /// when Value is null. For the view model methods that take a raw amount string and parse
    /// it with CultureInfo.InvariantCulture (PayrollViewModel.SetSingleValueAsync,
    /// UpdateInlineAmountAsync): passing this rather than Text keeps those parses working
    /// whatever the machine's number format turns out to be, since Text is deliberately
    /// formatted for the current culture instead.</summary>
    public string InvariantText =>
        Value is { } value
            ? value.ToString(FixedFormat, CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>"F2"-style format specifier -- fixed point, no grouping.</summary>
    private string FixedFormat => "F" + DecimalDigits.ToString(CultureInfo.InvariantCulture);

    /// <summary>"N2"-style format specifier -- fixed point, thousands separators if asked for.</summary>
    private string DisplayFormat =>
        (UseGroupSeparator ? "N" : "F") + DecimalDigits.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses, clamps, rounds and reformats what's currently typed, then raises
    /// <see cref="ValueCommitted"/> if that left the box holding something other than what it
    /// held when it was focused. Called on Enter and on lost focus; public so a dialog's OK
    /// button can force a commit on a box that still has focus.</summary>
    public void CommitValue()
    {
        decimal? committed = TryParseText(Text, out var parsed)
            ? ClampAndRound(parsed)
            : AllowNull ? null : ClampAndRound(0m);

        SetCurrentValue(ValueProperty, committed);

        // Unconditionally, even when Value itself didn't change: the text can still need
        // reformatting ("1234.5" -> "1,234.50"), and in that case OnValueChanged never fired.
        UpdateTextFromValue();

        if (committed == _valueOnFocus) return;

        _valueOnFocus = committed;
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The baseline is taken on logical focus as well as keyboard focus below, so
    /// that it's always set by the time the matching OnLostFocus (a logical-focus event
    /// itself) commits -- a box that somehow took focus without taking keyboard focus would
    /// otherwise commit against a stale baseline and report a change that never happened.</summary>
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);

        _valueOnFocus = Value;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        _valueOnFocus = Value;

        if (SelectAllOnFocus)
            SelectAll();
    }

    /// <summary>A click into an unfocused box would normally place the caret and leave
    /// nothing selected, defeating SelectAllOnFocus. Swallowing that first click and focusing
    /// by hand lets OnGotKeyboardFocus do the selecting; once the box has focus, clicks and
    /// drag-selection behave exactly as they always do.</summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SelectAllOnFocus && !IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Focus();
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        CommitValue();
        base.OnLostFocus(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitValue();

                // Handled only in the ClearFocusOnEnter case: there, Enter's whole job is to
                // finish this box's edit. Everywhere else it commits and then carries on
                // bubbling, so a dialog's IsDefault button (EmployeeDialog's Save) still gets
                // its Enter the way it would from any other TextBox.
                if (ClearFocusOnEnter)
                {
                    e.Handled = true;
                    Keyboard.ClearFocus();
                }
                else
                {
                    SelectAll();
                }

                return;

            case Key.Escape:
                // An Escape with nothing to abandon is left alone rather than swallowed, so
                // it still reaches a dialog's IsCancel button (EmployeeDialog's Cancel). With
                // an edit in progress it undoes that edit first -- a second Escape then
                // closes the dialog.
                if (Value == _valueOnFocus)
                {
                    base.OnPreviewKeyDown(e);
                    return;
                }

                SetCurrentValue(ValueProperty, _valueOnFocus);
                UpdateTextFromValue();
                SelectAll();
                e.Handled = true;
                return;

            case Key.Up when Increment != 0m && !IsReadOnly:
                Step(Increment);
                e.Handled = true;
                return;

            case Key.Down when Increment != 0m && !IsReadOnly:
                Step(-Increment);
                e.Handled = true;
                return;

            // Space reaches PreviewTextInput as " " and would be rejected there anyway, but
            // blocking the key outright keeps it from ever arriving as a keystroke at all.
            case Key.Space:
                e.Handled = true;
                return;

            default:
                base.OnPreviewKeyDown(e);
                return;
        }
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (!IsAllowedText(ComposeCandidate(e.Text)))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewTextInput(e);
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);

        if (_syncingTextFromValue) return;

        _syncingValueFromText = true;
        try
        {
            SetCurrentValue(ValueProperty, TryParseText(Text, out var parsed) ? parsed : null);
        }
        finally
        {
            _syncingValueFromText = false;
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var pasted = e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)
            ? e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
            : null;

        // A non-text payload (an image, a file drop) has nothing sensible to insert, so it's
        // refused outright rather than left to WPF's own default handling.
        if (pasted is null || !IsAllowedText(ComposeCandidate(pasted)))
            e.CancelCommand();
    }

    /// <summary>What the text would become if <paramref name="input"/> replaced the current
    /// selection -- the string the keystroke/paste checks actually validate, since a
    /// character that's fine on its own ("." or "-") still depends on what's already
    /// there.</summary>
    private string ComposeCandidate(string input) =>
        Text.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, input);

    /// <summary>Whether <paramref name="candidate"/> is a number, or could still become one
    /// with more typing -- "", "-", "1." and ".5" all pass, since refusing them would make it
    /// impossible to ever type the values they lead to. Range is deliberately not checked
    /// here (see _syncingValueFromText's own comment); only shape is.</summary>
    private bool IsAllowedText(string candidate)
    {
        if (candidate.Length == 0) return true;

        var format = CultureInfo.CurrentCulture.NumberFormat;
        var decimalSeparator = format.NumberDecimalSeparator;
        var groupSeparator = format.NumberGroupSeparator;
        var negativeSign = format.NegativeSign;

        var rest = candidate;
        if (rest.StartsWith(negativeSign, StringComparison.CurrentCulture))
        {
            if (Minimum >= 0m) return false;
            rest = rest[negativeSign.Length..];
        }

        var decimalsSeen = -1;
        var index = 0;
        while (index < rest.Length)
        {
            if (char.IsDigit(rest[index]))
            {
                // Once past the decimal separator, stop at DecimalDigits digits rather than
                // accepting extras only to round them away again on commit.
                if (decimalsSeen >= 0 && ++decimalsSeen > DecimalDigits) return false;
                index++;
                continue;
            }

            if (decimalsSeen < 0 && DecimalDigits > 0 &&
                string.CompareOrdinal(rest, index, decimalSeparator, 0, decimalSeparator.Length) == 0)
            {
                decimalsSeen = 0;
                index += decimalSeparator.Length;
                continue;
            }

            // Group separators are accepted wherever they turn up (pasting a "1,234.50" that
            // this box itself produced has to work) without checking they fall every three
            // digits -- the parse on commit is what decides, and NumberStyles.Number is
            // equally relaxed about where they sit.
            if (decimalsSeen < 0 && UseGroupSeparator && groupSeparator.Length > 0 &&
                string.CompareOrdinal(rest, index, groupSeparator, 0, groupSeparator.Length) == 0)
            {
                index += groupSeparator.Length;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryParseText(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    private decimal ClampAndRound(decimal value) =>
        Math.Clamp(Math.Round(value, DecimalDigits, MidpointRounding.AwayFromZero), Minimum, Maximum);

    /// <summary>Arrow-key step. Starts from 0 rather than refusing to move when the box is
    /// empty, so Up on a blank box is a quick way to the first value.</summary>
    private void Step(decimal delta)
    {
        SetCurrentValue(ValueProperty, ClampAndRound((Value ?? 0m) + delta));
        UpdateTextFromValue();
        SelectAll();
    }

    private void UpdateTextFromValue()
    {
        var formatted = Value is { } value
            ? value.ToString(DisplayFormat, CultureInfo.CurrentCulture)
            : string.Empty;

        if (string.Equals(Text, formatted, StringComparison.Ordinal)) return;

        _syncingTextFromValue = true;
        try
        {
            // SetCurrentValue, not Text = -- the amount boxes on PayrollSummaryView bind Text
            // one-way through NumberConverter, and a plain local assignment would blow that
            // binding away for good, leaving the box frozen on the next reload.
            SetCurrentValue(TextProperty, formatted);
            CaretIndex = formatted.Length;
        }
        finally
        {
            _syncingTextFromValue = false;
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericTextBox)d;

        // Mid-typing, the text is already right by definition -- reformatting it here is what
        // would jump the caret to the end after every keystroke.
        if (box._syncingValueFromText) return;

        box.UpdateTextFromValue();
    }

    private static object CoerceValueProperty(DependencyObject d, object baseValue)
    {
        var box = (NumericTextBox)d;

        if (box._syncingValueFromText || baseValue is not decimal value) return baseValue;

        return box.ClampAndRound(value);
    }

    private static void OnRangeOrFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericTextBox)d;

        box.CoerceValue(ValueProperty);
        box.UpdateTextFromValue();
    }
}
