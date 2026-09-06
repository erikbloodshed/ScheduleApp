using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Hosted inside MainWindow's AuthOverlay (see MainWindow.xaml) and shown by
/// MainWindow.ShowSignInOverlay instead of SignInPanel the very first time the app runs
/// against a database with no UserAccounts yet (see IUserAccountRepository.AnyAsync) --
/// there's nothing to sign in to, so this creates the first account instead of asking
/// for existing credentials. Every later run finds at least this one account and gets
/// SignInPanel instead; more accounts after this one are added via ManageUsersDialog
/// from inside the app, not through this panel again. Same SignedIn/ExitRequested
/// event contract as SignInPanel -- see its own doc comment for why events rather than
/// the old ShowDialog()/DialogResult approach.
/// </summary>
public partial class SetupAdminPanel : UserControl
{
    private const int MinimumPasswordLength = 8;

    private IUserAccountRepository? _userAccountRepository;

    public event EventHandler<AuthenticatedEventArgs>? AccountCreated;
    public event EventHandler? ExitRequested;

    public SetupAdminPanel()
    {
        InitializeComponent();
    }

    /// <summary>Called once by MainWindow right after construction -- see
    /// SignInPanel.Initialize's own doc comment for why this isn't a constructor
    /// parameter.</summary>
    public void Initialize(IUserAccountRepository userAccountRepository)
    {
        _userAccountRepository = userAccountRepository;
    }

    public void FocusUsername() => UsernameBox.Focus();

    /// <summary>Same custom-logo swap as SignInPanel.ApplyLogo -- this first-run panel
    /// shows the same logo, so it should reflect the same setting.</summary>
    public void ApplyLogo(string? logoPath)
    {
        if (AuthLogoLoader.TryLoad(logoPath) is { } bitmap)
            LogoImage.Source = bitmap;
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBoxControl.Password;
        var confirmPassword = ConfirmPasswordBoxControl.Password;

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowError("Enter an account name.");
            return;
        }

        if (password.Length < MinimumPasswordLength)
        {
            ShowError($"Password must be at least {MinimumPasswordLength} characters.");
            return;
        }

        if (password != confirmPassword)
        {
            ShowError("Password and confirmation don't match.");
            ConfirmPasswordBoxControl.Clear();
            ConfirmPasswordBoxControl.Focus();
            return;
        }

        CreateButton.IsEnabled = false;
        try
        {
            var existing = await _userAccountRepository!.GetByUsernameAsync(username);
            if (existing is not null)
            {
                ShowError($"The account name \"{username}\" is already taken.");
                return;
            }

            var (hash, salt) = PasswordHasher.Hash(password);
            var account = await _userAccountRepository.AddAsync(new UserAccount
            {
                Username = username,
                PasswordHash = hash,
                PasswordSalt = salt
            });

            AccountCreated?.Invoke(this, new AuthenticatedEventArgs(account.Id, account.Username));
        }
        catch (DuplicateUsernameException)
        {
            // The pre-check above already covers the common case -- this only fires on
            // the (very unlikely, for a first-run setup screen) race where a second
            // instance of this panel won the same insert first.
            ShowError($"The account name \"{username}\" is already taken.");
        }
        catch (Exception ex)
        {
            ShowError("Could not create the account.\n\n" + ex.Message);
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
