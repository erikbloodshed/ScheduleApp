namespace ScheduleApp.Desktop.Services;

/// <summary>
/// The app's single notification surface is the status bar hosted in MainWindow
/// (see RootStatusBarPresenter, wired up in MainWindow's constructor) -- every
/// ViewModel reports success/info/caution/error through these four methods
/// instead of picking its own title/icon/timeout per call site, so the same
/// kind of message always looks the same no matter where it comes from.
/// Confirmations (Yes/No, "are you sure?") deliberately aren't here -- the status
/// bar can't block for an answer, so those still use MessageBox.
/// </summary>
public static class StatusBarNotificationExtensions
{
    public static void ShowSuccess(this IStatusBarService statusBarService, string message, string title = "Success") =>
        statusBarService.Show(
            title, message, StatusKind.Success, TimeSpan.FromSeconds(5));

    /// <summary>Brief, non-blocking status pulses (e.g. Generate Reports' step-by-step
    /// progress) -- shorter timeout than the others since several of these can fire
    /// in quick succession and each new one replaces whatever's currently shown.</summary>
    public static void ShowInfo(this IStatusBarService statusBarService, string message, string title = "Working…") =>
        statusBarService.Show(
            title, message, StatusKind.Info, TimeSpan.FromSeconds(5));

    /// <summary>Validation problems and partial results -- something needs attention
    /// but nothing failed outright.</summary>
    public static void ShowCaution(this IStatusBarService statusBarService, string message, string title = "Check your entry") =>
        statusBarService.Show(
            title, message, StatusKind.Caution, TimeSpan.FromSeconds(5));

    public static void ShowError(this IStatusBarService statusBarService, string message, string title = "Error") =>
        statusBarService.Show(
            title, message, StatusKind.Error, TimeSpan.FromSeconds(10));
}
