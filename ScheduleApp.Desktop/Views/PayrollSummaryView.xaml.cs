using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.Views;

/// <summary>Period pickers + the itemized Gross Pay/Deductions/Net Pay breakdown
/// (PayrollViewModel.Result) for whichever employee is selected on the Payroll tab's tree.
/// Almost all layout/behavior lives in the .xaml as plain Command bindings straight to
/// PayrollViewModel (see that file's InlineAdjustmentRowTemplate/AdjustmentGroupTemplate) --
/// the handlers below are the exceptions, needed only because a
/// single-value category's (Allowance/Premium Pay/SSS/PhilHealth/Pag-IBIG/Cash Advance)
/// inline amount box, and an inline-itemized category's (Incentive/OtherCharge) own row
/// Description/Amount boxes, all commit as they're typed in rather than through an ICommand
/// the way every button-driven action on this page does -- TextBox has no Command/
/// CommandParameter of its own to bind that to. The two amount boxes are NumericTextBoxes
/// and commit through its single ValueCommitted event (see that control's doc comment for
/// what else it buys: digits-only input, 0.00 formatting, no negatives); the Description
/// box is a plain TextBox and still needs the older LostFocus + KeyDown pair. Also because
/// a single-value category's own
/// "Edit" button doesn't touch the view model at all, just the sibling amount box's own
/// focus/selection (see EditSingleValueButton_Click) -- and because DeductionLineTemplate's
/// own Exclude/Include toggle button reaches past its own DataContext (the PayrollLineItem
/// it's rendering, not the view model) the same way InlineAdjustmentRowTemplate's Delete
/// button does, rather than a bound
/// ICommand on PayrollLineItem itself. DataContext is set explicitly to a PayrollViewModel by
/// PayrollPage's code-behind, not inherited or set here -- see PayrollPage's own doc comment
/// for why.</summary>
public partial class PayrollSummaryView : UserControl
{
    public PayrollSummaryView()
    {
        InitializeComponent();
    }

    /// <summary>Bound to a single-value category's own "Edit" button (AdjustmentGroupTemplate
    /// and its Deductions-column copy), which replaced the old "Clear" button -- see
    /// PayrollAdjustmentGroup.SingleValueAdjustment's own doc comment for why this button
    /// went from an ICommand to a plain Click handler. No view-model call at all: the button
    /// sits in the same three-column Grid as the amount box it's editing (Label, Edit,
    /// TextBox), so the sibling TextBox is found by walking the Grid's own Children rather
    /// than needing a name or a binding -- Focus() then SelectAll() puts the caret in the box
    /// with its existing figure highlighted, ready to be typed straight over. The actual
    /// commit still happens the normal way once focus leaves the box, or on Enter (see
    /// SingleValueAmountBox_ValueCommitted below), so clicking Edit and clicking away without
    /// typing anything is a no-op, same as clicking directly into the box always was. The box
    /// is a NumericTextBox now rather than a plain TextBox -- still a TextBox as far as the
    /// OfType walk below is concerned, and one that selects its own contents on focus anyway,
    /// which makes the SelectAll here belt-and-braces rather than load-bearing.</summary>
    private void EditSingleValueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: Grid grid }) return;
        if (grid.Children.OfType<TextBox>().FirstOrDefault() is not TextBox box) return;

        box.Focus();
        box.SelectAll();
    }

    /// <summary>A single-value category's amount box, committed. NumericTextBox raises this
    /// on Enter and on lost focus alike, once per actual change (see its own doc comment), so
    /// unlike the LostFocus + KeyDown pair this replaced there's no second, duplicate call
    /// after Enter for PayrollViewModel.SetSingleValueAsync's unchanged-amount check to
    /// absorb -- that check still stands, it just isn't what's keeping Enter from writing
    /// twice any more. InvariantText rather than Text: SetSingleValueAsync parses with
    /// CultureInfo.InvariantCulture while the box formats itself for the current culture.</summary>
    private void SingleValueAmountBox_ValueCommitted(object? sender, EventArgs e)
    {
        if (sender is not NumericTextBox { DataContext: PayrollAdjustmentGroupRow group } box) return;
        if (DataContext is not PayrollViewModel viewModel) return;

        _ = viewModel.SetSingleValueAsync(group.Type, box.InvariantText);
    }

    /// <summary>The older LostFocus/KeyDown/commit split, kept for an inline-itemized row's
    /// own Description box -- free text, so there's no NumericTextBox.ValueCommitted to hang
    /// it on the way the two amount boxes now do -- see InlineAdjustmentRowTemplate and
    /// PayrollViewModel.UpdateInlineDescriptionAsync.</summary>
    private void InlineDescriptionBox_LostFocus(object sender, RoutedEventArgs e) => CommitInlineDescription(sender);

    private void InlineDescriptionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitInlineDescription(sender);
        e.Handled = true;
        Keyboard.ClearFocus();
    }

    private void CommitInlineDescription(object sender)
    {
        if (sender is not TextBox { DataContext: PayrollAdjustment adjustment } box) return;
        if (DataContext is not PayrollViewModel viewModel) return;

        _ = viewModel.UpdateInlineDescriptionAsync(adjustment, box.Text);
    }

    /// <summary>Same single NumericTextBox.ValueCommitted handler as
    /// SingleValueAmountBox_ValueCommitted above, for an inline-itemized row's own Amount box
    /// instead -- see InlineAdjustmentRowTemplate and
    /// PayrollViewModel.UpdateInlineAmountAsync.</summary>
    private void InlineAmountBox_ValueCommitted(object? sender, EventArgs e)
    {
        if (sender is not NumericTextBox { DataContext: PayrollAdjustment adjustment } box) return;
        if (DataContext is not PayrollViewModel viewModel) return;

        _ = viewModel.UpdateInlineAmountAsync(adjustment, box.InvariantText);
    }

    /// <summary>DeductionLineTemplate's own Exclude/Include toggle button -- unlike every
    /// other handler in this file, the sender's DataContext (per DeductionLineTemplate) is
    /// a PayrollLineItem, not what's being changed. The button only ever renders when
    /// SupportsWaiver is true (see that property's own doc comment), which today means this
    /// is always the Undertime line, so reading line.Waived here just tells us which way to
    /// flip it -- the actual commit is still EmployeeId/period-scoped (see
    /// IPayrollUndertimeWaiverRepository), not line-scoped, the same as it was for the
    /// checkbox this replaced.</summary>
    private void UndertimeWaivedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PayrollLineItem line }) return;
        if (DataContext is not PayrollViewModel viewModel) return;

        _ = viewModel.SetUndertimeWaivedAsync(!line.Waived);
    }
}
