using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>The first run's account-creation card -- see
/// <see cref="ViewModels.Accounts.SetupAdminViewModel"/>.</summary>
public partial class SetupAdminPanel
{
    public SetupAdminPanel()
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
            this.BindCommand(ViewModel, vm => vm.CreateCommand, v => v.CreateButton).DisposeWith(d);
        });
    }

    public IObservable<Unit> ExitRequested => Observable.FromEventPattern<RoutedEventHandler, RoutedEventArgs>(
            handler => ExitButton.Click += handler,
            handler => ExitButton.Click -= handler)
        .Select(_ => Unit.Default);

    public void FocusUsername() => UsernameBox.Focus();

    public void ApplyLogo(string? logoPath)
    {
        if (AuthLogoLoader.TryLoad(logoPath) is { } bitmap)
            LogoImage.Source = bitmap;
    }
}
