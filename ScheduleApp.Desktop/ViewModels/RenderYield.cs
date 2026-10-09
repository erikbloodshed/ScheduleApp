using System.Windows;
using System.Windows.Threading;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Lets the UI repaint partway through a long loop on the UI thread. WPF's async continuations
/// post at DispatcherPriority.Normal, which outranks the Render work that would actually repaint
/// a progress indicator just updated -- a loop whose awaits resolve quickly never lets a Render
/// pass run until it's done, so the person sees nothing move until it's over (see
/// https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.yield). Yielding at
/// Background -- below Render -- lets the pending repaint happen first.
/// </summary>
internal static class RenderYield
{
    /// <summary>Steps aside for a repaint when running on the app's UI thread; off it (a
    /// ViewModel test) there's nothing to repaint, and nothing would run the continuation.</summary>
    public static async Task ForRenderAsync()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
            await Dispatcher.Yield(DispatcherPriority.Background);
    }
}
