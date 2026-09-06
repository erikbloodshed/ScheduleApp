using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleApp.Core.Payroll;
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
/// Description/Amount boxes, all commit on LostFocus/Enter rather than through an ICommand
/// the way every button-driven action on this page does -- TextBox has no Command/
/// CommandParameter of its own to bind that to -- because a single-value category's own
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
    /// commit still happens the normal way once focus leaves the box (LostFocus/Enter --
    /// see SingleValueAmountBox_LostFocus/KeyDown below), so clicking Edit and clicking away
    /// without typing anything is a no-op, same as clicking directly into the box always was.</summary>
    private void EditSingleValueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: Grid grid }) return;
        if (grid.Children.OfType<TextBox>().FirstOrDefault() is not TextBox box) return;

        box.Focus();
        box.SelectAll();
    }

    private void SingleValueAmountBox_LostFocus(object sender, RoutedEventArgs e) => CommitSingleValue(sender);

    /// <summary>Enter commits immediately (same "don't make someone tab away just to save"
    /// expectation as a search box) rather than waiting for LostFocus, then clears focus so
    /// the box doesn't visibly sit there mid-edit -- the LostFocus handler above still fires
    /// after that, but PayrollViewModel.SetSingleValueAsync no-ops on an amount that hasn't
    /// changed since the last commit (see its own doc comment), so the second call is
    /// harmless rather than a duplicate write.</summary>
    private void SingleValueAmountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitSingleValue(sender);
        e.Handled = true;
        Keyboard.ClearFocus();
    }

    private void CommitSingleValue(object sender)
    {
        if (sender is not TextBox { DataContext: PayrollAdjustmentGroupRow group } box) return;
        if (DataContext is not PayrollViewModel viewModel) return;

        _ = viewModel.SetSingleValueAsync(group.Type, box.Text);
    }

    /// <summary>Same LostFocus/KeyDown/commit split as SingleValueAmountBox_.../
    /// CommitSingleValue above, just for an inline-itemized row's own Description box instead
    /// -- see InlineAdjustmentRowTemplate and PayrollViewModel.UpdateInlineDescriptionAsync.
    /// </summary>
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

    /// <summary>Same LostFocus/KeyDown/commit split again, for an inline-itemized row's own
    /// Amount box -- see InlineAdjustmentRowTemplate and
    /// PayrollViewModel.UpdateInlineAmountAsync.</summary>
    private void InlineAmountBox_LostFocus(object sender, RoutedEventArgs e) => CommitInlineAmount(sender);

    private void InlineAmountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitInlineAmount(sender);
        e.Handled = true;
        Keyboard.ClearFocus();
    }

    private void CommitInlineAmount(object sender)
    {
        if (sender is not TextBox { DataContext: PayrollAdjustment adjustment } box) return;
        if (DataContext is not PayrollViewModel viewModel) return;

        _ = viewModel.UpdateInlineAmountAsync(adjustment, box.Text);
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
