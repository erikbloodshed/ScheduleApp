using System.Windows;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Collects a new account name + password for ManageUsersDialog's Add User button.
/// Same shape as EmployeeDialog: this dialog touches no repository itself -- it's
/// handed the existing account names up front (existingUsernames) and validates
/// against that in-memory set, the same way EmployeeDialog validates Employee ID
/// against a passed-in takenEmployeeIds set rather than querying the database itself.
/// ManageUsersDialog does the actual hashing (see PasswordHasher) and
/// IUserAccountRepository.AddAsync call after this returns true -- including the
/// unlikely check-then-act race DuplicateUsernameException exists for, which this
/// dialog's local pre-check can't catch on its own.
/// </summary>
public partial class AddUserDialog : Wpf.Ui.Controls.FluentWindow
{
    private const int MinimumPasswordLength = 8;

    private readonly IReadOnlyCollection<string> _existingUsernames;

    public string Username { get; private set; } = string.Empty;
    public string Password { get; private set; } = string.Empty;

    public AddUserDialog(IReadOnlyCollection<string> existingUsernames)
    {
        InitializeComponent();
        _existingUsernames = existingUsernames;

        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBoxControl.Password;
        var confirmPassword = ConfirmPasswordBoxControl.Password;

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowError("Enter an account name.");
            return;
        }

        if (_existingUsernames.Contains(username, StringComparer.OrdinalIgnoreCase))
        {
            ShowError($"The account name \"{username}\" is already taken.");
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

        Username = username;
        Password = password;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
