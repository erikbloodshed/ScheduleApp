using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.ViewModels.Accounts;

/// <summary>
/// The sign-in card over the main window, whenever at least one account exists. Prefilled
/// from a remembered sign-in, which a successful sign-in saves or forgets as "Remember me"
/// says -- a failed one never touches it, so a typo can't wipe out a credential that still
/// works. A wrong name, a wrong password and a deactivated account all get the same answer,
/// so an attempt can't tell them apart.
/// </summary>
public partial class SignInViewModel : ReactiveViewModel
{
    private readonly IUserAccountRepository _repository;
    private readonly IRememberedSignInStore _rememberedSignIn;

    public SignInViewModel(IUserAccountRepository repository, IRememberedSignInStore rememberedSignIn)
    {
        _repository = repository;
        _rememberedSignIn = rememberedSignIn;

        if (rememberedSignIn.TryLoad() is { } remembered)
        {
            Username = remembered.Username;
            Password = remembered.Password;
            RememberMe = true;
        }
    }

    [Reactive]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>Pushed in by the View (a PasswordBox doesn't bind); cleared after a refused
    /// attempt, which the View mirrors.</summary>
    [Reactive]
    public partial string Password { get; set; } = string.Empty;

    [Reactive]
    public partial bool RememberMe { get; set; }

    [Reactive]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Who signed in, or null when refused. Running, the button is disabled, as a
    /// command's is.</summary>
    [ReactiveCommand]
    private async Task<AuthenticatedUser?> SignInAsync()
    {
        var username = Username.Trim();
        if (username.Length == 0 || Password.Length == 0)
        {
            ErrorMessage = "Enter an account name and password.";
            return null;
        }

        try
        {
            var account = await _repository.GetByUsernameAsync(username);
            if (account is null || !account.IsActive || !PasswordHasher.Verify(Password, account.PasswordHash, account.PasswordSalt))
            {
                ErrorMessage = "Invalid account name or password.";
                Password = string.Empty;
                return null;
            }

            if (RememberMe)
                _rememberedSignIn.Save(username, Password);
            else
                _rememberedSignIn.Clear();

            ErrorMessage = null;
            return new AuthenticatedUser(account.Id, account.Username);
        }
        catch (Exception ex)
        {
            // Most likely the database went away since startup; shown like a refusal.
            ErrorMessage = "Could not reach the database to sign in.\n\n" + ex.Message;
            return null;
        }
    }
}

/// <summary>
/// The first run's card, against a database with no accounts yet: creates the first one
/// instead of asking for existing credentials. Later accounts are added from Manage Users.
/// </summary>
public partial class SetupAdminViewModel(IUserAccountRepository repository) : NewPasswordViewModel
{
    [Reactive]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>The account created (and so signed in), or null when turned away.</summary>
    [ReactiveCommand]
    private async Task<AuthenticatedUser?> CreateAsync()
    {
        var username = Username.Trim();
        if (username.Length == 0)
        {
            ErrorMessage = "Enter an account name.";
            return null;
        }

        if (!CheckPassword()) return null;

        try
        {
            if (await repository.GetByUsernameAsync(username) is not null)
            {
                ErrorMessage = $"The account name \"{username}\" is already taken.";
                return null;
            }

            var (hash, salt) = PasswordHasher.Hash(Password);
            var account = await repository.AddAsync(new UserAccount { Username = username, PasswordHash = hash, PasswordSalt = salt });
            ErrorMessage = null;
            return new AuthenticatedUser(account.Id, account.Username);
        }
        catch (DuplicateUsernameException)
        {
            // A second setup screen won the same insert first.
            ErrorMessage = $"The account name \"{username}\" is already taken.";
            return null;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not create the account.\n\n" + ex.Message;
            return null;
        }
    }
}
