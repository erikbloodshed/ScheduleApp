namespace ScheduleApp.Core.Users;

/// <summary>
/// A login for Schedule Manager itself -- deliberately separate from Employee, which
/// represents staff being scheduled/tracked for attendance, not people who use this
/// app. The two are unrelated: an Employee has no UserAccount, and a UserAccount isn't
/// tied to any particular Employee. See README's "Known gaps" section, which
/// previously noted there was no user/auth system in ScheduleApp at all -- this is
/// that system, scoped to "can this person open the app," not to per-feature
/// permissions (there's only one kind of account; anyone who can sign in can do
/// anything the app already lets anyone do).
/// </summary>
public class UserAccount
{
    public int Id { get; set; }

    /// <summary>Account name entered on the login screen. Uniqueness is enforced by
    /// ScheduleDbContext's unique index -- SQL Server's default collation is
    /// case-insensitive, so "Admin" and "admin" collide there the same way LoginWindow
    /// and ManageUsersDialog already treat them.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Base64-encoded PBKDF2 hash -- see PasswordHasher. Never the plaintext
    /// password; nothing in this app ever stores that.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Base64-encoded random salt used to produce PasswordHash -- see
    /// PasswordHasher.</summary>
    public string PasswordSalt { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>False for an account that's been disabled rather than deleted (see
    /// ManageUsersDialog's Deactivate button). LoginWindow refuses a sign-in for an
    /// inactive account even with the correct password -- same generic "invalid
    /// account name or password" message as a wrong password gets, so a login attempt
    /// can't be used to discover that an account exists but was disabled.</summary>
    public bool IsActive { get; set; } = true;
}
