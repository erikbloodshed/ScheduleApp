namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Holds who's logged in for the lifetime of the app session -- set once by
/// MainWindow.OnAuthSucceeded right after SignInPanel or SetupAdminPanel succeeds (see
/// MainWindow's own doc comment), read by MainWindow (title bar) and ManageUsersDialog
/// (to stop someone deactivating/deleting the very account they're currently signed in
/// as). Singleton rather than Scoped since there's exactly one signed-in account for the
/// whole process -- same reasoning as AppShutdownSignal.
/// </summary>
public class CurrentUserContext
{
    public int UserId { get; private set; }
    public string Username { get; private set; } = string.Empty;

    /// <summary>Called exactly once, from MainWindow.OnAuthSucceeded, right after a
    /// successful sign-in or first-run account creation.</summary>
    public void Set(int userId, string username)
    {
        UserId = userId;
        Username = username;
    }
}
