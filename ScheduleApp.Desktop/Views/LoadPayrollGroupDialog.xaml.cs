using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Build-order step 10's "Load Payroll Group…" dialog: a plain list of every saved
/// PayrollRun (IPayrollRunRepository.ListAsync, newest first -- see
/// LoadPayrollGroupViewModel's own doc comment), pick one and click Load (or double-click
/// the row) to reopen it. Unlike PayrollWizardDialog/PayslipScopeDialog, there's no
/// department/employee tree here and nothing to resolve Pins back to actual Employee
/// objects with -- that round trip needs a live query against IScheduleRepository (an
/// employee may have been added, renamed, or deleted since this run was saved), which
/// only PayrollViewModel has a reason to hold a reference to, so it's done by
/// PayrollRunViewModel.LoadPayrollGroupAsync after this dialog returns, not here (see that
/// method's own doc comment). This dialog's only job is picking *which* saved run.
///
/// Constructed directly (`new LoadPayrollGroupDialog(...)`), not through DI -- same
/// convention every other dialog on this tab follows (see PayslipScopeDialog's own doc
/// comment for why).
/// </summary>
public partial class LoadPayrollGroupDialog
{
    /// <summary>Shown for a LoadPayrollGroupViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public LoadPayrollGroupDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.IsLoading, v => v.LoadingIndicator.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HasNoRuns, v => v.NoRunsText.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Runs, v => v.RunsListBox.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.SelectedRun, v => v.RunsListBox.SelectedItem).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.DeletePayrollRunCommand, v => v.DeleteButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ChooseCommand, v => v.LoadButton).DisposeWith(d);

            // Double-clicking a row is the same as picking it and clicking Load.
            Observable.FromEventPattern<MouseButtonEventHandler, MouseButtonEventArgs>(
                    handler => RunsListBox.MouseDoubleClick += handler,
                    handler => RunsListBox.MouseDoubleClick -= handler)
                .Select(_ => RxVoid.Default)
                .InvokeCommand(ViewModel, vm => vm.ChooseCommand)
                .DisposeWith(d);

            viewModel.ChooseCommand
                .Where(chosen => chosen)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);

            viewModel.LoadRunsCommand.Execute().Subscribe().DisposeWith(d);
        });
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
