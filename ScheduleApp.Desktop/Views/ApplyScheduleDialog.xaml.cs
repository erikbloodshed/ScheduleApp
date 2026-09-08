using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

public partial class ApplyScheduleDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>One row's pair of time pickers, its optional per-segment
    /// buffer-override textboxes, plus its remove button -- kept together so
    /// OkButton_Click can read them back and AddSegmentButton_Click/RemoveSegmentRow
    /// can manage them as a unit.</summary>
    private sealed record SegmentRow(
        Grid Container, TimeInput TimeInPicker, TimeInput TimeOutPicker,
        TextBox ClockInBufferBox, TextBox ClockOutBufferBox);

    private readonly List<SegmentRow> _segmentRows = new();

    /// <summary>Builds the label for a tri-state combo's index-0 "no override"
    /// option, spelling out what that default actually resolves to instead of
    /// the old unqualified "Use employee default" (the person had no way to see
    /// what the default *was* without leaving this dialog to check the Employee
    /// dialog). Single employee: reads that employee's own flag straight off,
    /// via <paramref name="selector"/>. Several employees (the multi-select
    /// "Set Schedule for N Employees" flow): shows the shared value only if
    /// every selected employee actually agrees -- a blended "(varies)" would be
    /// misleading, since which per-employee default applies isn't a single
    /// fact this dialog can state. <paramref name="trueLabel"/>/
    /// <paramref name="falseLabel"/> supply the same true/false wording the
    /// combo's own explicit-override options use, so the parenthetical reads
    /// as one of those two rather than a bare "True"/"False".</summary>
    private static string DescribeEmployeeDefault(
        IReadOnlyList<Employee> employees, Func<Employee, bool> selector, string trueLabel, string falseLabel)
    {
        var first = selector(employees[0]);
        var allAgree = employees.All(e => selector(e) == first);
        return allAgree
            ? $"Use employee default ({(first ? trueLabel : falseLabel)})"
            : "Use employee default (varies)";
    }

    /// <summary>Same idea as <see cref="DescribeEmployeeDefault"/> just above, but for
    /// the four Normal-type buffer boxes below (ClockInBufferBeforeBox/.../
    /// ClockOutBufferAfterBox), whose "default" is a number rather than a true/false
    /// state. Each employee's own buffer default (see Employee.ClockInBufferBeforeHours/
    /// .../ClockOutBufferAfterHours) takes priority over <paramref name="policyDefaultHours"/>
    /// when set -- the same three-tier day/employee/policy cascade
    /// SingleWindowShiftCalculationStrategy/RestDayShiftCalculationStrategy resolve at
    /// calculation time via NormalBufferResolver, just described here as a single
    /// starting number rather than actually resolved against one specific day.
    ///
    /// Single employee: returns that employee's own resolved value, formatted the same
    /// "0.##" way ShowBufferDefault already displays every buffer box's default in.
    /// Several employees (the multi-select "Set Schedule for N Employees" flow): returns
    /// the shared value only if every selected employee's own resolved value actually
    /// agrees -- same "no misleading blended figure" reasoning as DescribeEmployeeDefault's
    /// own "(varies)" case -- otherwise the literal text "(varies)".
    ///
    /// "(varies)" is safe to show here specifically because of how InitializeBufferBox/
    /// ShowBufferDefault/TryReadBufferOverride already work together: it's just gray
    /// placeholder text (Tag stays true), so TryReadBufferOverride still reads it back
    /// as "no override" -- untouched, it correctly saves a plain per-day null, letting
    /// each selected employee's own default (or the policy default, for whichever of
    /// them don't have one) keep applying independently once the schedule is actually
    /// calculated, rather than forcing every selected employee onto one blended number
    /// that might not even be any of theirs.</summary>
    private static string DescribeBufferDefault(
        IReadOnlyList<Employee> employees, Func<Employee, double?> selector, double policyDefaultHours)
    {
        double Resolve(Employee e) => selector(e) ?? policyDefaultHours;
        var first = Resolve(employees[0]);
        var allAgree = employees.All(e => Resolve(e) == first);
        return allAgree
            ? first.ToString("0.##", CultureInfo.InvariantCulture)
            : "(varies)";
    }

    /// <summary>What WorkTimeBox should start showing for a brand-new (non-prefill)
    /// entry -- unlike DescribeBufferDefault above, this isn't gray placeholder text
    /// describing a genuine runtime resolution tier (WorkTimeHours has none; it's a
    /// required, concrete value once a day is saved), it's the actual starting number
    /// the box is set to, same as <paramref name="policyDefaultHours"/> always was
    /// before Employee.DefaultWorkTimeHours existed. Single employee: that employee's
    /// own resolved value (their own default, or the policy default if they don't have
    /// one). Several employees: only if every one of them resolves to the same value --
    /// otherwise falls back to the plain policy default, since there's no placeholder
    /// convention here to fall back to the way DescribeBufferDefault's "(varies)" text
    /// does, and WorkTimeBox must always hold a real, positive number for OK to accept
    /// (see the WorkTimeBox validation in OkButton_Click).</summary>
    private static double ResolveWorkTimeDefault(IReadOnlyList<Employee> employees, double policyDefaultHours)
    {
        double Resolve(Employee e) => (double?)e.DefaultWorkTimeHours ?? policyDefaultHours;
        var first = Resolve(employees[0]);
        return employees.All(e => Resolve(e) == first) ? first : policyDefaultHours;
    }

    /// <summary>Backs the three tri-state ComboBoxes for the Overtime/Night Diff
    /// per-day override section -- SelectedIndex 0 always means "no override, use
    /// the employee-level default" (null), 1/2 mean the two explicit states.
    /// Built per-instance in the constructor (not shared static arrays the way
    /// these used to be) since index 0's label now depends on the specific
    /// employees this dialog was opened for -- see DescribeEmployeeDefault.
    /// Three separate lists, one per combo, rather than two or one shared one:
    /// Overtime-eligible and Night-diff-eligible both use "Eligible"/"Not
    /// eligible" wording for their explicit options, but each reads a
    /// different Employee flag for its own index-0 default label
    /// (QualifiesForOvertime vs QualifiesForNightDiff), so they can't share an
    /// array the way the two static ones used to be shared before this
    /// per-employee-default change. ReadTriStateOverride reads any of the
    /// three back the same way, by index alone, ignoring the text.</summary>
    private readonly string[] _overtimeEligibleOptions;
    private readonly string[] _nightDiffEligibleOptions;
    private readonly string[] _applyRateOptions;

    /// <summary>Whether every selected employee has Employee.QualifiesForRestDayPay set --
    /// computed once in the constructor (eligibility doesn't change over the dialog's
    /// lifetime, only which ScheduleType is selected does), and used by
    /// UpdateFieldAvailability to gate RestDayDutyCheckBox. Unlike
    /// _overtimeEligibleOptions/_nightDiffEligibleOptions above, this isn't an
    /// employee-level *default* that a per-day override can still turn on for an
    /// ineligible employee -- Rest Day Pay eligibility has no such override, so an
    /// ineligible employee can never check this box at all (see the constraint this
    /// satisfies in the refactor plan).</summary>
    private readonly bool _allEmployeesEligibleForRestDayPay;

    /// <summary>True when NONE of the selected employees are eligible, as opposed to a
    /// mixed selection where some are and some aren't -- distinguishes which of the two
    /// ToolTip messages UpdateFieldAvailability shows on a disabled RestDayDutyCheckBox.</summary>
    private readonly bool _noEmployeesEligibleForRestDayPay;

    /// <summary>RestDayDutyCheckBox's own XAML ToolTip, captured once after
    /// InitializeComponent so UpdateFieldAvailability can restore it verbatim once the
    /// checkbox becomes enabled again -- see the two ineligibility messages it's
    /// temporarily replaced with otherwise.</summary>
    private readonly object _restDayDutyCheckBoxDefaultToolTip;

    /// <summary>The AttendancePolicy defaults currently in effect (see
    /// AttendanceSettings.Policy) -- shown as gray placeholder text in each new
    /// buffer box so the field never looks blank/unexplained, without actually
    /// becoming a per-segment override (see InitializeBufferBox) unless the
    /// person types something themselves. Split Shift segments have no
    /// employee-level default tier the way Normal's own four buffers do (see
    /// _defaultNormalClockInBufferBeforeHours below) -- these two remain the
    /// plain policy value, unadjusted for whichever employee(s) are selected.</summary>
    private readonly double _defaultClockInBufferHours;
    private readonly double _defaultClockOutBufferHours;

    /// <summary>AttendanceSettings.DefaultWorkTimeHours -- pre-filled into WorkTimeBox
    /// for a brand-new (non-prefill) entry, replacing what used to be a hardcoded "10"
    /// (see the else-branch below). Only used there; an edit of an existing entry
    /// prefills from that entry's own WorkTimeHours instead, same as before.</summary>
    private readonly double _defaultWorkTimeHours;

    /// <summary>Same idea as _defaultClockInBufferHours/_defaultClockOutBufferHours
    /// above, but for Normal's own AttendancePolicy.ClockInBufferBefore/
    /// ClockInBufferAfter/ClockOutBufferBefore/ClockOutBufferAfter defaults. Four
    /// separate values (not one symmetric pair) because those four policy fields
    /// aren't symmetric -- unlike FlexibleSegmentClockInBuffer/
    /// FlexibleSegmentClockOutBuffer above, which each apply the same distance on
    /// both sides.
    ///
    /// Unlike _defaultClockInBufferHours/_defaultClockOutBufferHours, these four
    /// are only the *bottom* of the three-tier cascade DescribeBufferDefault
    /// actually resolves into ClockInBufferBeforeBox/.../ClockOutBufferAfterBox's
    /// gray placeholder text -- each selected employee's own
    /// ClockInBufferBeforeHours/etc. (see Employee) takes priority over the raw
    /// policy value here when set, mirroring the exact cascade
    /// NormalBufferResolver resolves at calculation time. These fields still hold
    /// only the policy-wide value itself, never an employee's -- DescribeBufferDefault
    /// folds the employee tier in fresh from <c>employees</c> each time it's
    /// called, rather than this field being replaced by one.</summary>
    private readonly double _defaultNormalClockInBufferBeforeHours;
    private readonly double _defaultNormalClockInBufferAfterHours;
    private readonly double _defaultNormalClockOutBufferBeforeHours;
    private readonly double _defaultNormalClockOutBufferAfterHours;

    /// <summary>PayrollPolicy.OvertimeRatePercentage/NightDiffRatePercentage
    /// currently in effect (see MainViewModel's own _payrollPolicy field) --
    /// shown as gray placeholder text in OvertimeRatePercentageBox/
    /// NightDiffRatePercentageBox the same way _defaultClockInBufferHours etc.
    /// above are for the buffer boxes, via the same InitializeBufferBox/
    /// ShowBufferDefault machinery (reused here for a rate percentage, not just
    /// hours -- the "grayed-out placeholder vs. real override" behavior is
    /// identical either way, only the unit differs).</summary>
    private readonly decimal _defaultOvertimeRatePercentage;
    private readonly decimal _defaultNightDiffRatePercentage;

    /// <summary>Set once a default segment row has been auto-added on first
    /// switching to Split Shift, so removing it (leaving zero rows, which is legal --
    /// see FlexibleSegments) doesn't cause it to silently reappear.</summary>
    private bool _defaultSegmentAdded;

    public ScheduleType ScheduleType { get; private set; }
    public decimal? WorkTimeHours { get; private set; }
    public TimeOnly? TimeIn { get; private set; }

    /// <summary>Only meaningful when ScheduleType is Leave; null otherwise -- same
    /// nullable-when-inapplicable convention as WorkTimeHours/TimeIn above, and
    /// read the same way by OkButton_Click. See ScheduleEntry.IsPaidLeave for what
    /// true/false mean downstream in PayrollCalculator. PaidLeaveRadio/UnpaidLeaveRadio
    /// default to Paid in the XAML, but the constructor below re-seeds that default
    /// from the single selected employee's Employee.DefaultLeaveIsPaid for a
    /// brand-new (non-prefill) entry -- see there for why multi-employee stays Paid.</summary>
    public bool? IsPaidLeave { get; private set; }

    /// <summary>Only meaningful when ScheduleType is SplitShift -- that day's
    /// allowed punching windows. Empty (not null) otherwise, and empty is also
    /// legal for SplitShift itself (a split shift day with no windows configured
    /// yet). ClockInBufferHours/ClockOutBufferHours are optional per-segment
    /// overrides for AttendancePolicy's FlexibleSegmentClockInBuffer/
    /// FlexibleSegmentClockOutBuffer defaults (see FlexibleSegment) -- null (the
    /// common case) means "use the policy default", which the UI shows as gray
    /// placeholder text in the buffer boxes rather than leaving them blank (see
    /// InitializeBufferBox); typing over that placeholder is what turns it into a
    /// real override here. Property name kept as FlexibleSegments rather than
    /// renamed to SplitShiftSegments -- same "no renaming needed" call as
    /// AttendancePolicy.FlexibleSegmentClockInBuffer/FlexibleSegmentClockOutBuffer
    /// (see SettingsDialog, which only relabeled their on-screen text).</summary>
    public IReadOnlyList<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)> FlexibleSegments { get; private set; } =
        Array.Empty<(TimeOnly, TimeOnly, double?, double?)>();

    /// <summary>Only meaningful when ScheduleType is Flexible -- that day's
    /// single required punching window (see
    /// ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut). This dialog now
    /// requires both RestrictedTimeIn and RestrictedTimeOut for Flexible
    /// (OkButton_Click blocks OK until both are picked, the same way
    /// TimeInPicker is required for Normal/Official Business), so this is
    /// never actually null once the dialog closes with a Flexible result --
    /// it's only nullable here because the underlying ScheduleEntry field is
    /// (other callers, e.g. the Excel importer, still allow an unrestricted
    /// Flexible day; this dialog just no longer offers a way to produce one,
    /// which also sidesteps the old "how do I clear it back to blank" gap the
    /// three-dropdown TimePicker this dialog used to use had). Both pickers
    /// default to 5:00 AM/9:00 PM for a brand-new entry, same starting values
    /// SplitShift seeds its first segment with (see UpdateFieldAvailability)
    /// -- see the constructor. Always null for
    /// Normal/Leave/OfficialBusiness/SplitShift.</summary>
    public TimeOnly? RestrictedTimeIn { get; private set; }

    /// <summary>Same idea as <see cref="RestrictedTimeIn"/>, but for the end of
    /// Flexible's single allowed punching window.</summary>
    public TimeOnly? RestrictedTimeOut { get; private set; }

    /// <summary>Only meaningful when ScheduleType is Normal -- optional per-day
    /// overrides for AttendancePolicy's ClockInBufferBefore/ClockInBufferAfter/
    /// ClockOutBufferBefore/ClockOutBufferAfter defaults, or for whichever
    /// employee-level default (see Employee.ClockInBufferBeforeHours/etc.) sits
    /// between this day and that policy value -- see ScheduleEntry,
    /// NormalBufferResolver. Null (the common case) means "use whichever of
    /// those two is in effect for this employee", shown as gray placeholder text
    /// the same way FlexibleSegments' per-segment overrides are (see
    /// InitializeBufferBox/DescribeBufferDefault); typing over that placeholder
    /// is what turns it into a real, day-specific override. Always null for
    /// Flexible/Leave.</summary>
    public double? ClockInBufferBeforeHours { get; private set; }
    public double? ClockInBufferAfterHours { get; private set; }
    public double? ClockOutBufferBeforeHours { get; private set; }
    public double? ClockOutBufferAfterHours { get; private set; }

    /// <summary>Per-day overrides added by the Attendance & Payroll Calculation
    /// Refactor Plan -- see the matching properties on ScheduleEntry for what each
    /// means and who reads it. Only meaningful for Normal/Flexible/SplitShift (see
    /// OvertimeNightDiffLabel/OvertimeNightDiffBorder's XAML comment); always null
    /// for Leave/Official Business, same nullable-when-inapplicable convention as
    /// ClockInBufferBeforeHours etc. above. Null (the common case) means "no
    /// override, inherit the employee-level/global default" for all five.</summary>
    public bool? OvertimeEligibleOverride { get; private set; }
    public bool? NightDiffEligibleOverride { get; private set; }
    public bool? ApplyOvertimeRatePercentageOverride { get; private set; }
    public decimal? OvertimeRatePercentageOverride { get; private set; }
    public decimal? NightDiffRatePercentageOverride { get; private set; }

    public ApplyScheduleDialog(
        IReadOnlyList<Employee> employees,
        IReadOnlyList<(DateOnly Start, DateOnly End)> ranges,
        bool isEditing,
        ScheduleEntry? prefill,
        ScheduleType? initialType,
        double defaultClockInBufferHours,
        double defaultClockOutBufferHours,
        double defaultNormalClockInBufferBeforeHours,
        double defaultNormalClockInBufferAfterHours,
        double defaultNormalClockOutBufferBeforeHours,
        double defaultNormalClockOutBufferAfterHours,
        double defaultWorkTimeHours,
        decimal defaultOvertimeRatePercentage,
        decimal defaultNightDiffRatePercentage)
    {
        InitializeComponent();

        _restDayDutyCheckBoxDefaultToolTip = RestDayDutyCheckBox.ToolTip;
        _allEmployeesEligibleForRestDayPay = employees.All(e => e.QualifiesForRestDayPay);
        _noEmployeesEligibleForRestDayPay = employees.All(e => !e.QualifiesForRestDayPay);

        _defaultClockInBufferHours = defaultClockInBufferHours;
        _defaultClockOutBufferHours = defaultClockOutBufferHours;
        _defaultNormalClockInBufferBeforeHours = defaultNormalClockInBufferBeforeHours;
        _defaultNormalClockInBufferAfterHours = defaultNormalClockInBufferAfterHours;
        _defaultNormalClockOutBufferBeforeHours = defaultNormalClockOutBufferBeforeHours;
        _defaultNormalClockOutBufferAfterHours = defaultNormalClockOutBufferAfterHours;
        _defaultWorkTimeHours = defaultWorkTimeHours;
        _defaultOvertimeRatePercentage = defaultOvertimeRatePercentage;
        _defaultNightDiffRatePercentage = defaultNightDiffRatePercentage;

        _overtimeEligibleOptions = new[]
        {
            DescribeEmployeeDefault(employees, e => e.QualifiesForOvertime, "Eligible", "Not eligible"),
            "Eligible", "Not eligible"
        };
        _nightDiffEligibleOptions = new[]
        {
            DescribeEmployeeDefault(employees, e => e.QualifiesForNightDiff, "Eligible", "Not eligible"),
            "Eligible", "Not eligible"
        };
        _applyRateOptions = new[]
        {
            DescribeEmployeeDefault(employees, e => e.ApplyOvertimeRatePercentageByDefault, "Apply premium", "Straight time only"),
            "Apply premium", "Straight time only"
        };

        OvertimeEligibleCombo.ItemsSource = _overtimeEligibleOptions;
        NightDiffEligibleCombo.ItemsSource = _nightDiffEligibleOptions;
        ApplyOvertimeRatePercentageCombo.ItemsSource = _applyRateOptions;

        if (employees.Count == 1)
        {
            Title = isEditing ? "Edit Schedule for Selected Days" : "Set Schedule for Selected Days";
            EmployeeHeader.Text = employees[0].DisplayName;
        }
        else
        {
            // No prefill/edit framing for the multi-employee flow -- see
            // MainViewModel.AssignScheduleToCheckedEmployeesAsync.
            Title = $"Set Schedule for {employees.Count} Employees";

            const int maxNamesShown = 5;
            var names = employees.Select(e => e.DisplayName).ToList();
            var shown = string.Join(", ", names.Take(maxNamesShown));
            var suffix = names.Count > maxNamesShown ? $", and {names.Count - maxNamesShown} more" : string.Empty;
            EmployeeHeader.Text = $"{employees.Count} employees selected: {shown}{suffix}";
        }

        var totalDays = ranges.Sum(r => r.End.DayNumber - r.Start.DayNumber + 1);
        var lines = ranges.Select(r => r.Start == r.End
            ? r.Start.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            : $"{r.Start:MMM d} - {r.End:MMM d, yyyy}");

        SummaryText.Text = string.Join("\n", lines) +
            $"\n\nTotal: {totalDays} day(s) across {ranges.Count} range(s)";

        // isEditing but no prefill means the selection has existing schedules that
        // don't all match -- there's something to overwrite, just no single shared
        // answer to show. MixedScheduleNote explains why the fields below start
        // blank instead of leaving that unexplained.
        MixedScheduleNote.Visibility = isEditing && prefill is null ? Visibility.Visible : Visibility.Collapsed;

        TypeCombo.ItemsSource = Enum.GetValues<ScheduleType>();

        if (prefill is not null)
        {
            // Suppress UpdateFieldAvailability's "auto-seed a default 5:00 AM-9:00 PM
            // window" below -- the real prefilled segments (or intentionally zero,
            // if that's what the day being edited had) are added explicitly instead.
            _defaultSegmentAdded = true;

            // initialType (set when opened from the calendar's right-click "Set
            // Schedule As" submenu -- see MainViewModel.SetScheduleForSelectionAsync)
            // wins over whatever type the existing entry happens to be, since picking
            // a specific type from that submenu is the person saying "make this a
            // Leave day" (etc.) regardless of what it currently is. The other fields
            // below still come from prefill either way -- there's no single sensible
            // "starting" hours/time-in for an arbitrary type change, so this is really
            // just a shortcut for the type dropdown itself, same as the null case.
            TypeCombo.SelectedItem = initialType ?? prefill.ScheduleType;
            WorkTimeBox.Text = prefill.WorkTimeHours is { } hours ? hours.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
            TimeInPicker.SelectedTime = prefill.TimeIn;

            // Fall back to the same 5:00 AM/9:00 PM starting values a brand-new
            // entry gets (see the else-branch below) when the prefill predates
            // the "make Flexible restricted" change and has no saved window --
            // this dialog no longer offers a way to leave either picker blank
            // for Flexible (see RestrictedTimeIn's own doc comment), so an old
            // null here needs a starting value rather than reproducing a state
            // OK won't accept.
            RestrictedTimeInPicker.SelectedTime = prefill.RestrictedTimeIn ?? new TimeOnly(5, 0);
            RestrictedTimeOutPicker.SelectedTime = prefill.RestrictedTimeOut ?? new TimeOnly(21, 0);

            // False is the only case that needs an explicit radio flip -- true and
            // null (not a saved Leave day, or an older row from before this field
            // existed) both read the same as the Paid default already checked in
            // the XAML.
            UnpaidLeaveRadio.IsChecked = prefill.IsPaidLeave == false;

            foreach (var seg in prefill.FlexibleSegments.OrderBy(s => s.TimeIn))
                AddSegmentRow(
                    seg.TimeIn, seg.TimeOut,
                    seg.ClockInBufferHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                    seg.ClockOutBufferHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                    isExistingSegment: true);
        }
        else
        {
            TypeCombo.SelectedItem = initialType ?? ScheduleType.Normal;
            WorkTimeBox.Text = ResolveWorkTimeDefault(employees, _defaultWorkTimeHours)
                .ToString("0.##", CultureInfo.InvariantCulture);
            TimeInPicker.SelectedTime = new TimeOnly(5, 0);

            // Same starting values as TimeInPicker's own default above, and the
            // same 5:00 AM-9:00 PM SplitShift seeds its first segment with (see
            // UpdateFieldAvailability) -- these two are only ever read back when
            // ScheduleType is Flexible, so setting them here even though the
            // type combo may default to Normal is harmless (mirrors TimeInPicker
            // being pre-filled regardless of the starting type).
            RestrictedTimeInPicker.SelectedTime = new TimeOnly(5, 0);
            RestrictedTimeOutPicker.SelectedTime = new TimeOnly(21, 0);

            // Brand-new entry, nothing saved yet to prefill IsPaidLeave from --
            // seed the Paid/Unpaid radio from the employee's own
            // Employee.DefaultLeaveIsPaid instead of just leaving the XAML's
            // hardcoded Paid default in place, so the common per-employee choice
            // doesn't have to be re-picked by hand every time this employee gets
            // a Leave day. Single-employee only: with several employees selected
            // (the multi-select "Set Schedule for N Employees" flow) there's no
            // single sensible default when they don't all agree, so that case
            // just keeps the XAML's plain Paid default, same as before this
            // setting existed. Either way, this is only ever a starting point --
            // the person can still flip the radio themselves before OK.
            if (employees.Count == 1)
                UnpaidLeaveRadio.IsChecked = employees[0].DefaultLeaveIsPaid == false;
        }

        // Seeds RestDayDutyCheckBox from whichever of the two branches above ran --
        // checked only when there's an actual prior schedule to reflect (an existing
        // RestDay entry whose WorkTimeHours/TimeIn were both filled in, i.e. the
        // windowed sub-case). A prefill for some other ScheduleType has nothing
        // meaningful to check this from (WorkTimeHours/TimeIn above just came from
        // that other type's own use of the same two fields, e.g. Normal's shift
        // start), and a brand-new entry has no prior schedule at all -- both fall
        // through to unchecked, same as the RestDay-is-unscheduled-by-default call
        // in UpdateFieldAvailability's own doc comment. Read here rather than in
        // UpdateFieldAvailability itself since it's a one-time starting value, not
        // something that should be recomputed every time the type combo changes.
        RestDayDutyCheckBox.IsChecked =
            prefill is { ScheduleType: ScheduleType.RestDay } &&
            (prefill.WorkTimeHours is not null || prefill.TimeIn is not null);

        // isExistingNormalEntry (rather than just "prefill is not null") -- a
        // prefill whose ScheduleType wasn't Normal never had these fields set
        // (they're Normal-only, see ScheduleEntry), so there's no previously-saved
        // value in effect for this day to distinguish from a fresh gray suggestion.
        var isExistingNormalEntry = prefill is { ScheduleType: ScheduleType.Normal };
        InitializeBufferBox(ClockInBufferBeforeBox,
            prefill?.ClockInBufferBeforeHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
            DescribeBufferDefault(employees, e => e.ClockInBufferBeforeHours, _defaultNormalClockInBufferBeforeHours),
            isExistingNormalEntry);
        InitializeBufferBox(ClockInBufferAfterBox,
            prefill?.ClockInBufferAfterHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
            DescribeBufferDefault(employees, e => e.ClockInBufferAfterHours, _defaultNormalClockInBufferAfterHours),
            isExistingNormalEntry);
        InitializeBufferBox(ClockOutBufferBeforeBox,
            prefill?.ClockOutBufferBeforeHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
            DescribeBufferDefault(employees, e => e.ClockOutBufferBeforeHours, _defaultNormalClockOutBufferBeforeHours),
            isExistingNormalEntry);
        InitializeBufferBox(ClockOutBufferAfterBox,
            prefill?.ClockOutBufferAfterHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
            DescribeBufferDefault(employees, e => e.ClockOutBufferAfterHours, _defaultNormalClockOutBufferAfterHours),
            isExistingNormalEntry);

        // OvertimeHours/NightDiffHours are only ever computed for Normal, Flexible, and
        // SplitShift (see OvertimeNightDiffLabel's XAML comment) -- unlike
        // isExistingNormalEntry above, this covers all three, since these five
        // overrides apply to any of them.
        var isExistingOvertimeNightDiffEntry = prefill is { ScheduleType: ScheduleType.Normal or ScheduleType.Flexible or ScheduleType.SplitShift };

        OvertimeEligibleCombo.SelectedIndex = TriStateIndexFromNullableBool(prefill?.OvertimeEligibleOverride);
        NightDiffEligibleCombo.SelectedIndex = TriStateIndexFromNullableBool(prefill?.NightDiffEligibleOverride);
        ApplyOvertimeRatePercentageCombo.SelectedIndex = TriStateIndexFromNullableBool(prefill?.ApplyOvertimeRatePercentageOverride);

        InitializeBufferBox(OvertimeRatePercentageBox,
            prefill?.OvertimeRatePercentageOverride?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
            _defaultOvertimeRatePercentage.ToString("0.##", CultureInfo.InvariantCulture), isExistingOvertimeNightDiffEntry);
        InitializeBufferBox(NightDiffRatePercentageBox,
            prefill?.NightDiffRatePercentageOverride?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
            _defaultNightDiffRatePercentage.ToString("0.##", CultureInfo.InvariantCulture), isExistingOvertimeNightDiffEntry);

        UpdateFieldAvailability();
    }

    /// <summary>Maps a nullable override bool to the shared 0/1/2 tri-state
    /// ComboBox convention (see _overtimeEligibleOptions/_nightDiffEligibleOptions/
    /// _applyRateOptions) -- null (no override) is always index 0 regardless of
    /// which of the three option lists is in use.</summary>
    private static int TriStateIndexFromNullableBool(bool? value) => value switch
    {
        null => 0,
        true => 1,
        false => 2,
    };

    /// <summary>The inverse of <see cref="TriStateIndexFromNullableBool"/>, read
    /// back by OkButton_Click for each of the three tri-state ComboBoxes.</summary>
    private static bool? ReadTriStateOverride(ComboBox combo) => combo.SelectedIndex switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateFieldAvailability();

    /// <summary>Just re-runs UpdateFieldAvailability -- RestDayDutyCheckBox only
    /// ever changes what's visible/required below it, it doesn't validate or
    /// read anything itself (that's still OkButton_Click, via isUnscheduledRestDay
    /// there).</summary>
    private void RestDayDutyCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        => UpdateFieldAvailability();

    private void UpdateFieldAvailability()
    {
        var selected = TypeCombo.SelectedItem as ScheduleType?;
        var isLeave = selected is ScheduleType.Leave;
        var isOfficialBusiness = selected is ScheduleType.OfficialBusiness;
        var isFlexible = selected is ScheduleType.Flexible;
        var isSplitShift = selected is ScheduleType.SplitShift;

        // RestDay is the one type where WorkTimeBox/TimeInPicker are BOTH shown
        // (like Normal/Official Business) AND optional (unlike anything else) --
        // see RestDayShiftCalculationStrategy, which picks between two whole
        // different calculation shapes depending on whether they're set: left
        // blank, it's a plain day off and punches are never looked at at all
        // (CalculateUnscheduledDay -- no Rest Day Duty pay is possible no
        // matter what was punched); filled in, it's matched against a single
        // buffer-window exactly like Normal (CalculateWindowedDay, reusing
        // SingleWindowShiftCalculationStrategy) and only that windowed match
        // can earn the premium. RestDayDutyCheckBox (visible only for RestDay,
        // just below) is what actually chooses between the two now -- see its
        // own XAML comment for why a checkbox replaced the old "leave both
        // fields blank" convention.
        var isRestDay = selected is ScheduleType.RestDay;

        // Only employees with QualifiesForRestDayPay can ever earn Rest Day Pay (see
        // Employee.QualifiesForRestDayPay), so "Scheduled duty" is disabled outright --
        // not just left uncheckable -- rather than silently letting someone schedule a
        // duty day for an employee it can never pay out for. Guard the assignment so
        // a checked box only gets forced unchecked (and Unchecked fires, re-entering
        // this method via RestDayDutyCheckBox_CheckedChanged) when it's actually
        // necessary, rather than on every unrelated call while already unchecked.
        if (isRestDay && !_allEmployeesEligibleForRestDayPay)
        {
            if (RestDayDutyCheckBox.IsChecked == true) RestDayDutyCheckBox.IsChecked = false;
            RestDayDutyCheckBox.IsEnabled = false;
            RestDayDutyCheckBox.ToolTip = _noEmployeesEligibleForRestDayPay
                ? "None of the selected employees are eligible for Rest Day Pay."
                : "Not all selected employees are eligible for Rest Day Pay.";
        }
        else
        {
            RestDayDutyCheckBox.IsEnabled = true;
            RestDayDutyCheckBox.ToolTip = _restDayDutyCheckBoxDefaultToolTip;
        }

        var isRestDayDuty = isRestDay && RestDayDutyCheckBox.IsChecked == true;

        RestDayDutyCheckBox.Visibility = isRestDay ? Visibility.Visible : Visibility.Collapsed;

        // Leave needs none of these -- it skips punch matching entirely (see
        // LeaveShiftCalculationStrategy) and has no scheduled window at all.
        // Normal needs a shift length (WorkTimeBox) and a TimeIn (shift start,
        // TimeOut is then derived). Official Business also skips punch
        // matching (see OfficialBusinessShiftCalculationStrategy) but, like
        // Normal, still takes a WorkTimeBox/TimeIn pair -- the employee is
        // credited for that scheduled window without having to actually punch
        // in or out. Flexible and SplitShift both need a required-hours total
        // (WorkTimeBox) but no single TimeIn -- Flexible's optional single-window
        // restriction lives in the Restricted punching window section below
        // instead, and SplitShift's windows live in the segments section below
        // instead. RestDay shares Normal/Official Business' shown pair of fields,
        // but only once RestDayDutyCheckBox is checked -- left unchecked, this
        // day has no fixed schedule at all, so the fields have nothing to show
        // (isRestDay && !isRestDayDuty hides them below, same as Leave).
        var hideWorkTimeAndTimeIn = isLeave || (isRestDay && !isRestDayDuty);
        WorkTimeLabel.Visibility = WorkTimeBox.Visibility = hideWorkTimeAndTimeIn ? Visibility.Collapsed : Visibility.Visible;
        WorkTimeBox.IsEnabled = !isLeave;

        TimeInLabel.Visibility = TimeInPicker.Visibility =
            hideWorkTimeAndTimeIn || isFlexible || isSplitShift ? Visibility.Collapsed : Visibility.Visible;
        TimeInPicker.IsEnabled = !isLeave && !isFlexible && !isSplitShift;

        // No more RestDay-specific wording needed now that RestDayDutyCheckBox
        // (just above) is what tells the person these fields are optional --
        // once shown (isRestDayDuty), they're required exactly like Normal's.
        WorkTimeLabel.Text = "Work time (hours)";
        TimeInLabel.Text = "Time in";
        WorkTimeBox.ToolTip = "Shift length for Normal. For Flexible/Split Shift, this is the day's required total hours instead -- there's no single shift to measure a length from. For a Rest Day duty, this is the scheduled length punches are matched against.";

        // Shares rows 7-8 with WorkTimeLabel/WorkTimeBox/TimeInLabel/TimeInPicker
        // above -- see the XAML comment on LeavePayLabel for why that's safe.
        LeavePayLabel.Visibility = LeavePayPanel.Visibility = isLeave ? Visibility.Visible : Visibility.Collapsed;

        RestrictedTimeLabel.Visibility = RestrictedTimeBorder.Visibility =
            isFlexible ? Visibility.Visible : Visibility.Collapsed;

        SegmentsLabel.Visibility = SegmentsBorder.Visibility = AddSegmentButton.Visibility =
            isSplitShift ? Visibility.Visible : Visibility.Collapsed;

        // Normal and RestDay-as-duty -- these buffers only mean anything when
        // punches are actually matched against a window (see
        // SingleWindowShiftCalculationStrategy), which Leave and Official
        // Business both skip entirely, so both stay excluded here even though
        // Official Business shares Normal's WorkTimeBox/TimeInPicker pair above.
        // A RestDay left unchecked (isRestDayDuty false) skips that matching too
        // -- unlike before RestDayDutyCheckBox existed, that's now known for
        // certain at this point rather than only after OkButton_Click reads the
        // fields back, so the boxes are hidden outright rather than left
        // visible-but-harmless. Shares rows 9-10 with the Restricted punching
        // window UI and the Split Shift segments UI above -- see the XAML
        // comment on NormalBufferLabel for why that's safe.
        NormalBufferLabel.Visibility = NormalBufferBorder.Visibility =
            (!isLeave && !isOfficialBusiness && !isFlexible && !isSplitShift && !hideWorkTimeAndTimeIn) ? Visibility.Visible : Visibility.Collapsed;

        // Overtime/Night Diff overrides -- unlike the Normal/RestDay-only buffers
        // just above, this section stays visible for RestDay too (exclusion list
        // is one shorter than the buffers'), though RestDay only ever actually
        // acts on the Night Diff half: RestDayShiftCalculationStrategy's own doc
        // comment is explicit that OvertimeHours is never populated for RestDay in
        // either mode, so its Overtime-eligible/rate-override controls are
        // presently harmless-but-inert whenever RestDay is selected (a separate,
        // pre-existing gap from the one this pass fixes -- not addressed here).
        // See OvertimeNightDiffLabel's own XAML comment for why Leave/Official
        // Business are excluded.
        OvertimeNightDiffLabel.Visibility = OvertimeNightDiffBorder.Visibility =
            !isLeave && !isOfficialBusiness ? Visibility.Visible : Visibility.Collapsed;

        // Seed one default window the first time Split Shift is selected, matching
        // the app's old single-window default -- purely a starting point, since
        // zero segments is legal and the user is free to remove it.
        if (isSplitShift && !_defaultSegmentAdded && _segmentRows.Count == 0)
        {
            _defaultSegmentAdded = true;
            AddSegmentRow(new TimeOnly(5, 0), new TimeOnly(21, 0));
        }
    }

    private void AddSegmentButton_Click(object sender, RoutedEventArgs e) => AddSegmentRow();

    /// <summary>Sets up a buffer-override TextBox's starting state and the
    /// GotFocus/TextChanged/LostFocus trio that keeps it behaving as a default
    /// display rather than a real value until the person actually edits it -- see
    /// ShowBufferDefault for what "default state" means, how it's styled
    /// differently for a brand-new row versus one loaded from an already-saved
    /// segment, and how OkButton_Click reads it back. Shared by AddSegmentRow's
    /// per-segment Split Shift boxes, the Overtime/Night Diff rate-percentage
    /// boxes, and the constructor's fixed Normal-level ClockInBufferBeforeBox/
    /// .../ClockOutBufferAfterBox -- isExistingSegment means "existing saved
    /// value" in all three cases, not literally a FlexibleSegment.
    ///
    /// <paramref name="defaultDisplayText"/> is a pre-formatted string rather than
    /// a raw number -- unlike every other caller, the four Normal-level boxes'
    /// default can legitimately be "(varies)" rather than an actual figure (see
    /// DescribeBufferDefault), which a plain double couldn't represent. Nothing
    /// downstream needs the numeric value itself: TryReadBufferOverride reads
    /// Tag, not this text, to decide "no override," so a caller with a real
    /// number just formats it the same "0.##" way ShowBufferDefault always
    /// has.</summary>
    private static void InitializeBufferBox(TextBox box, string overrideText, string defaultDisplayText, bool isExistingSegment)
    {
        if (!string.IsNullOrWhiteSpace(overrideText))
        {
            // A real override loaded from an existing FlexibleSegment via prefill
            // -- shown as-is, normal color, Tag left at its default (null, i.e.
            // "not a placeholder").
            box.Text = overrideText;
        }
        else
        {
            ShowBufferDefault(box, defaultDisplayText, isExistingSegment);
        }

        // Selects rather than clears: the box already shows either a real value
        // or the policy default (see above), and highlighting it lets typing
        // immediately replace it -- the usual "click a prefilled field and just
        // start typing" convention -- while still letting the person see, and
        // click past without losing, whatever was already there. SelectAll alone
        // only reliably works for keyboard (Tab) focus; a mouse click re-places
        // the caret afterward and cancels the selection unless the click that
        // caused the focus is intercepted too, hence PreviewMouseLeftButtonDown
        // below (a well-known WPF TextBox quirk, not redundant with GotFocus).
        box.GotFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsFocused) return;

            e.Handled = true;
            box.Focus();
        };

        box.TextChanged += (_, _) =>
        {
            // Tag is true only while the box is still showing the policy default
            // untouched (see ShowBufferDefault) -- the first real edit (typing
            // over the selection above, pasting, deleting a character, anything)
            // means this is now a real value the person is entering, not the
            // default anymore, so it stops being styled as one and stops being
            // read back as "no override" in OkButton_Click.
            if (box.Tag is true)
            {
                box.Tag = null;
                box.ClearValue(TextBox.ForegroundProperty);
            }
        };

        box.LostFocus += (_, _) =>
        {
            // Left empty -- either never typed anything, or typed then deleted it
            // all again -- means "no override": restore the default display
            // rather than leaving a blank box with no indication of what value is
            // actually in effect.
            if (string.IsNullOrWhiteSpace(box.Text))
                ShowBufferDefault(box, defaultDisplayText, isExistingSegment);
        };
    }

    /// <summary>Puts a buffer-override TextBox into "showing the default, not a
    /// real value" state: Tag set to true so OkButton_Click's read-back
    /// (and InitializeBufferBox's TextChanged handler above) can tell default
    /// text apart from something the person actually typed -- including the edge
    /// case where they type the same text that was already showing, which does
    /// count as a real override (any edit at all clears Tag, regardless of what
    /// the resulting text happens to be). <paramref name="defaultDisplayText"/>
    /// is shown verbatim -- see InitializeBufferBox's own doc comment for why
    /// this is a pre-formatted string rather than a raw number.
    ///
    /// Styled differently depending on isExistingSegment: gray for a brand-new
    /// row the person is actively deciding on (nothing saved yet, so it really is
    /// just a hint), normal/black for a row loaded from an already-saved
    /// FlexibleSegment (see the prefill loop in the constructor) -- that one *is*
    /// the value currently in effect for this schedule, just inherited rather
    /// than overridden, so graying it out on re-edit would misleadingly suggest
    /// the save didn't take.</summary>
    private static void ShowBufferDefault(TextBox box, string defaultDisplayText, bool isExistingSegment)
    {
        box.Text = defaultDisplayText;
        if (isExistingSegment)
            box.ClearValue(TextBox.ForegroundProperty);
        else
            box.Foreground = System.Windows.Media.Brushes.Gray;
        box.Tag = true;
    }

    private void AddSegmentRow(
        TimeOnly? timeIn = null, TimeOnly? timeOut = null,
        string clockInBufferText = "", string clockOutBufferText = "",
        bool isExistingSegment = false)
    {
        var container = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var timeInPicker = new TimeInput
        {
            SelectedTime = timeIn,
            Density = TimeInputDensity.Compact,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(timeInPicker, 0);
        Grid.SetRow(timeInPicker, 0);

        var dash = new TextBlock
        {
            Text = "-",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(dash, 1);
        Grid.SetRow(dash, 0);

        var timeOutPicker = new TimeInput
        {
            SelectedTime = timeOut,
            Density = TimeInputDensity.Compact,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(timeOutPicker, 2);
        Grid.SetRow(timeOutPicker, 0);

        var removeButton = new Button { Content = "✕", Width = 24, Padding = new Thickness(0) };
        Grid.SetColumn(removeButton, 3);
        Grid.SetRow(removeButton, 0);

        // Optional per-segment overrides for AttendancePolicy's
        // FlexibleSegmentClockInBuffer/FlexibleSegmentClockOutBuffer (see
        // FlexibleSegment.ClockInBufferHours/ClockOutBufferHours) -- shown grayed
        // out at the current policy default rather than blank (see
        // InitializeBufferBox); a segment left untouched just keeps inheriting
        // whatever's configured in Settings. Laid out on their own row under the
        // times rather than crammed onto the same line, since the dialog isn't
        // wide enough for both.
        var bufferPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(bufferPanel, 0);
        Grid.SetColumnSpan(bufferPanel, 3);
        Grid.SetRow(bufferPanel, 1);

        var clockInBufferBox = new TextBox
        {
            Width = 40,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Clock-in search window override, in hours either side of the start time above. " +
                "Shown grayed-out at the current policy default -- type over it to set an override just " +
                "for this segment, or leave it as-is to keep tracking the policy default even if it " +
                "changes later."
        };
        var clockOutBufferBox = new TextBox
        {
            Width = 40,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Clock-out search window override, in hours either side of the end time above. " +
                "Shown grayed-out at the current policy default -- type over it to set an override just " +
                "for this segment, or leave it as-is to keep tracking the policy default even if it " +
                "changes later."
        };
        InitializeBufferBox(clockInBufferBox, clockInBufferText,
            _defaultClockInBufferHours.ToString("0.##", CultureInfo.InvariantCulture), isExistingSegment);
        InitializeBufferBox(clockOutBufferBox, clockOutBufferText,
            _defaultClockOutBufferHours.ToString("0.##", CultureInfo.InvariantCulture), isExistingSegment);

        bufferPanel.Children.Add(new TextBlock
        {
            Text = "In buffer (h):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray
        });
        bufferPanel.Children.Add(clockInBufferBox);
        bufferPanel.Children.Add(new TextBlock
        {
            Text = "Out buffer (h):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray
        });
        bufferPanel.Children.Add(clockOutBufferBox);

        container.Children.Add(timeInPicker);
        container.Children.Add(dash);
        container.Children.Add(timeOutPicker);
        container.Children.Add(removeButton);
        container.Children.Add(bufferPanel);

        var row = new SegmentRow(container, timeInPicker, timeOutPicker, clockInBufferBox, clockOutBufferBox);
        removeButton.Click += (_, _) => RemoveSegmentRow(row);

        _segmentRows.Add(row);
        SegmentsPanel.Children.Add(container);
    }

    private void RemoveSegmentRow(SegmentRow row)
    {
        _segmentRows.Remove(row);
        SegmentsPanel.Children.Remove(row.Container);
    }

    /// <summary>Reads one buffer-override TextBox back into a nullable double for
    /// OkButton_Click: null (with no warning) if the box is still showing its
    /// grayed-out policy-default placeholder (Tag is true, see ShowBufferDefault)
    /// or was left/cleared empty -- either way meaning "no override, inherit the
    /// policy default" -- otherwise the parsed value. Returns false, having
    /// already shown a warning naming <paramref name="fieldLabel"/>, if the box
    /// has real text that isn't a valid non-negative number. Shared by the
    /// Split Shift per-segment loop, the Normal per-day fields, and the Overtime/
    /// Night Diff rate-percentage overrides below, since all three read the same
    /// "gray placeholder vs. real override" TextBox state set up by
    /// InitializeBufferBox -- <paramref name="unit"/> exists so the warning text
    /// still reads correctly for the rate-percentage boxes, which aren't measured
    /// in hours the way every other caller's box is.</summary>
    private static bool TryReadBufferOverride(TextBox box, string fieldLabel, out double? value, string unit = "hours")
    {
        value = null;
        if (box.Tag is true || string.IsNullOrWhiteSpace(box.Text))
            return true;

        if (!double.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            Warn($"Enter a valid {fieldLabel} in {unit} (0 or more), or leave it at the grayed-out policy default.");
            return false;
        }

        value = parsed;
        return true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (TypeCombo.SelectedItem is not ScheduleType scheduleType)
        {
            Warn("Select a type.");
            return;
        }

        decimal? workTime = null;
        TimeOnly? timeIn = null;
        TimeOnly? restrictedTimeIn = null;
        TimeOnly? restrictedTimeOut = null;
        bool? isPaidLeave = null;
        double? clockInBufferBeforeHours = null;
        double? clockInBufferAfterHours = null;
        double? clockOutBufferBeforeHours = null;
        double? clockOutBufferAfterHours = null;
        bool? overtimeEligibleOverride = null;
        bool? nightDiffEligibleOverride = null;
        bool? applyOvertimeRatePercentageOverride = null;
        decimal? overtimeRatePercentageOverride = null;
        decimal? nightDiffRatePercentageOverride = null;
        var segments = new List<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)>();

        if (scheduleType == ScheduleType.Leave)
        {
            isPaidLeave = !(UnpaidLeaveRadio.IsChecked == true);
        }
        else
        {
            // RestDay is the only type where WorkTimeBox/TimeInPicker are optional
            // rather than required -- see isRestDay's doc comment in
            // UpdateFieldAvailability. RestDayDutyCheckBox (unchecked, or not
            // RestDay at all) means the unscheduled, plain-day-off mode
            // (workTime and timeIn both stay null, their already-initialized
            // default -- no punch that day can ever earn Rest Day Duty pay);
            // checked means both fields are filled and required, same
            // as Normal below -- UpdateFieldAvailability hides them entirely
            // whenever the checkbox is unchecked, so there's no longer a
            // one-filled-one-blank state reachable from the UI to guard against
            // here (that used to be read straight off the two fields' blankness;
            // now it's read off the checkbox instead, since TimeInPicker itself
            // has no way to get back to blank once a time's been picked -- see
            // the checkbox's own XAML comment).
            var isUnscheduledRestDay = scheduleType == ScheduleType.RestDay && RestDayDutyCheckBox.IsChecked != true;

            // Declared out here (rather than via "out var hours" below) so the
            // Flexible/SplitShift sections further down -- which are never
            // RestDay, so isUnscheduledRestDay is always false for them and this
            // always runs -- can still read it back for their own required-hours
            // checks the same way they did before this RestDay branch existed.
            decimal hours = 0;
            if (!isUnscheduledRestDay)
            {
                if (!decimal.TryParse(WorkTimeBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out hours) || hours <= 0)
                {
                    Warn(scheduleType is ScheduleType.Flexible or ScheduleType.SplitShift
                        ? "Enter a valid required hours total, e.g. 8 or 8.5."
                        : "Enter a valid work time in hours, e.g. 9 or 9.5.");
                    return;
                }

                workTime = hours;
            }

            // Overtime/Night Diff overrides -- Normal, Flexible, and SplitShift
            // all compute OvertimeHours/NightDiffHours (see OvertimeNightDiffLabel's
            // XAML comment), so all three read these back; Official Business
            // never does, and the section is hidden for it (see
            // UpdateFieldAvailability) so there's nothing meaningful to read
            // there.
            if (scheduleType != ScheduleType.OfficialBusiness)
            {
                overtimeEligibleOverride = ReadTriStateOverride(OvertimeEligibleCombo);
                nightDiffEligibleOverride = ReadTriStateOverride(NightDiffEligibleCombo);
                applyOvertimeRatePercentageOverride = ReadTriStateOverride(ApplyOvertimeRatePercentageCombo);

                if (!TryReadBufferOverride(OvertimeRatePercentageBox, "overtime rate percentage", out var otRate, unit: "as a decimal, e.g. 0.25 for 25%")) return;
                if (!TryReadBufferOverride(NightDiffRatePercentageBox, "night diff rate percentage", out var ndRate, unit: "as a decimal, e.g. 0.10 for 10%")) return;
                overtimeRatePercentageOverride = (decimal?)otRate;
                nightDiffRatePercentageOverride = (decimal?)ndRate;
            }

            if (scheduleType == ScheduleType.Flexible)
            {
                // Both required (see RestrictedTimeIn's own doc comment) --
                // unlike SplitShift's segments below, there's still no
                // overlap/total-hours check against WorkTimeBox's required
                // hours: RestrictedTimeOut remains a plain search-boundary
                // filter rather than a hard ceiling on the day's total (see
                // FlexibleShiftCalculationStrategy), so a window narrower than
                // the required hours is still legal -- it just means some of
                // those hours have to come from punches paired outside the
                // window's capping/filtering, same as today.
                if (RestrictedTimeInPicker.SelectedTime is not { } parsedRestrictedTimeIn)
                {
                    Warn("Select a time in for the punching window.");
                    return;
                }

                if (RestrictedTimeOutPicker.SelectedTime is not { } parsedRestrictedTimeOut)
                {
                    Warn("Select a time out for the punching window.");
                    return;
                }

                if (parsedRestrictedTimeIn == parsedRestrictedTimeOut)
                {
                    Warn("Time in and time out can't be the same.");
                    return;
                }

                restrictedTimeIn = parsedRestrictedTimeIn;
                restrictedTimeOut = parsedRestrictedTimeOut;
            }
            else if (scheduleType == ScheduleType.SplitShift)
            {
                foreach (var row in _segmentRows)
                {
                    if (row.TimeInPicker.SelectedTime is not { } segIn)
                    {
                        Warn("Select a start time for each punching window.");
                        return;
                    }

                    if (row.TimeOutPicker.SelectedTime is not { } segOut)
                    {
                        Warn("Select an end time for each punching window.");
                        return;
                    }

                    if (segOut == segIn)
                    {
                        Warn("Each punching window's start and end time can't be the same -- remove the window instead, or use different times.");
                        return;
                    }

                    if (!TryReadBufferOverride(row.ClockInBufferBox, "clock-in buffer", out var clockInBuffer)) return;
                    if (!TryReadBufferOverride(row.ClockOutBufferBox, "clock-out buffer", out var clockOutBuffer)) return;

                    segments.Add((segIn, segOut, clockInBuffer, clockOutBuffer));
                }

                // Sorted so overlap-checking only needs to compare each window against
                // the one right after it, rather than every pair -- valid regardless of
                // whether a window crosses midnight, since TimeIn alone (never itself
                // wrapped) is enough to order windows within the same day; see
                // SplitShiftCalculationStrategy.Calculate, which relies on this same
                // ordering guarantee.
                segments.Sort((a, b) => a.TimeIn.CompareTo(b.TimeIn));
                for (var i = 1; i < segments.Count; i++)
                {
                    // A window that crosses midnight (TimeOut <= TimeIn -- see
                    // FlexibleSegment.CrossesMidnight) really ends 24h later than its
                    // raw TimeOut suggests, so its end has to be pushed a day forward
                    // before comparing against the next window's start -- otherwise an
                    // overnight window's end (e.g. 6:00 AM) would look *earlier* than
                    // its own start (10:00 PM) and this check would miss a real overlap,
                    // or flag a false one, depending on the neighbor's own time.
                    var previousEnd = segments[i - 1].TimeOut.ToTimeSpan();
                    if (segments[i - 1].TimeOut <= segments[i - 1].TimeIn)
                        previousEnd += TimeSpan.FromDays(1);

                    if (segments[i].TimeIn.ToTimeSpan() < previousEnd)
                    {
                        Warn("Punching windows can't overlap -- adjust the times so each window's start is at or after the previous window's end.");
                        return;
                    }
                }

                if (segments.Count > 0)
                {
                    // Same midnight-crossing adjustment as the overlap check above --
                    // a raw TimeOut-minus-TimeIn would come out negative for a window
                    // that wraps, understating (or with enough wrapped windows,
                    // potentially even negating) the real total.
                    var totalWindowHours = segments.Sum(s =>
                    {
                        var hoursSpan = (s.TimeOut.ToTimeSpan() - s.TimeIn.ToTimeSpan()).TotalHours;
                        return hoursSpan > 0 ? hoursSpan : hoursSpan + 24;
                    });
                    if ((double)hours > totalWindowHours)
                    {
                        Warn($"The punching windows add up to only {totalWindowHours:0.##} hour(s), which is shorter than the {hours} required hour(s) -- widen the windows or lower the required hours.");
                        return;
                    }
                }
            }
            else if (isUnscheduledRestDay)
            {
                // Nothing further to read -- timeIn and workTime both stay at
                // their already-initialized null, which is exactly what tells
                // RestDayShiftCalculationStrategy.Resolve to route to
                // CalculateUnscheduledDay instead of CalculateWindowedDay. No
                // buffers either: those only mean anything once punches are
                // matched against a window (see SingleWindowShiftCalculationStrategy),
                // and the unscheduled mode never does that matching.
            }
            else
            {
                // Normal and Official Business both still need a TimeIn (shift
                // start, with TimeOut derived from it plus WorkTimeHours -- see
                // ScheduleEntry.TimeOut); Flexible no longer uses this field at
                // all -- its optional restriction is entirely in
                // RestrictedTimeIn/RestrictedTimeOut above -- and neither does
                // SplitShift, whose windows are entirely in the segments above.
                // A RestDay that reaches this branch is the windowed sub-case
                // (isUnscheduledRestDay is false, i.e. RestDayDutyCheckBox is
                // checked, and UpdateFieldAvailability keeps both fields visible
                // and required whenever that's true) -- same TimeIn requirement
                // as Normal.
                if (TimeInPicker.SelectedTime is not { } parsedTimeIn)
                {
                    Warn("Select a time in.");
                    return;
                }

                timeIn = parsedTimeIn;

                // The clock-in/clock-out buffers only mean anything when punches
                // are actually matched against this window (see
                // SingleWindowShiftCalculationStrategy) -- Official Business skips
                // punch matching entirely, so it has no buffers to read here (the
                // fields are hidden for it too, see UpdateFieldAvailability).
                // Windowed RestDay reuses the exact same SingleWindowShiftCalculationStrategy
                // matching Normal does (see CalculateWindowedDay), so it reads
                // these back too -- the fields were already shown for RestDay
                // (see NormalBufferBorder's visibility in UpdateFieldAvailability,
                // which was never RestDay-exclusive to begin with) but nothing
                // used to read them back for it.
                if (scheduleType == ScheduleType.Normal || scheduleType == ScheduleType.RestDay)
                {
                    if (!TryReadBufferOverride(ClockInBufferBeforeBox, "clock-in buffer, before scheduled time", out clockInBufferBeforeHours)) return;
                    if (!TryReadBufferOverride(ClockInBufferAfterBox, "clock-in buffer, after scheduled time", out clockInBufferAfterHours)) return;
                    if (!TryReadBufferOverride(ClockOutBufferBeforeBox, "clock-out buffer, before scheduled time", out clockOutBufferBeforeHours)) return;
                    if (!TryReadBufferOverride(ClockOutBufferAfterBox, "clock-out buffer, after scheduled time", out clockOutBufferAfterHours)) return;
                }
            }
        }

        ScheduleType = scheduleType;
        WorkTimeHours = workTime;
        TimeIn = timeIn;
        IsPaidLeave = isPaidLeave;
        FlexibleSegments = segments;
        RestrictedTimeIn = restrictedTimeIn;
        RestrictedTimeOut = restrictedTimeOut;
        ClockInBufferBeforeHours = clockInBufferBeforeHours;
        ClockInBufferAfterHours = clockInBufferAfterHours;
        ClockOutBufferBeforeHours = clockOutBufferBeforeHours;
        ClockOutBufferAfterHours = clockOutBufferAfterHours;
        OvertimeEligibleOverride = overtimeEligibleOverride;
        NightDiffEligibleOverride = nightDiffEligibleOverride;
        ApplyOvertimeRatePercentageOverride = applyOvertimeRatePercentageOverride;
        OvertimeRatePercentageOverride = overtimeRatePercentageOverride;
        NightDiffRatePercentageOverride = nightDiffRatePercentageOverride;

        DialogResult = true;
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "Check your entry", MessageBoxButton.OK, MessageBoxImage.Warning);
}