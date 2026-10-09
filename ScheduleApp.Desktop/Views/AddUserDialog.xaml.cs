using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>Manage Users' Add User -- see <see cref="ViewModels.Accounts.AddUserViewModel"/>.</summary>
public partial class AddUserDialog
{
    public AddUserDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            this.Bind(ViewModel, vm => vm.Username, v => v.UsernameBox.Text).DisposeWith(d);
            PasswordBoxBinding.Bind(PasswordBoxControl, viewModel.WhenAnyValue(vm => vm.Password), value => viewModel.Password = value)
                .DisposeWith(d);
            PasswordBoxBinding.Bind(ConfirmPasswordBoxControl, viewModel.WhenAnyValue(vm => vm.ConfirmPassword),
                value => viewModel.ConfirmPassword = value).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Visibility, AccountViews.VisibleWhenSet).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.OkButton).DisposeWith(d);
            viewModel.AcceptCommand.Where(accepted => accepted).Subscribe(_ => DialogResult = true).DisposeWith(d);
        });

        Loaded += (_, _) => UsernameBox.Focus();
    }
}
