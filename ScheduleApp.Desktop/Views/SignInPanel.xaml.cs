using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Hosted inside MainWindow's AuthOverlay (see MainWindow.xaml) and shown by
/// MainWindow.ShowSignInOverlay whenever at least one UserAccount already exists (see
/// IUserAccountRepository.AnyAsync) -- a fresh database with no accounts yet gets
/// SetupAdminPanel instead. This used to be its own modal LoginWindow shown before
/// MainWindow ever existed; it's a UserControl now so the sign-in card can sit directly
/// on top of MainWindow's own content instead of a separate window on top of it. Raises
/// SignedIn on success (MainWindow.OnAuthSucceeded hides the overlay and proceeds) and
/// ExitRequested if the person chooses not to sign in (MainWindow closes itself, which
/// exits the app the same way Shutdown() did for the old LoginWindow -- see
/// MainWindow.xaml.cs).
/// </summary>
public partial class SignInPanel : UserControl
{
    private IUserAccountRepository? _userAccountRepository;
    private RememberedSignInStore? _rememberedSignInStore;

    public event EventHandler<AuthenticatedEventArgs>? SignedIn;
    public event EventHandler? ExitRequested;

    public SignInPanel()
    {
        InitializeComponent();
    }

    /// <summary>Called once by MainWindow right after construction -- can't go through
    /// the constructor since this control is instantiated by MainWindow.xaml's parser,
    /// not resolved through DI the way the old LoginWindow(IUserAccountRepository) was.
    /// Also pre-fills the form (and checks RememberMeCheckBox) from whatever
    /// rememberedSignInStore.TryLoad() returns -- see that store's own doc comment for
    /// how the password survives on disk between launches.</summary>
    public void Initialize(IUserAccountRepository userAccountRepository, RememberedSignInStore rememberedSignInStore)
    {
        _userAccountRepository = userAccountRepository;
        _rememberedSignInStore = rememberedSignInStore;

        if (_rememberedSignInStore.TryLoad() is { } remembered)
        {
            UsernameBox.Text = remembered.Username;
            PasswordBoxControl.Password = remembered.Password;
            RememberMeCheckBox.IsChecked = true;
        }
    }

    public void FocusUsername() => UsernameBox.Focus();

    /// <summary>Called once by MainWindow right after Initialize, with whatever
    /// SignInSettings.LogoPath currently resolves to -- swaps the built-in
    /// /Assets/Tinapayan_Logo.png for a custom image if one's configured (see
    /// AuthLogoLoader.TryLoad for the null/missing/unloadable fallback behavior).</summary>
    public void ApplyLogo(string? logoPath)
    {
        if (AuthLogoLoader.TryLoad(logoPath) is { } bitmap)
            LogoImage.Source = bitmap;
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBoxControl.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            ShowError("Enter an account name and password.");
            return;
        }

        SignInButton.IsEnabled = false;
        try
        {
            var account = await _userAccountRepository!.GetByUsernameAsync(username);

            // Same generic message whether the account doesn't exist, the password is
            // wrong, or the account was deactivated (see UserAccount.IsActive) -- a
            // login attempt shouldn't be able to distinguish "wrong password" from
            // "that account doesn't exist" from "that account was disabled".
            if (account is null || !account.IsActive ||
                !PasswordHasher.Verify(password, account.PasswordHash, account.PasswordSalt))
            {
                ShowError("Invalid account name or password.");
                PasswordBoxControl.Clear();
                PasswordBoxControl.Focus();
                return;
            }

            // Only touched on a *successful* sign-in -- see RememberedSignInStore.Clear's
            // own doc comment for why a failed attempt (e.g. a typo) must never wipe out
            // a credential that was still good.
            if (RememberMeCheckBox.IsChecked == true)
                _rememberedSignInStore!.Save(username, password);
            else
                _rememberedSignInStore!.Clear();

            SignedIn?.Invoke(this, new AuthenticatedEventArgs(account.Id, account.Username));
        }
        catch (Exception ex)
        {
            // Most likely cause here is the database connection dropping between
            // App.OnStartup's Migrate() and this click -- SQL Server Express stopping,
            // a network blip, etc. Shown inline rather than as a MessageBox so it reads
            // consistently with a wrong-password rejection above.
            ShowError("Could not reach the database to sign in.\n\n" + ex.Message);
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
