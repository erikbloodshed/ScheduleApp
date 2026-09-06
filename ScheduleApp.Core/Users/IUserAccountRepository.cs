namespace ScheduleApp.Core.Users;

/// <summary>
/// Abstraction over ScheduleApp's own UserAccounts table (see SqlUserAccountRepository
/// in ScheduleApp.Data) -- login accounts for this app, not to be confused with
/// Employee (see UserAccount's own doc comment for why they're separate).
/// </summary>
public interface IUserAccountRepository
{
    /// <summary>True if at least one account exists. App.xaml.cs uses this at startup
    /// to decide between showing LoginWindow (accounts already exist) and
    /// SetupAdminWindow (first run -- nothing to log into yet, so the first account is
    /// created instead of entered).</summary>
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive lookup by Username (see UserAccount.Username's doc
    /// comment), or null if no account -- active or not -- has that name. Includes
    /// inactive accounts on purpose: callers (LoginWindow, ManageUsersDialog's
    /// duplicate-name check when adding) decide what to do about IsActive == false
    /// themselves rather than this silently hiding the row from them.</summary>
    Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Every account, most recently created first -- backs
    /// ManageUsersDialog's list.</summary>
    Task<List<UserAccount>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a new account (Id/CreatedAtUtc/IsActive are assigned here) and
    /// returns it back with those set. Throws DuplicateUsernameException if Username is
    /// already taken -- callers should still pre-check with GetByUsernameAsync for a
    /// faster, friendlier rejection; this is the second line of defense for the
    /// check-then-act race, same pattern ScheduleRepository.AddEmployeeAsync already
    /// uses for Employee.Pin.</summary>
    Task<UserAccount> AddAsync(UserAccount account, CancellationToken cancellationToken = default);

    /// <summary>Overwrites PasswordHash/PasswordSalt for an existing account -- e.g. a
    /// forgotten-password reset from ManageUsersDialog. No-op if the id doesn't exist.</summary>
    Task UpdatePasswordAsync(int id, string passwordHash, string passwordSalt, CancellationToken cancellationToken = default);

    /// <summary>Flips IsActive -- see UserAccount.IsActive's doc comment for why this
    /// exists alongside DeleteAsync. No-op if the id doesn't exist.</summary>
    Task SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Removes an account outright. No-op if the id doesn't exist.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
