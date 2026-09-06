namespace ScheduleApp.Core.Exceptions;

/// <summary>
/// Thrown by IUserAccountRepository.AddAsync when the given account name is already
/// taken by another account (active or not). SetupAdminWindow/ManageUsersDialog
/// already pre-check this before it gets here -- this is a second line of defense for
/// the check-then-act race, e.g. two people setting up the app against the same fresh
/// database at the same time. Same shape as DuplicateEmployeeIdException.
/// </summary>
public class DuplicateUsernameException : Exception
{
    public string Username { get; }

    public DuplicateUsernameException(string username)
        : base($"The account name \"{username}\" is already taken.")
    {
        Username = username;
    }
}
