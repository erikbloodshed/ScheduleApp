using System.Windows;
using System.Windows.Controls;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// A number field that always reads as a percentage -- Value 0.30 displays as "30 %",
/// not "0.30". Every rate/premium field in this app (PayrollPolicy.RestDayPremiumPercentage,
/// Employee.HolidayPremiumPercentage, and their siblings) already stores exactly this kind
/// of value -- a decimal fraction, 0.30 meaning 30% -- and today shows it back as the bare
/// fraction with a tooltip explaining the "0.30 means 30%" convention (see
/// SettingsDialog.xaml's "Premium rates" section). This control is what lets a field show
/// the number a person actually thinks in instead.
///
/// Not a rewrite of NumericTextBox's own parsing/validation/commit machinery -- it wraps
/// one, unmodified (see the matching XAML's ValueBox), and adds only the "%" suffix and the
/// x100/div-100 scaling between what's displayed and what <see cref="Value"/> holds -- done via
/// FractionToPercentConverter on ValueBox's own Value/PlaceholderValue bindings, not by hand
/// in code-behind, so both directions (a value set from outside needing to reach the
/// display, and a typed-and-committed value needing to reach this control's own Value) go
/// through the same one conversion rather than needing to be kept in sync separately.
///
/// <see cref="Value"/>/<see cref="PlaceholderValue"/> stay in the *same* fraction units as
/// ValueBox's own equivalents would be for a plain NumericTextBox bound to the same
/// PayrollPolicy/Employee property -- only the *displayed* number is scaled -- so swapping
/// one of those existing fields over to this control needs no change to the C# property it
/// binds to.
/// </summary>
public partial class PercentTextBox : UserControl
{
    public PercentTextBox()
    {
        InitializeComponent();
    }

    /// <summary>Raised whenever the wrapped ValueBox commits a real change -- see
    /// NumericTextBox.ValueCommitted for exactly when that is (Enter or lost focus, and
    /// only when something actually changed). Forwarded rather than re-derived from this
    /// control's own Value DP changing, so it fires at the same single moment ValueBox's
    /// own event does, not once per intermediate binding update.</summary>
    public event EventHandler? ValueCommitted;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(decimal?),
        typeof(PercentTextBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>The percentage as a fraction -- 0.30 for 30%, matching how every rate field
    /// in this app already stores one (see this class's own doc comment). Two-way bindable,
    /// so Value="{Binding RestDayPremiumPercentage}" is enough on its own, the same as
    /// NumericTextBox.Value. Displayed multiplied by 100 with a trailing "%" -- see
    /// FractionToPercentConverter, applied to ValueBox's own binding in the XAML.</summary>
    public decimal? Value
    {
        get => (decimal?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty DecimalDigitsProperty = DependencyProperty.Register(
        nameof(DecimalDigits),
        typeof(int),
        typeof(PercentTextBox),
        new PropertyMetadata(2));

    /// <summary>Digits shown after the decimal point of the *displayed percentage* -- 2
    /// (the default) shows "30.00 %"; 0 shows "30 %". Same meaning as
    /// NumericTextBox.DecimalDigits, just describing the scaled-by-100 number this control
    /// actually displays rather than the raw fraction Value holds.</summary>
    public int DecimalDigits
    {
        get => (int)GetValue(DecimalDigitsProperty);
        set => SetValue(DecimalDigitsProperty, value);
    }

    public static readonly DependencyProperty PlaceholderValueProperty = DependencyProperty.Register(
        nameof(PlaceholderValue),
        typeof(decimal?),
        typeof(PercentTextBox),
        new PropertyMetadata(null));

    /// <summary>Same fraction units as <see cref="Value"/> (0.30, not 30), shown -- scaled
    /// and suffixed the same way a real Value would be -- grayed out whenever Value itself
    /// is null. See NumericTextBox.PlaceholderValue for the full behavior this forwards to
    /// unchanged: an untouched placeholder still commits as null, never as whatever number
    /// happens to be showing.</summary>
    public decimal? PlaceholderValue
    {
        get => (decimal?)GetValue(PlaceholderValueProperty);
        set => SetValue(PlaceholderValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(decimal),
        typeof(PercentTextBox),
        new PropertyMetadata(0m));

    /// <summary>Same fraction units as <see cref="Value"/> -- 0.05 rejects anything below
    /// 5%, not below 0.05%. Forwarded (x100) to ValueBox's own Minimum. Defaults to 0, same
    /// as NumericTextBox.Minimum's own default -- every premium field this control exists
    /// for is non-negative.</summary>
    public decimal Minimum
    {
        get => (decimal)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>The highest Maximum this control can safely take -- see MaximumProperty's
    /// own doc comment for why decimal.MaxValue itself isn't a safe default here the way it
    /// is for NumericTextBox.Maximum.</summary>
    public const decimal MaxSafeValue = decimal.MaxValue / 100m;

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(decimal),
        typeof(PercentTextBox),
        new PropertyMetadata(MaxSafeValue));

    /// <summary>Same fraction units as <see cref="Value"/> -- 5 rejects anything above
    /// 500%, not above 5%. Forwarded (x100) to ValueBox's own Maximum, which is why this
    /// defaults to MaxSafeValue (decimal.MaxValue / 100) rather than plain
    /// decimal.MaxValue the way NumericTextBox.Maximum itself does: FractionToPercentConverter
    /// multiplies whatever this holds by 100 on its way to ValueBox, and decimal.MaxValue * 100
    /// overflows decimal's own range outright. No realistic premium ever approaches even this
    /// safe ceiling, so it's effectively "unbounded" for every actual caller.</summary>
    public decimal Maximum
    {
        get => (decimal)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private void ValueBox_ValueCommitted(object sender, EventArgs e) =>
        ValueCommitted?.Invoke(this, EventArgs.Empty);
}
