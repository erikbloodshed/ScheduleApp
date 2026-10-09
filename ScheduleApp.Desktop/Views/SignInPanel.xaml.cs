using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>The sign-in card over the main window -- see
/// <see cref="ViewModels.Accounts.SignInViewModel"/>. Exit closes the window (and with it
/// the app).</summary>
public partial class SignInPanel
{
    public SignInPanel()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            this.Bind(ViewModel, vm => vm.Username, v => v.UsernameBox.Text).DisposeWith(d);
            PasswordBoxBinding.Bind(PasswordBoxControl, viewModel.WhenAnyValue(vm => vm.Password), value => viewModel.Password = value)
                .DisposeWith(d);
            this.Bind(ViewModel, vm => vm.RememberMe, v => v.RememberMeCheckBox.IsChecked, on => on, check => check == true)
                .DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Visibility, AccountViews.VisibleWhenSet).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.SignInCommand, v => v.SignInButton).DisposeWith(d);
        });
    }

    /// <summary>Each click of Exit.</summary>
    public IObservable<Unit> ExitRequested => Observable.FromEventPattern<RoutedEventHandler, RoutedEventArgs>(
            handler => ExitButton.Click += handler,
            handler => ExitButton.Click -= handler)
        .Select(_ => Unit.Default);

    public void FocusUsername() => UsernameBox.Focus();

    /// <summary>Swaps the built-in logo for SignInSettings.LogoPath's image, if it
    /// loads.</summary>
    public void ApplyLogo(string? logoPath)
    {
        if (AuthLogoLoader.TryLoad(logoPath) is { } bitmap)
            LogoImage.Source = bitmap;
    }
}
