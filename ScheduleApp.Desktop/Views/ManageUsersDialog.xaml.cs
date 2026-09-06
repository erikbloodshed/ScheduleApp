using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Opened from MainWindow's toolbar (see MainWindow.ManageUsersButton_Click) -- the
/// only place accounts are added after SetupAdminPanel creates the first one. Lists
/// every UserAccount and lets the signed-in person add another, reset a password, or
/// deactivate/reactivate/delete an existing one.
///
/// Two safety rules enforced here (see UpdateButtonStates), on top of what the
/// repository itself guarantees: you can't deactivate or delete the account you're
/// currently signed in as (CurrentUserContext), and you can't deactivate or delete the
/// last remaining *active* account -- either would either lock the current session out
/// mid-use or leave the app with no way for anyone to sign in the next time it starts,
/// since there's no "forgot password" recovery path (see SignInPanel/SetupAdminPanel).
/// An inactive account can still be deleted freely, since it already can't sign in.
/// </summary>
public partial class ManageUsersDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IUserAccountRepository _userAccountRepository;
    private readonly CurrentUserContext _currentUser;

    private List<UserAccount> _accounts = [];

    public ManageUsersDialog(IUserAccountRepository userAccountRepository, CurrentUserContext currentUser)
    {
        InitializeComponent();
        _userAccountRepository = userAccountRepository;
        _currentUser = currentUser;

        Loaded += async (_, _) => await ReloadAsync();
    }

    private UserAccount? SelectedAccount => (UsersGrid.SelectedItem as UserRow)?.Account;

    private async Task ReloadAsync()
    {
        try
        {
            _accounts = await _userAccountRepository.GetAllAsync();
        }
        catch (Exception ex)
        {
            ShowError("Could not load accounts.\n\n" + ex.Message);
            return;
        }

        HideError();
        UsersGrid.ItemsSource = _accounts.Select(a => new UserRow(a)).ToList();
        UpdateButtonStates();
    }

    private void UsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtonStates();

    private void UpdateButtonStates()
    {
        var selected = SelectedAccount;

        if (selected is null)
        {
            ResetPasswordButton.IsEnabled = false;
            ToggleActiveButton.IsEnabled = false;
            ToggleActiveButton.Content = "Deactivate";
            DeleteButton.IsEnabled = false;
            return;
        }

        var isSelf = selected.Id == _currentUser.UserId;
        var activeCount = _accounts.Count(a => a.IsActive);

        // Only meaningful when the selected account is itself active -- deactivating
        // (or deleting) an already-inactive account never changes the active count.
        var isLastActive = selected.IsActive && activeCount <= 1;

        ResetPasswordButton.IsEnabled = true;

        ToggleActiveButton.Content = selected.IsActive ? "Deactivate" : "Activate";
        ToggleActiveButton.IsEnabled = selected.IsActive ? !isSelf && !isLastActive : true;

        DeleteButton.IsEnabled = !isSelf && !isLastActive;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var existingUsernames = _accounts.Select(a => a.Username).ToList();
        var dialog = new AddUserDialog(existingUsernames) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var (hash, salt) = PasswordHasher.Hash(dialog.Password);
            await _userAccountRepository.AddAsync(new UserAccount
            {
                Username = dialog.Username,
                PasswordHash = hash,
                PasswordSalt = salt
            });
        }
        catch (DuplicateUsernameException ex)
        {
            // Only reachable via the check-then-act race AddUserDialog's own local
            // pre-check can't catch (see its doc comment) -- e.g. Manage Users open
            // twice at once, unlikely for a single-operator desktop app but cheap to
            // handle correctly anyway.
            ShowError(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ShowError("Could not add the account.\n\n" + ex.Message);
            return;
        }

        await ReloadAsync();
    }

    private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected is null)
            return;

        var dialog = new ResetPasswordDialog(selected.Username) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var (hash, salt) = PasswordHasher.Hash(dialog.Password);
            await _userAccountRepository.UpdatePasswordAsync(selected.Id, hash, salt);
        }
        catch (Exception ex)
        {
            ShowError("Could not reset the password.\n\n" + ex.Message);
            return;
        }

        HideError();
        MessageBox.Show(
            $"Password reset for \"{selected.Username}\".",
            "Done", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ToggleActiveButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected is null)
            return;

        var makeActive = !selected.IsActive;

        if (!makeActive)
        {
            var confirm = MessageBox.Show(
                $"Deactivate \"{selected.Username}\"? That account won't be able to sign in until it's " +
                "reactivated here.",
                "Confirm deactivate", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        try
        {
            await _userAccountRepository.SetActiveAsync(selected.Id, makeActive);
        }
        catch (Exception ex)
        {
            ShowError("Could not update the account.\n\n" + ex.Message);
            return;
        }

        await ReloadAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected is null)
            return;

        var confirm = MessageBox.Show(
            $"Delete \"{selected.Username}\"? This can't be undone.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _userAccountRepository.DeleteAsync(selected.Id);
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the account.\n\n" + ex.Message);
            return;
        }

        await ReloadAsync();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    /// <summary>Read-only display row for UsersGrid -- wraps a UserAccount with
    /// formatted Created/Status text rather than a value converter, since only this
    /// one dialog needs it.</summary>
    private class UserRow(UserAccount account)
    {
        public UserAccount Account { get; } = account;
        public string Username => Account.Username;
        public string CreatedDisplay => Account.CreatedAtUtc.ToLocalTime().ToString("MM/dd/yyyy");
        public string StatusDisplay => Account.IsActive ? "Active" : "Inactive";
    }
}
