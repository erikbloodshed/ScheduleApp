using System.Windows;
using System.Windows.Threading;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Defers work until the UI thread has finished what it's doing -- including the rest of a
/// synchronous burst of changes, such as a department checkbox flipping every one of its
/// employees -- and has repainted, so the work sees the settled state and runs once.
/// </summary>
internal static class UiDispatch
{
    /// <summary>Runs <paramref name="action"/> at Background priority (below Render and Input)
    /// when called on the app's UI thread; off it (a ViewModel test) there's no message loop
    /// to defer to, so it runs straight away.</summary>
    public static void AfterCurrentWork(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
            dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        else
            action();
    }
}
