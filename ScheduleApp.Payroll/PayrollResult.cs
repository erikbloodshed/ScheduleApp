using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Payroll;

/// <summary>
/// One computed, non-editable Gross Pay/Deductions line -- e.g. "Basic Pay" or
/// "Overtime" -- as opposed to a <see cref="PayrollAdjustment"/> row, which the
/// person can add/edit/delete themselves directly on <c>PayrollSummaryView</c>.
/// See <see cref="PayrollCalculator.Calculate"/>.
/// </summary>
public class PayrollLineItem
{
    public required string Label { get; init; }

    /// <summary>Already rounded to the nearest centavo (2 dp) -- see
    /// PayrollCalculator's Round helper. Never negative; which side of the
    /// Gross Pay/Deductions split a line belongs to is which list it's in
    /// (<see cref="PayrollResult.ComputedGrossPay"/> vs.
    /// <see cref="PayrollResult.ComputedDeductions"/>), not the sign of this
    /// value.</summary>
    public required decimal Amount { get; init; }

    /// <summary>True when a person has checked "disregard" for this line --
    /// currently only ever set on the Undertime line (see
    /// PayrollCalculator.Calculate and IPayrollUndertimeWaiverRepository).
    /// Amount above is always the real computed figure regardless -- waiving
    /// never changes what's shown here, only whether <see
    /// cref="PayrollResult.TotalDeductions"/> counts it. Defaults to false so
    /// every other computed line (Basic Pay/Overtime/Night Diff, which have
    /// no waive concept at all) doesn't need to set this explicitly.</summary>
    public bool Waived { get; init; }

    /// <summary>True only for the Undertime line -- what PayrollSummaryView's
    /// single DeductionLineTemplate uses to show/hide the Exclude/Include toggle
    /// per item, instead of routing Undertime through its own ContentControl
    /// outside the ItemsControl. Defaults to false, same as Waived, so Basic
    /// Pay/Overtime/Night Diff (and any future computed line without a waive
    /// concept) don't need to set this explicitly.</summary>
    public bool SupportsWaiver { get; init; }
}

/// <summary>
/// One <see cref="PayrollAdjustmentType"/>'s row(s) for this employee's period --
/// e.g. every Allowance row plus its subtotal -- so <c>PayrollSummaryView</c> can
/// render one card section per type straight off this, without re-grouping a
/// flat adjustment list itself. Two shapes:
///
/// - Single-value (see <see cref="IsSingleValue"/>): Allowance/Premium Pay/SSS/
///   PhilHealth/Pag-IBIG/Cash Advance. <see cref="Adjustments"/> holds at most
///   one row, exposed separately as <see cref="SingleValueAdjustment"/> for an
///   inline, dialog-free amount box -- see that property's own doc comment.
/// - Inline-itemized (see <see cref="IsInlineItemized"/>): Incentive/OtherCharge,
///   genuinely itemized (any number of rows). Description + Amount per row, both
///   typed directly into the row itself, a subtotal, and a "+ Add Row" that
///   creates a blank row in place -- no dialog.
///
/// Present for all eight <see cref="PayrollAdjustmentType"/> values -- see
/// <see cref="PayrollResult.GrossPayAdjustmentGroups"/>/
/// <see cref="PayrollResult.DeductionAdjustmentGroups"/> -- even when
/// <see cref="Adjustments"/> is empty, so a category with nothing entered yet
/// still has a place to enter its first value. One documented exception:
/// PremiumHoliday's group is omitted from GrossPayAdjustmentGroups entirely
/// (not just left empty) for an employee with Employee.QualifiesForPremiumPay
/// == false, the same "omit rather than show at zero" treatment Rest Day Pay
/// gets in ComputedGrossPay -- see PayrollCalculator.Calculate, Pay
/// Eligibility Flags plan Phase 4.
/// </summary>
public class PayrollAdjustmentGroup
{
    public required PayrollAdjustmentType Type { get; init; }

    /// <summary>Every row for this type, this employee, this period -- see
    /// PayrollCalculator.Calculate's filtering. Ordered the same way
    /// IPayrollAdjustmentRepository.GetForEmployeePeriodAsync returns them
    /// (by CreatedAt within the type), not re-sorted here.</summary>
    public required IReadOnlyList<PayrollAdjustment> Adjustments { get; init; }

    /// <summary>Sum of every row's Amount -- deliberately *not* rounded the way
    /// the computed lines in PayrollResult.ComputedGrossPay/ComputedDeductions
    /// are, since each PayrollAdjustment.Amount is already a whole-centavo value
    /// a person typed in (decimal(10,2) in the database) rather than something
    /// PayrollCalculator derived through multiplication -- summing them can't
    /// introduce a fraction of a centavo to round away in the first place.
    /// </summary>
    public decimal Subtotal => Adjustments.Sum(a => a.Amount);

    /// <summary>True for Allowance/SSS/PhilHealth/Pag-IBIG/Cash Advance -- see
    /// PayrollAdjustmentTypeLabel.IsSingleValue. Exposed here (rather than
    /// having PayrollSummaryView call Type.IsSingleValue() itself) for the same
    /// reason Subtotal is: a plain passthrough the view binds straight to
    /// without reaching past this class for it.</summary>
    public bool IsSingleValue => Type.IsSingleValue();

    /// <summary>True for Incentive/OtherCharge -- see
    /// PayrollAdjustmentTypeLabel.IsInlineItemized. Same plain-passthrough reason
    /// as IsSingleValue above; AdjustmentGroupTemplate's inline-itemized body binds
    /// straight to this. Together with IsSingleValue, covers every
    /// PayrollAdjustmentType -- there's no third, dialog-based shape any more.</summary>
    public bool IsInlineItemized => Type.IsInlineItemized();

    /// <summary>The one row for a single-value type, or null if nothing's been
    /// entered yet for this employee/period -- always null for an itemized
    /// type. What PayrollSummaryView's inline amount box binds to (Amount, for
    /// display) and its "Edit" button's Visibility gates on (non-null means
    /// there's an existing figure worth selecting for a quick overwrite) -- see
    /// PayrollAdjustmentTypeLabel.IsSingleValue's own doc comment for why
    /// these six types skip a dialog entirely. The "Edit" button itself is a
    /// plain Click handler (PayrollSummaryView.xaml.cs's
    /// EditSingleValueButton_Click), not a bound ICommand -- it only focuses
    /// and selects the sibling amount box's text; the actual commit still goes
    /// through PayrollViewModel.SetSingleValueAsync the same as typing into the
    /// box directly always has.
    ///
    /// If Adjustments already has more than one row -- data entered before
    /// this distinction existed, since nothing here retroactively merges or
    /// deletes anything -- this still returns just the first one; the rest
    /// stay in Adjustments (invisible to the single-value box, but not lost),
    /// so a person who needs to see them can still do so directly against the
    /// database rather than the app silently discarding history.</summary>
    public PayrollAdjustment? SingleValueAdjustment => IsSingleValue && Adjustments.Count > 0 ? Adjustments[0] : null;

    /// <summary>Same value as SingleValueAdjustment?.Amount, exposed as its own nullable
    /// property so PayrollSummaryView's inline amount box can bind to it in one hop instead
    /// of two -- a Text="{Binding SingleValueAdjustment.Amount}" path breaks awkwardly in WPF
    /// when SingleValueAdjustment is null (Amount is a non-nullable decimal, so there's
    /// nothing valid for the second hop to resolve to), where a null SingleValueAmount is
    /// just an ordinary value NumberConverter already knows how to show as a blank box. A
    /// row that's been explicitly zeroed (typed 0 into the box and committed) still has a
    /// row here -- just with Amount 0 -- so it renders as an ordinary 0.00 in the box rather
    /// than blank; blank means "no row at all", not "row present and zero".
    /// SingleValueAdjustment itself stays around for the "Edit" button's own Visibility
    /// check above.</summary>
    public decimal? SingleValueAmount => SingleValueAdjustment?.Amount;
}

/// <summary>
/// One employee's full Gross Pay/Deductions/Net Pay breakdown for one payroll
/// period -- everything <c>PayrollSummaryView</c> needs to render, produced by
/// <see cref="PayrollCalculator.Calculate"/>. Recomputed live every time the
/// Payroll tab is opened (see the Payroll Feature plan's Assumption 6 -- no
/// "finalize and lock" run record in v1); nothing on this class is itself
/// persisted, only the underlying <see cref="PayrollAdjustment"/> rows are.
/// </summary>
public class PayrollResult
{
    /// <summary>Employee.Pin -- see PayrollCalculator.Calculate for why this is
    /// required rather than nullable.</summary>
    public required int EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }

    /// <summary>Basic Pay, Overtime, Night Diff always present, in that order;
    /// Rest Day Pay is a fourth, trailing line present only when
    /// Employee.QualifiesForRestDayPay is true -- three or four items
    /// depending on eligibility, not a fixed four. (Stale note this replaces,
    /// found while confirming Phase 6 of the Pay Eligibility Flags plan: this
    /// comment previously said Rest Day Pay "always shows, even at ₱0.00,
    /// same 'always present' convention as the other three" -- true before
    /// that plan's Phase 4, no longer true after it. See PayrollCalculator.
    /// Calculate and RestDayPayAmount below for the omit-rather-than-zero
    /// gate and how to read this employee's Rest Day Pay peso figure without
    /// depending on whether the line itself is present.)</summary>
    public required IReadOnlyList<PayrollLineItem> ComputedGrossPay { get; init; }

    /// <summary>Undertime -- the one computed (non-editable) Deductions line. A
    /// list rather than a single property for symmetry with ComputedGrossPay,
    /// and so PayrollSummaryView can render both the same way without a special
    /// case for there being exactly one.</summary>
    public required IReadOnlyList<PayrollLineItem> ComputedDeductions { get; init; }

    /// <summary>Count of days credited as paid this period, matching the day
    /// count shown inline in ComputedGrossPay[0].Label for both employee
    /// types (e.g. "Basic Pay (5D)") -- Basic Pay itself is exactly this
    /// count times the employee's day rate (DailyRate for Daily-rated,
    /// effectiveDailyRate for Monthly-rated). For Daily-rated this is a
    /// literal count of days BasicPayForDay credited (Complete, Official
    /// Business, or paid Leave). For Monthly-rated it's the nominal-15-day
    /// divisor adjusted for uncredited/excess days (see PayrollCalculator.
    /// Calculate's own comments on that adjustment) rather than a literal
    /// count of AttendanceSummary rows -- a period genuinely shorter than 15
    /// real days (e.g. a 13-day February half) is still meant to price as a
    /// fully-paid nominal period, not a prorated-down one. Exposed here as
    /// its own property so a caller such as PayrollExcelExporter can read a
    /// plain int column instead of parsing label text.</summary>
    public required int WorkDays { get; init; }

    /// <summary>Count of calendar days in [PeriodStart, PeriodEnd] with no
    /// AttendanceSummary row at all for this employee -- distinct from an
    /// Absent day (which has a row; a schedule entry exists, no punch was
    /// found) and from a Rest Day (also has a row). Correct for both
    /// EmployeeType values, but only actionable for Monthly-rated: a gap day
    /// for a Daily-rated employee is already harmless (simply not credited,
    /// same as WorkDays already reflects), whereas for Monthly-rated it
    /// silently neither reduces the divisor nor counts toward a deduction --
    /// a real overpayment risk if a genuine day off is left unscheduled
    /// instead of marked Rest Day. A caller such as PayrollViewModel/
    /// PayrollComputationService checks this &gt; 0 for a Monthly-rated
    /// employee to surface a caution.</summary>
    public required int UnscheduledDayCount { get; init; }

    /// <summary>Total Overtime hours for the period, matching the figure already
    /// shown inline in ComputedGrossPay[1].Label (e.g. "Overtime (3.00H)"). Sum of
    /// each day's hours already rounded to 2 dp (see PayrollCalculator.RoundHours)
    /// -- decimal, not double, for that reason: it's the same rounded figure the
    /// Overtime peso amount was itself computed from, not a separate raw total.
    /// Exposed here as its own property for the same reason as WorkDays above.</summary>
    public required decimal OvertimeHours { get; init; }

    /// <summary>Total Night Diff hours for the period, matching the figure already
    /// shown inline in ComputedGrossPay[2].Label. Sum of each day's hours already
    /// rounded to 2 dp, same reasoning as OvertimeHours above. Exposed here as its
    /// own property for the same reason as WorkDays above.</summary>
    public required decimal NightDiffHours { get; init; }

    /// <summary>Total Rest Day Pay hours for the period (Employee Pay Types &amp;
    /// Rest Day plan), matching the figure already shown inline in the "Rest Day
    /// Pay (8.00H)"-style line's Label within ComputedGrossPay -- note that line
    /// (and therefore its index) is only present when
    /// Employee.QualifiesForRestDayPay is true; an ineligible employee still gets
    /// a correct RestDayHours here even though ComputedGrossPay omits the line
    /// entirely (see the Pay Eligibility Flags plan, Phase 4). Only counts
    /// hours from an unambiguous duty on a day scheduled as RestDay (see
    /// RestDayShiftCalculationStrategy) -- zero for a period with no Rest Day
    /// worked, or none scheduled at all. Sum of each day's hours already
    /// rounded to 2 dp, same reasoning as OvertimeHours above. Applies to both
    /// EmployeeType values -- a Daily-rated employee can be called in on a
    /// scheduled Rest Day too. Exposed here as its own property for the same
    /// reason as WorkDays above.</summary>
    public required decimal RestDayHours { get; init; }

    /// <summary>The Rest Day Pay peso figure actually credited this period --
    /// added to Pay Eligibility Flags plan Phase 6, once PayrollExcelExporter
    /// turned out to need this and had been reaching for
    /// ComputedGrossPay[3].Amount instead, which throws for any employee
    /// whose ComputedGrossPay has no fourth line (see that line's own doc
    /// comment: present only when Employee.QualifiesForRestDayPay is true --
    /// the default for every employee since the flags' migration). Unlike
    /// RestDayHours above -- which stays the real worked-hours figure
    /// regardless of eligibility, since hours worked is a fact independent of
    /// whether they're paid for it -- this is 0.00 for an ineligible
    /// employee, matching what ComputedGrossPay/TotalGrossPay actually show
    /// for them: nothing, not the hypothetical amount they'd have earned if
    /// eligible. Equivalent to (and always kept equal to) the fourth
    /// ComputedGrossPay line's Amount when that line is present, so a reader
    /// like PayrollExcelExporter's Rest Day Pay column can use this directly
    /// without checking ComputedGrossPay.Count first.</summary>
    public required decimal RestDayPayAmount { get; init; }

    /// <summary>The Holiday Pay peso figure earned this period (Holiday Pay plan,
    /// Phase 2) -- day component plus matching Overtime/Night Diff "copies" for
    /// every date in the period that both falls on a listed Holiday (see
    /// IHolidayRepository) and was actually worked (PunchStatus.Complete, or an
    /// actually-worked RestDay -- this now stacks with Rest Day Pay for a Holiday
    /// landing on a scheduled Rest Day, company policy update). Also includes, for
    /// a Monthly-rated employee only, the flat day component (no OT/ND copies) for
    /// a listed Holiday not worked at all, as long as the day isn't one Basic Pay
    /// already pays in full (OfficialBusiness/paid Leave) -- see
    /// PayrollCalculator.CalculateHolidayPay's own doc comment for the full rule.
    /// Unlike RestDayPayAmount above, this carries no employee-level
    /// eligibility gate of its own here -- Employee.QualifiesForPremiumPay governs
    /// whether the figure is ever shown to the person at all, but that gate lives
    /// on the PremiumHoliday adjustment group it feeds (Phase 3's
    /// PayrollComputationService.ContributionDefaultsFor), not on this raw
    /// computed value, the same "hours/pesos worked is a fact independent of
    /// whether they're paid for it" reasoning RestDayHours' own doc comment
    /// describes for Rest Day Pay. Not itself a ComputedGrossPay line -- unlike
    /// Rest Day Pay (a wholly new pay type with nowhere else to go), Holiday Pay
    /// is surfaced to the person entirely through the pre-existing, editable
    /// PremiumHoliday adjustment group instead, so adding a second, parallel
    /// display of the same money here would double it up rather than merely
    /// explain it -- this property exists so a caller (that seeding logic) has
    /// something to seed the adjustment's TargetAmount from, not so
    /// PayrollSummaryView renders it directly.</summary>
    public required decimal HolidayPayAmount { get; init; }

    /// <summary>Count of calendar days this period that earned the day component
    /// of HolidayPayAmount above -- i.e. how many of the period's listed Holiday
    /// dates were actually worked (PunchStatus.Complete or a worked RestDay), plus,
    /// for a Monthly-rated employee, how many more were paid anyway despite not
    /// being worked (see HolidayPayAmount's own doc comment) -- not merely listed.
    /// Exposed here as its own property for the same reason WorkDays is:
    /// a caller such as PayrollExcelExporter can read a plain int column instead
    /// of re-deriving it from HolidayPayAmount, which also folds in OT/ND copies
    /// and so can't be divided back out into a day count on its own.</summary>
    public required int HolidayWorkedDays { get; init; }

    /// <summary>Total Undertime hours for the period, matching the figure already
    /// shown inline in ComputedDeductions[0].Label. Sum of each day's hours already
    /// rounded to 2 dp, same reasoning as OvertimeHours above. Unaffected by whether
    /// Undertime is waived, same as ComputedDeductions[0].Amount itself, which stays
    /// the real computed figure regardless of Waived (see PayrollLineItem.Waived's
    /// own doc comment). Exposed here as its own property for the same reason as
    /// WorkDays above.</summary>
    public required decimal UndertimeHours { get; init; }

    /// <summary>One group per PayrollAdjustmentType where IsDeduction() is false
    /// -- Allowance, Incentive, Premium Pay, in that enum-declaration
    /// order -- each rendered as its own itemized card section under
    /// Gross Pay.</summary>
    public required IReadOnlyList<PayrollAdjustmentGroup> GrossPayAdjustmentGroups { get; init; }

    /// <summary>One group per PayrollAdjustmentType where IsDeduction() is true
    /// -- SSS, PhilHealth, Pag-IBIG, Cash Advance, Other Charges, in that
    /// enum-declaration order -- each rendered as its own itemized card section
    /// under Deductions.</summary>
    public required IReadOnlyList<PayrollAdjustmentGroup> DeductionAdjustmentGroups { get; init; }

    /// <summary>Basic Pay + Overtime + Night Diff + every GrossPayAdjustmentGroup
    /// subtotal.</summary>
    public decimal TotalGrossPay =>
        ComputedGrossPay.Sum(line => line.Amount) + GrossPayAdjustmentGroups.Sum(g => g.Subtotal);

    /// <summary>Undertime (unless waived -- see PayrollLineItem.Waived) + every
    /// DeductionAdjustmentGroup subtotal. A waived line's Amount is deliberately
    /// excluded here even though it still appears in ComputedDeductions for
    /// display -- "should still be calculated [and shown], just disregarded from
    /// the total" is exactly the distinction Waived exists to carry.</summary>
    public decimal TotalDeductions =>
        ComputedDeductions.Where(line => !line.Waived).Sum(line => line.Amount) +
        DeductionAdjustmentGroups.Sum(g => g.Subtotal);

    /// <summary>Echo of PayrollPolicy.NetPayRoundingMultiple, the multiple NetPay
    /// below rounds to -- stored here (the same way OvertimeHours/NightDiffHours
    /// echo PayrollCalculator's own computation) so NetPay can stay a pure
    /// computed property off TotalGrossPay/TotalDeductions without PayrollResult
    /// needing to hold a whole PayrollPolicy reference just for this one value.
    /// See NetPayRoundingMultiple's own doc comment on PayrollPolicy for what it
    /// means and its default.</summary>
    public required decimal NetPayRoundingMultiple { get; init; }

    /// <summary>TotalGrossPay − TotalDeductions, rounded to the nearest multiple of
    /// NetPayRoundingMultiple (see MRound below) -- Excel's own MROUND(number,
    /// multiple). Can still go negative after rounding (e.g. a Cash Advance bigger
    /// than what was earned that period) -- PayrollSummaryView's concern how to
    /// display that, not this class's.</summary>
    public decimal NetPay => MRound(TotalGrossPay - TotalDeductions, NetPayRoundingMultiple);

    /// <summary>Excel's MROUND(value, multiple): the nearest multiple of `multiple`
    /// to `value`, ties rounding away from zero (MROUND(2.5, 1) = 3, same as
    /// MROUND(-2.5, 1) = -3) -- matching PayrollCalculator.Round's own
    /// MidpointRounding.AwayFromZero convention rather than banker's rounding, for
    /// the same "matches what a person doing this by hand would expect" reason.
    /// Unlike Excel's own MROUND -- which errors (#NUM!) whenever `value` and
    /// `multiple` have different signs -- this only ever receives a non-negative
    /// `multiple` (see NetPayRoundingMultiple's own doc comment) but `value` here
    /// is NetPay, which is allowed to be negative (a Cash Advance bigger than what
    /// was earned); dividing/multiplying through still lands on the mathematically
    /// nearest multiple in that case (e.g. MRound(-2.30m, 5m) = 0m, the nearest
    /// multiple of 5 to -2.30) rather than surfacing a spreadsheet error a Payroll
    /// Summary card has no way to show. `multiple` <= 0 -- an unconfigured or
    /// misconfigured PayrollPolicy -- returns `value` unrounded rather than
    /// dividing by zero.</summary>
    private static decimal MRound(decimal value, decimal multiple) =>
        multiple > 0
            ? Math.Round(value / multiple, 0, MidpointRounding.AwayFromZero) * multiple
            : value;
}