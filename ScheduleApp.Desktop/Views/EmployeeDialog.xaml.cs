using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Controls;

namespace ScheduleApp.Desktop.Views;

public partial class EmployeeDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IReadOnlySet<int> _takenEmployeeIds;

    /// <summary>Kept only so OkButton_Click can fall back to the pay type that
    /// *isn't* currently active's last-saved rate (see the DailyRate/MonthlyRate
    /// validation comment there) -- everything else already reads off the
    /// controls directly rather than off this.</summary>
    private readonly Employee? _existing;

    public string LastName => LastNameBox.Text.Trim();
    public string FirstName => FirstNameBox.Text.Trim();

    public int? DepartmentId => UnassignedCheck.IsChecked == true
        ? null
        : (DepartmentCombo.SelectedItem as Department)?.Id;

    /// <summary>Required -- see Employee.Pin's own doc comment for why. Set by
    /// OkButton_Click once EmployeeIdBox's text has passed validation.</summary>
    public int EmployeeId { get; private set; }

    public bool QualifiesForOvertime => QualifiesForOvertimeCheck.IsChecked == true;
    public bool QualifiesForNightDiff => QualifiesForNightDiffCheck.IsChecked == true;

    /// <summary>See Employee.QualifiesForRestDayPay for what this feeds into and who
    /// reads it. Unlike QualifiesForOvertime/QualifiesForNightDiff above, this has no
    /// IsChecked="True" default in the XAML -- a new employee starts out ineligible.</summary>
    public bool QualifiesForRestDayPay => QualifiesForRestDayPayCheck.IsChecked == true;

    /// <summary>See Employee.QualifiesForPremiumPay for what this feeds into and who
    /// reads it. Same unchecked-by-default convention as QualifiesForRestDayPay above.</summary>
    public bool QualifiesForPremiumPay => QualifiesForPremiumPayCheck.IsChecked == true;

    /// <summary>See Employee.ApplyOvertimeRatePercentageByDefault for what this feeds
    /// into and who reads it -- the separate "does the overtime premium actually apply,
    /// once eligible" toggle, independent of QualifiesForOvertime above.</summary>
    public bool ApplyOvertimeRatePercentageByDefault => ApplyOvertimeRatePercentageByDefaultCheck.IsChecked == true;

    /// <summary>See Employee.DefaultLeaveIsPaid for what this feeds into and who reads it.</summary>
    public bool DefaultLeaveIsPaid => DefaultLeaveIsPaidCheck.IsChecked == true;

    /// <summary>Read straight from which of PayTypeDailyRadio/PayTypeMonthlyRadio is
    /// checked -- see Employee.EmployeeType for what this feeds into and how it gates
    /// DailyRate vs. MonthlyRate below.</summary>
    public EmployeeType EmployeeType => PayTypeMonthlyRadio.IsChecked == true ? EmployeeType.Monthly : EmployeeType.Daily;

    /// <summary>Set by OkButton_Click once DailyRateBox's text has passed validation --
    /// see Employee.DailyRate for what this feeds into. Blank is treated as 0, the same
    /// "safe, never-blocking" default Employee.DailyRate itself defaults to, rather than
    /// being a required field. Only meaningful when EmployeeType == Daily; see MonthlyRate
    /// below for the Monthly-rated equivalent.</summary>
    public decimal DailyRate { get; private set; }

    /// <summary>Set by OkButton_Click once MonthlyRateBox's text has passed validation --
    /// see Employee.MonthlyRate for what this feeds into. Same blank-is-0, never-blocking
    /// convention as DailyRate above. Only meaningful when EmployeeType == Monthly.</summary>
    public decimal MonthlyRate { get; private set; }

    /// <summary>Set by OkButton_Click once RestDayWorkPremiumPercentageBox's text has
    /// passed validation -- see Employee.RestDayWorkPremiumPercentage for what this feeds
    /// into and the expected format (a decimal like 0.30 for 30%, not the full multiplier).
    /// Same blank-is-0, never-blocking convention as DailyRate above; applies to both Pay
    /// Types, so it's read regardless of which PayType radio is checked.</summary>
    public decimal RestDayWorkPremiumPercentage { get; private set; }

    /// <summary>Set by OkButton_Click once DefaultSssBox's text has passed validation --
    /// see Employee.DefaultSss for what this feeds into and how it's used. Blank is
    /// treated as 0, same "never-blocking" convention as DailyRate above.</summary>
    public decimal DefaultSss { get; private set; }

    /// <summary>See DefaultSss above; see Employee.DefaultPhilHealth for what this feeds into.</summary>
    public decimal DefaultPhilHealth { get; private set; }

    /// <summary>See DefaultSss above; see Employee.DefaultPagIbig for what this feeds into.</summary>
    public decimal DefaultPagIbig { get; private set; }

    /// <summary>See DefaultSss above; see Employee.DefaultPremiumPay for what this feeds into.</summary>
    public decimal DefaultPremiumPay { get; private set; }

    /// <summary>See DefaultSss above; see Employee.DefaultAllowance for what this feeds into.</summary>
    public decimal DefaultAllowance { get; private set; }

    /// <summary>See DefaultSss above; see Employee.DefaultCashAdvance for what this feeds into.</summary>
    public decimal DefaultCashAdvance { get; private set; }

    /// <summary>Set by OkButton_Click once ClockInBufferBeforeHoursBox's text has passed
    /// validation -- see Employee.ClockInBufferBeforeHours for what this feeds into and
    /// who reads it. Unlike the money fields above, blank stays null here (not 0) --
    /// null is itself the meaningful "no employee-level default, inherit the policy
    /// default" value, not a placeholder amount, so treating it as 0 would silently turn
    /// "no override" into "always match immediately, no search window at all." See
    /// TryParseOptionalBufferField for the shared blank-stays-null/non-negative-number
    /// validation these four buffer fields use instead of TryParseMoneyField's
    /// blank-is-0 rule.</summary>
    public double? ClockInBufferBeforeHours { get; private set; }

    /// <summary>See ClockInBufferBeforeHours above; see Employee.ClockInBufferAfterHours
    /// for what this feeds into.</summary>
    public double? ClockInBufferAfterHours { get; private set; }

    /// <summary>See ClockInBufferBeforeHours above; see Employee.ClockOutBufferBeforeHours
    /// for what this feeds into.</summary>
    public double? ClockOutBufferBeforeHours { get; private set; }

    /// <summary>See ClockInBufferBeforeHours above; see Employee.ClockOutBufferAfterHours
    /// for what this feeds into.</summary>
    public double? ClockOutBufferAfterHours { get; private set; }

    /// <param name="departments">Real departments to offer in the picker.</param>
    /// <param name="preselectedDepartmentId">Department to preselect when adding a new employee.</param>
    /// <param name="existing">Pass an existing employee to edit it instead of adding a new one.</param>
    /// <param name="takenEmployeeIds">Employee IDs already used by OTHER employees -- i.e. excluding
    /// <paramref name="existing"/>'s own ID when editing. Typing one of these blocks OK, same as the
    /// existing required-field checks below, so nothing is lost and the user can just pick another ID.</param>
    public EmployeeDialog(IEnumerable<Department> departments, int? preselectedDepartmentId,
        Employee? existing = null, IReadOnlySet<int>? takenEmployeeIds = null)
    {
        InitializeComponent();

        _takenEmployeeIds = takenEmployeeIds ?? new HashSet<int>();
        _existing = existing;

        // Deliberately set here, after InitializeComponent, rather than as a XAML
        // IsChecked="True" default on PayTypeDailyRadio -- see that radio's XAML
        // comment for why. Fires PayTypeRadio_CheckedChanged, which is what actually
        // shows DailyRateBox/hides MonthlyRateBox; safe now because every named
        // element in the window (including MonthlyRateBox, which the handler touches)
        // is already connected once InitializeComponent has returned.
        PayTypeDailyRadio.IsChecked = true;

        var departmentList = departments.ToList();
        DepartmentCombo.ItemsSource = departmentList;

        if (existing is not null)
        {
            Title = "Edit Employee";
            EmployeeIdBox.Text = existing.Pin.ToString(CultureInfo.InvariantCulture);
            LastNameBox.Text = existing.LastName;
            FirstNameBox.Text = existing.FirstName;
            QualifiesForOvertimeCheck.IsChecked = existing.QualifiesForOvertime;
            QualifiesForNightDiffCheck.IsChecked = existing.QualifiesForNightDiff;
            QualifiesForRestDayPayCheck.IsChecked = existing.QualifiesForRestDayPay;
            QualifiesForPremiumPayCheck.IsChecked = existing.QualifiesForPremiumPay;
            ApplyOvertimeRatePercentageByDefaultCheck.IsChecked = existing.ApplyOvertimeRatePercentageByDefault;

            // Setting IsChecked on the selected radio fires PayTypeRadio_CheckedChanged,
            // which is what actually flips RateLabel/DailyRateBox/MonthlyRateBox's
            // visibility below -- no separate toggle call needed here.
            if (existing.EmployeeType == EmployeeType.Monthly) PayTypeMonthlyRadio.IsChecked = true;
            else PayTypeDailyRadio.IsChecked = true;

            DailyRateBox.Value = existing.DailyRate;
            MonthlyRateBox.Value = existing.MonthlyRate;
            RestDayWorkPremiumPercentageBox.Value = existing.RestDayWorkPremiumPercentage;
            DefaultSssBox.Value = existing.DefaultSss;
            DefaultPhilHealthBox.Value = existing.DefaultPhilHealth;
            DefaultPagIbigBox.Value = existing.DefaultPagIbig;
            DefaultPremiumPayBox.Value = existing.DefaultPremiumPay;
            DefaultAllowanceBox.Value = existing.DefaultAllowance;
            DefaultCashAdvanceBox.Value = existing.DefaultCashAdvance;

            DefaultLeaveIsPaidCheck.IsChecked = existing.DefaultLeaveIsPaid;

            // Blank (not "0.00") for whichever of the four are null -- unlike the
            // money fields above, null here means "no employee-level default," a
            // real, meaningful state of its own, not just an unset amount, so
            // it's shown as genuinely empty rather than a formatted zero.
            ClockInBufferBeforeHoursBox.Text = existing.ClockInBufferBeforeHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
            ClockInBufferAfterHoursBox.Text = existing.ClockInBufferAfterHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
            ClockOutBufferBeforeHoursBox.Text = existing.ClockOutBufferBeforeHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
            ClockOutBufferAfterHoursBox.Text = existing.ClockOutBufferAfterHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

            if (existing.DepartmentId is int existingDeptId)
            {
                DepartmentCombo.SelectedItem = departmentList.FirstOrDefault(d => d.Id == existingDeptId);
            }
            else
            {
                UnassignedCheck.IsChecked = true;
                DepartmentCombo.IsEnabled = false;
            }
        }
        else
        {
            DepartmentCombo.SelectedItem = preselectedDepartmentId is int id
                ? departmentList.FirstOrDefault(d => d.Id == id)
                : departmentList.FirstOrDefault();
            DailyRateBox.Value = 0m;
            MonthlyRateBox.Value = 0m;
            RestDayWorkPremiumPercentageBox.Value = 0m;
            DefaultSssBox.Value = 0m;
            DefaultPhilHealthBox.Value = 0m;
            DefaultPagIbigBox.Value = 0m;
            DefaultPremiumPayBox.Value = 0m;
            DefaultAllowanceBox.Value = 0m;
            DefaultCashAdvanceBox.Value = 0m;
        }

        // Explicit calls rather than relying solely on the Checked/Unchecked events
        // above to have fired: for a brand-new employee neither checkbox's IsChecked
        // ever actually changes value (XAML default and code above both leave it at
        // the CheckBox's own default false), so no Checked/Unchecked event fires and
        // RestDayWorkPremiumPercentageBox/PremiumPayFieldPanel would otherwise be left
        // at whatever Visibility their XAML happens to declare. Calling both here
        // guarantees the fields' shown/hidden state always matches the checkboxes',
        // regardless of whether setting IsChecked above actually triggered an event.
        UpdateRestDayPayFieldVisibility();
        UpdatePremiumPayFieldVisibility();

        Loaded += (_, _) => EmployeeIdBox.Focus();
    }

    private void UnassignedCheck_CheckedChanged(object sender, RoutedEventArgs e)
        => DepartmentCombo.IsEnabled = UnassignedCheck.IsChecked != true;

    /// <summary>Hides RestDayWorkPremiumPercentageBox (and its label) whenever
    /// QualifiesForRestDayPayCheck is unchecked -- leaving a premium-% box visible
    /// and editable for someone who can't earn the premium at all would be exactly
    /// the kind of dead-end field this dialog avoids everywhere else. The stored
    /// value itself is untouched either way; only shown/hidden here, still read and
    /// validated normally in OkButton_Click regardless of this checkbox's state.</summary>
    private void QualifiesForRestDayPayCheck_CheckedChanged(object sender, RoutedEventArgs e)
        => UpdateRestDayPayFieldVisibility();

    private void UpdateRestDayPayFieldVisibility() =>
        RestDayWorkPremiumPercentageLabel.Visibility = RestDayWorkPremiumPercentageBox.Visibility =
            QualifiesForRestDayPayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Same idea as QualifiesForRestDayPayCheck_CheckedChanged above, for the
    /// "Premium Pay" field in the pay-adjustments row (PremiumPayFieldPanel wraps just
    /// that field's own label+box, leaving the Allowance/Cash Advance fields alongside
    /// it untouched).</summary>
    private void QualifiesForPremiumPayCheck_CheckedChanged(object sender, RoutedEventArgs e)
        => UpdatePremiumPayFieldVisibility();

    private void UpdatePremiumPayFieldVisibility() =>
        PremiumPayFieldPanel.Visibility =
            QualifiesForPremiumPayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shared Checked handler for both PayTypeDailyRadio and
    /// PayTypeMonthlyRadio -- reads current state off EmployeeType rather than off
    /// whichever radio raised the event, so it's idempotent no matter which one
    /// fires it. Only touches which rate box is shown/labeled; validation of
    /// whichever one is active happens later, in OkButton_Click.</summary>
    private void PayTypeRadio_CheckedChanged(object sender, RoutedEventArgs e)
    {
        var isMonthly = EmployeeType == EmployeeType.Monthly;
        RateLabel.Text = isMonthly ? "Monthly rate" : "Daily rate";
        DailyRateBox.Visibility = isMonthly ? Visibility.Collapsed : Visibility.Visible;
        MonthlyRateBox.Visibility = isMonthly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LastNameBox.Text) || string.IsNullOrWhiteSpace(FirstNameBox.Text))
        {
            MessageBox.Show("First and last name are required.", "Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (UnassignedCheck.IsChecked != true && DepartmentCombo.SelectedItem is null)
        {
            MessageBox.Show("Select a department, or check \"Leave unassigned for now\".", "Required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Required now -- see Employee.Pin's own doc comment for why (assigned on the
        // ZKTeco device before an employee can be added here at all, not something
        // ScheduleApp generates or lets stand in for "not yet known").
        if (string.IsNullOrWhiteSpace(EmployeeIdBox.Text))
        {
            MessageBox.Show("Employee ID is required.", "Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(EmployeeIdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var employeeId))
        {
            MessageBox.Show("Employee ID must be a whole number.", "Required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_takenEmployeeIds.Contains(employeeId))
        {
            MessageBox.Show($"Employee ID {employeeId} is already assigned to another employee. Choose a different ID.",
                "Duplicate Employee ID", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EmployeeId = employeeId;

        // Blank is treated as 0 -- same "never blocks saving" convention as
        // Employee.DailyRate's own default. All nine money/rate fields (DailyRate,
        // MonthlyRate, RestDayWorkPremiumPercentage, the three statutory contribution
        // defaults, and the three pay-adjustment defaults) are NumericTextBoxes, so there's
        // nothing left to validate here: that control refuses a non-number or a negative at
        // the keystroke, so the old TryParseMoneyField check -- parse, then warn on what
        // came back bad -- has no case left to catch, and MoneyValue below is just "what's
        // in the box, or 0 if it's empty".
        //
        // Only the active Pay Type's rate box is read here -- DailyRateBox when
        // Daily is selected, MonthlyRateBox when Monthly is (see PayTypeRadio_CheckedChanged
        // for which one is visible). The *other* one is never read, even if it has stale
        // or invalid text left over from before a Pay Type switch: its stored value is
        // just carried forward from whatever this employee already had saved (0 for a new
        // employee), since it's ignored by payroll for this Pay Type anyway -- see
        // Employee.DailyRate/MonthlyRate's own "ignored ... for the other Pay Type" doc
        // comments. This also means switching Pay Type back and forth in this dialog
        // before hitting OK never silently discards a rate the user already typed in.
        if (EmployeeType == EmployeeType.Monthly)
        {
            MonthlyRate = MoneyValue(MonthlyRateBox);
            DailyRate = _existing?.DailyRate ?? 0m;
        }
        else
        {
            DailyRate = MoneyValue(DailyRateBox);
            MonthlyRate = _existing?.MonthlyRate ?? 0m;
        }

        // Not gated behind Pay Type -- always read regardless of which radio is checked,
        // since a Daily-rated employee can be called in on a Rest Day too (see
        // Employee.RestDayWorkPremiumPercentage).
        RestDayWorkPremiumPercentage = MoneyValue(RestDayWorkPremiumPercentageBox);

        DefaultSss = MoneyValue(DefaultSssBox);
        DefaultPhilHealth = MoneyValue(DefaultPhilHealthBox);
        DefaultPagIbig = MoneyValue(DefaultPagIbigBox);
        DefaultPremiumPay = MoneyValue(DefaultPremiumPayBox);
        DefaultAllowance = MoneyValue(DefaultAllowanceBox);
        DefaultCashAdvance = MoneyValue(DefaultCashAdvanceBox);

        if (!TryParseOptionalBufferField(ClockInBufferBeforeHoursBox, "Clock-in buffer, before", out var clockInBufferBeforeHours)) return;
        ClockInBufferBeforeHours = clockInBufferBeforeHours;

        if (!TryParseOptionalBufferField(ClockInBufferAfterHoursBox, "Clock-in buffer, after", out var clockInBufferAfterHours)) return;
        ClockInBufferAfterHours = clockInBufferAfterHours;

        if (!TryParseOptionalBufferField(ClockOutBufferBeforeHoursBox, "Clock-out buffer, before", out var clockOutBufferBeforeHours)) return;
        ClockOutBufferBeforeHours = clockOutBufferBeforeHours;

        if (!TryParseOptionalBufferField(ClockOutBufferAfterHoursBox, "Clock-out buffer, after", out var clockOutBufferAfterHours)) return;
        ClockOutBufferAfterHours = clockOutBufferAfterHours;

        DialogResult = true;
    }

    /// <summary>What one of this dialog's nine money/rate boxes currently holds, with an
    /// empty box reading as 0 -- the "never blocks saving" rule those nine share (see the
    /// comment at this method's call sites). Replaces the old TryParseMoneyField, whose parse
    /// and its warning both became unreachable once these boxes became NumericTextBoxes: a
    /// letter, a second decimal point and a minus sign are all refused as they're typed now,
    /// so there is no bad value left for a check here to find.</summary>
    private static decimal MoneyValue(NumericTextBox box) => box.Value ?? 0m;

    /// <summary>Shared blank-stays-null/non-negative-number validation for the four
    /// buffer-default boxes (ClockInBufferBeforeHoursBox/.../ClockOutBufferAfterHoursBox)
    /// -- deliberately NOT TryParseMoneyField's blank-is-0 rule, since null is itself
    /// the meaningful "no employee-level default, inherit the policy default" state
    /// for these four (see NormalBufferResolver), not just an unset amount the way
    /// 0.00 is for the money fields above. Otherwise the same "never blocks saving,
    /// only a real invalid value does" shape as TryParseMoneyField.</summary>
    private static bool TryParseOptionalBufferField(TextBox box, string fieldLabel, out double? value)
    {
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            value = null;
            return true;
        }

        if (!double.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            MessageBox.Show($"{fieldLabel} must be a valid number of hours, 0 or greater (or left blank to inherit the policy default).",
                "Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            value = null;
            return false;
        }

        value = parsed;
        return true;
    }
}
