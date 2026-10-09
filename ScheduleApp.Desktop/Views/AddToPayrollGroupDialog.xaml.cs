using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Picker dialog for adding employees to an already-saved payroll group -- see
/// <see cref="AddToPayrollGroupViewModel"/>.
/// </summary>
public partial class AddToPayrollGroupDialog
{
    /// <summary>Shown for an AddToPayrollGroupViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public AddToPayrollGroupDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.Bind(ViewModel, vm => vm.SearchText, v => v.SearchBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.VisibleDepartments, v => v.EmployeeTree.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ScopeText, v => v.ScopeText.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.AddButton).DisposeWith(d);

            viewModel.AcceptCommand.Subscribe(_ => DialogResult = true).DisposeWith(d);
        });
    }
}
