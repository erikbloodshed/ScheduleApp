using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>Database Setup -- see <see cref="ViewModels.DatabaseSetupViewModel"/>. Opened
/// view-model-first from Settings, and directly (it runs before the app's windows exist) from
/// startup.</summary>
public partial class DatabaseSetupDialog
{
    public DatabaseSetupDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            Title = viewModel.Title;
            IntroText.Text = viewModel.IntroText;
            CancelButton.Label = viewModel.CancelLabel;
            ServerBox.ItemsSource = viewModel.KnownServers;
            AdminConnectionPanel.Visibility = VisibleWhen(viewModel.CreatesDedicatedLogin);
            LoginProvisioningPanel.Visibility = VisibleWhen(viewModel.CreatesDedicatedLogin);

            this.Bind(ViewModel, vm => vm.Server, v => v.ServerBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DatabaseName, v => v.DatabaseNameBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.UseWindowsAuthForAdmin, v => v.AdminWindowsAuthRadio.IsChecked,
                on => on, check => check == true).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.UseWindowsAuthForAdmin, v => v.AdminSqlAuthRadio.IsChecked,
                windows => (bool?)!windows).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.UseWindowsAuthForAdmin, v => v.AdminCredentialsPanel.Visibility,
                windows => VisibleWhen(!windows)).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.AdminUsername, v => v.AdminUsernameBox.Text).DisposeWith(d);
            PasswordBoxBinding.Bind(AdminPasswordBox, viewModel.WhenAnyValue(vm => vm.AdminPassword),
                value => viewModel.AdminPassword = value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NewLoginUsername, v => v.NewLoginUsernameBox.Text).DisposeWith(d);
            PasswordBoxBinding.Bind(NewLoginPasswordBox, viewModel.WhenAnyValue(vm => vm.NewLoginPassword),
                value => viewModel.NewLoginPassword = value).DisposeWith(d);
            PasswordBoxBinding.Bind(ConfirmPasswordBox, viewModel.WhenAnyValue(vm => vm.ConfirmPassword),
                value => viewModel.ConfirmPassword = value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.GrantsFullAccess, v => v.FullAccessRadio.IsChecked, on => on, check => check == true)
                .DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.GrantsFullAccess, v => v.ReadWriteRadio.IsChecked, full => (bool?)!full)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Visibility, AccountViews.VisibleWhenSet).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsBusy, v => v.ProgressPanel.Visibility, VisibleWhen).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.CreateCommand, v => v.CreateButton).DisposeWith(d);
            viewModel.CreateCommand.Where(created => created).Subscribe(_ => DialogResult = true).DisposeWith(d);
        });

        Loaded += (_, _) => ServerBox.Focus();
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
