namespace ScheduleApp.Core.Enums;

/// <summary>
/// What kind of itemized line a PayrollAdjustment row is -- see
/// ScheduleApp.Core.Payroll.PayrollAdjustment. The first three are Gross Pay,
/// the rest are Deductions (see PayrollAdjustmentTypeLabel.IsDeduction below) --
/// PayrollCalculator sums each type independently into its own line of the
/// Gross Pay/Deductions breakdown, and PayrollSummaryView renders one itemized
/// list per type within whichever of the two card sections it belongs to.
/// </summary>
public enum PayrollAdjustmentType
{
    // Gross Pay
    PremiumHoliday = 0,
    Allowance = 1,
    Incentive = 2,

    // Deductions
    SSS = 3,
    PhilHealth = 4,
    PagIbig = 5,
    CashAdvance = 6,
    Charge = 7,
}

/// <summary>
/// Display text and Gross Pay-vs-Deductions classification for
/// PayrollAdjustmentType, kept separate from ToString() the same way
/// ScheduleTypeLabel/PunchStatusLabel are -- callers that need a stable,
/// code-shaped value (e.g. ScheduleDbContext's string conversion, see
/// HasConversion&lt;string&gt;) keep using ToString(), while anything shown to
/// a person goes through ToText() instead.
/// </summary>
public static class PayrollAdjustmentTypeLabel
{
    public static string ToText(this PayrollAdjustmentType type) => type switch
    {
        PayrollAdjustmentType.PremiumHoliday => "Premium Pay",
        PayrollAdjustmentType.PhilHealth => "PhilHealth",
        PayrollAdjustmentType.PagIbig => "Pag-IBIG",
        PayrollAdjustmentType.CashAdvance => "Cash Advance",
        PayrollAdjustmentType.Charge => "Charges",
        _ => type.ToString(),
    };

    /// <summary>True for SSS/PhilHealth/Pag-IBIG/Cash Advance/Other Charges,
    /// false for Allowance/Incentive/Premium Pay -- the grouping
    /// PayrollSummaryView's two card sections (Gross Pay, Deductions) and
    /// PayrollCalculator's Net Pay total (Gross Pay minus Deductions) are both
    /// built from.</summary>
    public static bool IsDeduction(this PayrollAdjustmentType type) => type switch
    {
        PayrollAdjustmentType.Allowance => false,
        PayrollAdjustmentType.Incentive => false,
        PayrollAdjustmentType.PremiumHoliday => false,
        _ => true,
    };

    /// <summary>True for Allowance/Premium Pay/SSS/PhilHealth/Pag-IBIG/Cash
    /// Advance -- these six are a single settable value per employee/period
    /// rather than a genuinely itemized list: at most one PayrollAdjustment row
    /// per (EmployeeId, PeriodStart, PeriodEnd, Type), edited directly through
    /// an inline amount box on PayrollSummaryView (see
    /// PayrollViewModel.SetSingleValueAsync) -- there's no separate "add" step
    /// or dialog at all, typing an amount into the box either creates that one
    /// row or updates it in place -- an "Edit" button next to it just focuses
    /// the box and selects its existing text for a quick overwrite, typing 0
    /// and committing is how a person explicitly zeroes it out. PremiumHoliday
    /// joined this group once its own itemized dialog was
    /// dropped -- it only ever received a single amount per line the way these
    /// five already did, so it's now edited the same way instead of keeping a
    /// dialog just for itself. PayrollAdjustmentRepository.AddAsync/UpdateAsync
    /// still reject a second row for all six as a server-side backstop to that
    /// rule. False for Incentive/OtherCharge, which stay genuinely itemized --
    /// any number of rows, each with its own Description, summed into the
    /// category's Subtotal the way every type originally worked. Doesn't change
    /// PayrollCalculator.Calculate itself: summing zero-or-one rows already
    /// equals "the single value" (or ₱0.00 if unset), so nothing about the
    /// actual math needed to change for this.</summary>
    public static bool IsSingleValue(this PayrollAdjustmentType type) => type switch
    {
        PayrollAdjustmentType.Allowance => true,
        PayrollAdjustmentType.PremiumHoliday => true,
        PayrollAdjustmentType.SSS => true,
        PayrollAdjustmentType.PhilHealth => true,
        PayrollAdjustmentType.PagIbig => true,
        PayrollAdjustmentType.CashAdvance => true,
        _ => false,
    };

    /// <summary>True for Allowance/Premium Pay/Cash Advance -- the three
    /// IsSingleValue types that carry an Employee-level default (DefaultAllowance/
    /// DefaultPremiumPay/DefaultCashAdvance) the same way SSS/PhilHealth/Pag-IBIG
    /// do, and so -- like those three -- now get seeded and reseeded automatically
    /// by IPayrollComputationService.SeedOrReseedContributionsAsync every time an
    /// employee/period is computed (see ContributionDefaultsFor's own doc comment
    /// for how their TargetAmount is derived: unconditionally EmployeeDefault,
    /// with no withholding-cutoff gating -- PremiumHoliday carries one further
    /// gate on top, Employee.QualifiesForPremiumPay, that ContributionDefaultsFor
    /// checks before this flag ever comes into play; Allowance/CashAdvance have
    /// no such gate). This flag is what lets that seeding
    /// give these three an explicit 0.00 row even when EmployeeDefault itself is
    /// 0 -- see SeedOrReseedContributionsAsync's own skip-rule doc comment --
    /// whereas SSS/PhilHealth/Pag-IBIG (BlankAmountMeansZero false) skip entirely
    /// at EmployeeDefault 0, since 0 there genuinely means "not enrolled" rather
    /// than "this period's amount happens to be zero".
    ///
    /// Also still governs PayrollViewModel.SetSingleValueAsync's own blank-commit
    /// behavior: for these three, clearing the box and committing is treated the
    /// same as typing an explicit "0" -- creating (or updating) that employee/
    /// period's row with Amount = 0 rather than leaving no row at all -- so a
    /// payment nobody hand-edited still reads 0.00 everywhere (the box, 
    /// PayrollExcelExporter's column, and a printed "₱0.00" payslip line instead
    /// of PayslipLineBuilder's AddAdjustmentGroup silently omitting the category).
    /// SSS/PhilHealth/Pag-IBIG don't need this at edit time since seeding already
    /// keeps a row there on their behalf.</summary>
    public static bool BlankAmountMeansZero(this PayrollAdjustmentType type) => type switch
    {
        PayrollAdjustmentType.Allowance => true,
        PayrollAdjustmentType.PremiumHoliday => true,
        PayrollAdjustmentType.CashAdvance => true,
        _ => false,
    };

    /// <summary>True for Incentive/OtherCharge -- genuinely itemized (any number of
    /// rows, unlike the six IsSingleValue types above), but each row's Description
    /// and Amount are typed directly into the row itself on PayrollSummaryView (see
    /// AdjustmentGroupTemplate's inline-itemized body, PayrollViewModel's
    /// AddInlineRowAsync/UpdateInlineDescriptionAsync/UpdateInlineAmountAsync, and
    /// PayrollSummaryView.xaml.cs's InlineDescriptionBox_.../InlineAmountBox_...
    /// handlers) rather than through a dialog -- "+ Add Row" creates a blank row
    /// in place, no popup. These are the only two types left once IsSingleValue
    /// above is ruled out, now that PremiumHoliday has moved into that group --
    /// see PayrollAdjustmentGroup.IsSingleValue/IsInlineItemized, which between
    /// them now cover every PayrollAdjustmentType.</summary>
    public static bool IsInlineItemized(this PayrollAdjustmentType type) => type switch
    {
        PayrollAdjustmentType.Incentive => true,
        PayrollAdjustmentType.Charge => true,
        _ => false,
    };
}