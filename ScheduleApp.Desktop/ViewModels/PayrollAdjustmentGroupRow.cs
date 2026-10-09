using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Payroll;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One card section in PayrollSummaryView's Gross Pay/Deductions columns -- Allowance,
/// Incentive, SSS, etc. -- a bindable wrapper around the plain <see
/// cref="PayrollAdjustmentGroup"/> PayrollCalculator produces fresh on every load. Exists so
/// PayrollViewModel.GrossPayAdjustmentGroupRows/DeductionAdjustmentGroupRows can stay the same
/// eight ObservableCollection&lt;PayrollAdjustmentGroupRow&gt; instances across every
/// LoadCoreAsync call, patched in place field-by-field by PayrollViewModel's own
/// SyncAdjustmentGroupRows/SyncAdjustmentRows helpers instead of rebuilding
/// PayrollSummaryView's ItemsControl from scratch each time Result is reassigned -- the same
/// "add the row once, then patch its fields in place" story PayrollGroupRow already tells for
/// the Payroll Group table, just triggered by LoadCoreAsync's own reload instead of
/// RefreshPayrollGroupRowsAsync's.
///
/// Type is this row's identity -- it never changes once created. Stale note this replaces,
/// found while confirming Pay Eligibility Flags plan Phase 6: this used to also claim
/// PayrollResult.GrossPayAdjustmentGroups/DeductionAdjustmentGroups "always produce one group
/// per PayrollAdjustmentType, every load, in the same fixed enum-declaration order," and that
/// SyncAdjustmentGroupRows therefore only ever needed to create a row the first time a given
/// index was reached, never to reorder or remove one afterward. True for
/// DeductionAdjustmentGroups (SSS/PhilHealth/Pag-IBIG/Cash Advance/Charges, never gated by
/// eligibility), but no longer true for GrossPayAdjustmentGroups specifically: PremiumHoliday's
/// group is entirely absent, not just present-with-zero-Subtotal, for an employee with
/// Employee.QualifiesForPremiumPay == false (that plan's Phase 4) -- so the set and count of
/// groups can differ from one LoadCoreAsync call to the next, e.g. switching from an eligible
/// employee to an ineligible one. SyncAdjustmentGroupRows now matches rows to groups by this
/// Type (inserting/removing/reordering as needed, the same identity-matching approach
/// SyncAdjustmentRows already used for itemized rows) rather than assuming index i always means
/// the same PayrollAdjustmentType across calls.
/// </summary>
public sealed partial class PayrollAdjustmentGroupRow : ReactiveObject
{
    public PayrollAdjustmentGroupRow() =>
        _singleValueAmountHelper = this.WhenAnyValue(x => x.SingleValueAdjustment)
            .Select(adjustment => adjustment?.Amount)
            .ToProperty(this, x => x.SingleValueAmount);

    public required PayrollAdjustmentType Type { get; init; }

    /// <summary>Same plain passthrough as PayrollAdjustmentGroup.IsSingleValue -- what
    /// AdjustmentGroupTemplate's single-value body Visibility binds to.</summary>
    public bool IsSingleValue => Type.IsSingleValue();

    /// <summary>Same plain passthrough as PayrollAdjustmentGroup.IsInlineItemized -- what
    /// AdjustmentGroupTemplate's inline-itemized body Visibility binds to.</summary>
    public bool IsInlineItemized => Type.IsInlineItemized();

    /// <summary>Patched in place by PayrollViewModel.SyncAdjustmentGroupRows on every
    /// LoadCoreAsync -- see PayrollAdjustmentGroup.SingleValueAdjustment's own doc comment for
    /// what this holds and why it's a whole PayrollAdjustment rather than just a decimal.
    /// Observable (not init) precisely so re-assigning it on an existing row notifies instead of
    /// requiring a whole new PayrollAdjustmentGroupRow instance -- that's the entire point of
    /// this class.</summary>
    [Reactive]
    public partial PayrollAdjustment? SingleValueAdjustment { get; set; }

    /// <summary>SingleValueAdjustment?.Amount, for the same "a binding breaks on a null
    /// intermediate hop" reason as PayrollAdjustmentGroup.SingleValueAmount -- derived from
    /// SingleValueAdjustment, so SyncAdjustmentGroupRows is still the one place that sets this
    /// row's single-value state.</summary>
    [ObservableAsProperty]
    public partial decimal? SingleValueAmount { get; }

    /// <summary>Patched in place by PayrollViewModel.SyncAdjustmentGroupRows on every
    /// LoadCoreAsync, same as SingleValueAdjustment above -- what AdjustmentGroupTemplate's
    /// inline-itemized subtotal box binds to.</summary>
    [Reactive]
    public partial decimal Subtotal { get; set; }

    /// <summary>What InlineAdjustmentRowTemplate's own ItemsControl binds to for
    /// Incentive/OtherCharge's itemized rows. A stable
    /// ObservableCollection, not replaced wholesale on each load -- PayrollViewModel.
    /// SyncAdjustmentRows patches it in place (add/remove/reorder/replace only the row(s) that
    /// actually changed, matched by PayrollAdjustment.Id) instead of Clear()-then-repopulate,
    /// so editing one row's Amount doesn't tear down and rebuild every sibling row's own
    /// Description/Amount TextBoxes in the same category.</summary>
    public ObservableCollection<PayrollAdjustment> Adjustments { get; } = [];
}
