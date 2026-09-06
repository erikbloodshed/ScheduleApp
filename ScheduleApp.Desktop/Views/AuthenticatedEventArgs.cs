namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Raised by SignInPanel.SignedIn and SetupAdminPanel.AccountCreated -- MainWindow
/// subscribes both events to the same handler (OnAuthSucceeded) since either one means
/// "an account is now authenticated, hide the overlay and proceed". Carries the same
/// two values CurrentUserContext.Set(...) needs.
/// </summary>
public sealed class AuthenticatedEventArgs(int userId, string username) : EventArgs
{
    public int UserId { get; } = userId;
    public string Username { get; } = username;
}
