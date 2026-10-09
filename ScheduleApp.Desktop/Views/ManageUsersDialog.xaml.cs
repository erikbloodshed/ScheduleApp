using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels.Accounts;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>Manage Users -- see <see cref="ManageUsersViewModel"/>.</summary>
public partial class ManageUsersDialog
{
    public ManageUsersDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            UsersGrid.ItemsSource = viewModel.Rows;
            this.Bind(ViewModel, vm => vm.SelectedRow, v => v.UsersGrid.SelectedItem,
                row => row!, item => item as UserAccountRow).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Visibility, AccountViews.VisibleWhenSet).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ToggleActiveLabel, v => v.ToggleActiveButton.Label).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AddUserCommand, v => v.AddButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ResetPasswordCommand, v => v.ResetPasswordButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ToggleActiveCommand, v => v.ToggleActiveButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.DeleteCommand, v => v.DeleteButton).DisposeWith(d);

            Observable.Return(RxVoid.Default).InvokeCommand(viewModel.LoadCommand).DisposeWith(d);
        });
    }
}
