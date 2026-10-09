using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Accounts;

/// <summary>Who just signed in, or whose account was just created.</summary>
public sealed record AuthenticatedUser(int UserId, string Username);

/// <summary>The rule every new password follows: long enough, and typed the same twice.</summary>
public static class PasswordRules
{
    public const int MinimumLength = 8;

    /// <summary>What's wrong with <paramref name="password"/>, or null if nothing is.</summary>
    public static string? Problem(string password, string confirmation) =>
        password.Length < MinimumLength ? $"Password must be at least {MinimumLength} characters."
        : password != confirmation ? "Password and confirmation don't match."
        : null;
}

/// <summary>
/// A password typed twice, with what's wrong with it shown inline. A mismatched confirmation
/// is cleared, so the person retypes just that. The View pushes what's typed into
/// <see cref="Password"/>/<see cref="ConfirmPassword"/> (a PasswordBox doesn't bind) and
/// empties its box when one is cleared here.
/// </summary>
public abstract partial class NewPasswordViewModel : ReactiveViewModel
{
    [Reactive]
    public partial string Password { get; set; } = string.Empty;

    [Reactive]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Why the last attempt was turned away; null while there's nothing to say.</summary>
    [Reactive]
    public partial string? ErrorMessage { get; protected set; }

    /// <summary>Whether the password checks out; says why not if it doesn't.</summary>
    protected bool CheckPassword()
    {
        if (PasswordRules.Problem(Password, ConfirmPassword) is not { } problem)
            return true;

        ErrorMessage = problem;
        if (Password.Length >= PasswordRules.MinimumLength)
            ConfirmPassword = string.Empty;
        return false;
    }
}

/// <summary>Manage Users' Add User: a new account name and password. The name is checked
/// against the accounts already listed; Manage Users does the hashing and the insert (and
/// catches the rare race this local check can't).</summary>
public partial class AddUserViewModel(IReadOnlyCollection<string> existingUsernames) : NewPasswordViewModel
{
    [Reactive]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>The account name settled on, trimmed -- meaningful once OK returned true.</summary>
    public string AcceptedUsername => Username.Trim();

    [ReactiveCommand]
    private bool Accept()
    {
        var username = Username.Trim();
        if (username.Length == 0)
        {
            ErrorMessage = "Enter an account name.";
            return false;
        }

        if (existingUsernames.Contains(username, StringComparer.OrdinalIgnoreCase))
        {
            ErrorMessage = $"The account name \"{username}\" is already taken.";
            return false;
        }

        return CheckPassword();
    }
}

/// <summary>Manage Users' Reset Password: a new password for one account. Manage Users does
/// the hashing and the update.</summary>
public partial class ResetPasswordViewModel(string targetUsername) : NewPasswordViewModel
{
    public string Prompt { get; } = $"New password for \"{targetUsername}\"";

    [ReactiveCommand]
    private bool Accept() => CheckPassword();
}
