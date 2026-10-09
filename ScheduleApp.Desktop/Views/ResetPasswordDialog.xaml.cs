using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>Manage Users' Reset Password -- see <see cref="ViewModels.Accounts.ResetPasswordViewModel"/>.</summary>
public partial class ResetPasswordDialog
{
    public ResetPasswordDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            TargetUsernameText.Text = viewModel.Prompt;
            PasswordBoxBinding.Bind(PasswordBoxControl, viewModel.WhenAnyValue(vm => vm.Password), value => viewModel.Password = value)
                .DisposeWith(d);
            PasswordBoxBinding.Bind(ConfirmPasswordBoxControl, viewModel.WhenAnyValue(vm => vm.ConfirmPassword),
                value => viewModel.ConfirmPassword = value).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Visibility, AccountViews.VisibleWhenSet).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.OkButton).DisposeWith(d);
            viewModel.AcceptCommand.Where(accepted => accepted).Subscribe(_ => DialogResult = true).DisposeWith(d);
        });

        Loaded += (_, _) => PasswordBoxControl.Focus();
    }
}
