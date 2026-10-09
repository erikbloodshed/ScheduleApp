using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.Converters;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.Views;

/// <summary>The itemized Gross Pay/Deductions/Net Pay breakdown (PayrollSummaryViewModel.Result)
/// for whichever employee is selected, with its header and empty state. PayrollPage hands it the
/// ViewModel.
///
/// The per-category and per-row parts are DataTemplates bound to their own items, reaching the
/// ViewModel's CanEditAdjustmentsNow and Add/Delete commands through this control's ViewModel
/// property. The handlers below are the exceptions: an amount or description box commits as
/// it's typed in rather than through an ICommand (a TextBox has none) -- the amount boxes are
/// NumericTextBoxes committing through ValueCommitted (digits-only, 0.00, once per actual
/// change), the description box a plain TextBox on LostFocus/Enter. A single-value category's
/// "Edit" button only focuses its sibling amount box, and the Undertime line's Exclude/Include
/// toggle reaches past its own PayrollLineItem to the ViewModel.</summary>
public partial class PayrollSummaryView
{
    public PayrollSummaryView()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            this.OneWayBind(ViewModel, vm => vm.HeaderText, v => v.HeaderText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SelectedEmployee, v => v.PayTypeBadge.Visibility, VisibleWhenNotNull).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SelectedEmployee, v => v.PayTypeText.Text,
                employee => employee?.EmployeeType.ToString() ?? string.Empty).DisposeWith(d);

            // Refresh, or Cancel while anything's running -- see RefreshOrCancelPayslipCommand.
            this.BindCommand(ViewModel, vm => vm.RefreshOrCancelPayslipCommand, v => v.RefreshOrCancelButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RefreshOrCancelGlyph, v => v.RefreshOrCancelButton.Tag).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RefreshOrCancelToolTip, v => v.RefreshOrCancelButton.ToolTip).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SelectedEmployee, v => v.RefreshOrCancelButton.Visibility, VisibleWhenNotNull).DisposeWith(d);

            // The placeholder and the breakdown share one cell; exactly one shows.
            this.OneWayBind(ViewModel, vm => vm.EmptyStateMessage, v => v.EmptyStateText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.EmptyStateMessage, v => v.EmptyStatePanel.Visibility, VisibleWhenNotNull).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Result, v => v.BreakdownPanel.Visibility, VisibleWhenNotNull).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Result, v => v.TotalGrossPayText.Text, result => Amount(result?.TotalGrossPay)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Result, v => v.ComputedGrossPayList.ItemsSource, result => result?.ComputedGrossPay).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.GrossPayAdjustmentGroupRows, v => v.GrossPayGroupList.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Result, v => v.TotalDeductionsText.Text, result => Amount(result?.TotalDeductions)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Result, v => v.ComputedDeductionsList.ItemsSource, result => result?.ComputedDeductions).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.DeductionAdjustmentGroupRows, v => v.DeductionGroupList.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Result, v => v.NetPayText.Text, result => Amount(result?.NetPay)).DisposeWith(d);
        });
    }

    private static Visibility VisibleWhenNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    private static string Amount(decimal? amount) => amount is { } value ? NumberConverter.Format(value) : string.Empty;

    /// <summary>A single-value category's "Edit" button: puts the caret in the sibling amount box
    /// with its figure selected, ready to type over. It sits in the same Grid as the box (Label,
    /// Edit, box), so the box is found there rather than by name. The commit still happens the
    /// normal way, so Edit then clicking away without typing changes nothing.</summary>
    private void EditSingleValueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: Grid grid }) return;
        if (grid.Children.OfType<TextBox>().FirstOrDefault() is not { } box) return;

        box.Focus();
        box.SelectAll();
    }

    /// <summary>A single-value category's amount box, committed (Enter or lost focus, once per
    /// actual change). InvariantText, since SetSingleValueAsync parses invariant while the box
    /// formats for the current culture.</summary>
    private void SingleValueAmountBox_ValueCommitted(object? sender, EventArgs e)
    {
        if (sender is not NumericTextBox { DataContext: PayrollAdjustmentGroupRow group } box) return;

        _ = ViewModel?.SetSingleValueAsync(group.Type, box.InvariantText);
    }

    /// <summary>An itemized row's Description box -- free text, so it commits on LostFocus and
    /// Enter rather than a NumericTextBox's ValueCommitted.</summary>
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

        _ = ViewModel?.UpdateInlineDescriptionAsync(adjustment, box.Text);
    }

    /// <summary>An itemized row's Amount box, committed.</summary>
    private void InlineAmountBox_ValueCommitted(object? sender, EventArgs e)
    {
        if (sender is not NumericTextBox { DataContext: PayrollAdjustment adjustment } box) return;

        _ = ViewModel?.UpdateInlineAmountAsync(adjustment, box.InvariantText);
    }

    /// <summary>The Undertime line's Exclude/Include toggle. Its DataContext is the
    /// PayrollLineItem, which only says which way to flip; the waiver itself is per
    /// employee/period (see IPayrollUndertimeWaiverRepository).</summary>
    private void UndertimeWaivedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PayrollLineItem line }) return;

        _ = ViewModel?.SetUndertimeWaivedAsync(!line.Waived);
    }
}
