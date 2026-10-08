namespace ScheduleApp.Desktop.Views;

/// <summary>
/// A page MainWindow's navigation drawer tells when it's shown and when it's left (see
/// MainWindow.NavigateTo) -- the same two calls WPF-UI's NavigationView made, so each page's
/// load-on-first-visit logic carried over unchanged when the drawer replaced it.
/// </summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync();

    Task OnNavigatedFromAsync();
}
