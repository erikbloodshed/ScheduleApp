using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// The shared department/employee checkbox-tree + period-picker scope dialog behind "Print
/// Payslips…", "Export Payroll Report…" and "Export Schedule…" -- see
/// <see cref="PayslipScopeViewModel"/> for the tree/period/search machinery and its
/// validation. Only the window title, description, and confirm button's caption/tooltip differ
/// between callers; what was chosen is read off the ViewModel's AcceptedScope, which is only
/// set once its checks pass, so no caller ever sees a half-valid result.
/// </summary>
public partial class PayslipScopeDialog
{
    /// <summary>Shown for a PayslipScopeViewModel its opener builds -- with the captions,
    /// period and preset it should start with -- the view locator creating it through this
    /// constructor.</summary>
    public PayslipScopeDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            Title = viewModel.Title;
            DescriptionText.Text = viewModel.Description;
            ConfirmButton.Label = viewModel.ConfirmText;
            ConfirmButton.ToolTip = viewModel.ConfirmToolTip;

            this.Bind(ViewModel, vm => vm.PeriodStart, v => v.PeriodStartPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PeriodEnd, v => v.PeriodEndPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.SearchText, v => v.SearchBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.VisibleDepartments, v => v.EmployeeTree.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SelectionScopeText, v => v.ScopeText.Text).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.SelectAllTreeCommand, v => v.SelectAllButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ClearTreeSelectionCommand, v => v.ClearButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.ConfirmButton).DisposeWith(d);

            viewModel.AcceptCommand
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);

            // PresetSelection is null -- the "check everyone" tree -- unless the opener has an
            // active payroll group to hand in; see PayslipScopeViewModel.LoadEmployeeTreeAsync's
            // own doc comment.
            viewModel.LoadEmployeeTreeCommand.Execute(viewModel.PresetSelection).Subscribe().DisposeWith(d);
        });
    }
}
