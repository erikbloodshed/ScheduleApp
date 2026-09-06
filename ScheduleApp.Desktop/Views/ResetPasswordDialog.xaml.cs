using System.Windows;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Collects a new password for ManageUsersDialog's Reset Password button. Same
/// no-repository-access shape as AddUserDialog -- ManageUsersDialog does the actual
/// hashing (see PasswordHasher) and IUserAccountRepository.UpdatePasswordAsync call
/// after this returns true.
/// </summary>
public partial class ResetPasswordDialog : Window
{
    private const int MinimumPasswordLength = 8;

    public string Password { get; private set; } = string.Empty;

    public ResetPasswordDialog(string targetUsername)
    {
        InitializeComponent();
        TargetUsernameText.Text = $"New password for \"{targetUsername}\"";

        Loaded += (_, _) => PasswordBoxControl.Focus();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBoxControl.Password;
        var confirmPassword = ConfirmPasswordBoxControl.Password;

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

        Password = password;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
