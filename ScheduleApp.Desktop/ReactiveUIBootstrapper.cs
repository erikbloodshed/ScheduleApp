using System.Windows;
using System.Windows.Threading;
using ReactiveUI.Builder;

namespace ScheduleApp.Desktop;

/// <summary>
/// ReactiveUI 24+ requires explicit builder-pattern initialization before any ReactiveObject is
/// touched, exactly once per process. App's constructor calls EnsureInitialized() first thing.
/// </summary>
public static class ReactiveUIBootstrapper
{
    private static readonly Lazy<bool> Initialization = new(() =>
    {
        // WithWpf() binds the main-thread scheduler to the Application's dispatcher, or, when
        // there is no Application, to the calling thread's existing dispatcher, and throws if
        // that thread has none. Since ReactiveUI 24.3 it no longer creates one, so create it here.
        if (Application.Current is null)
            _ = Dispatcher.CurrentDispatcher;

        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .WithWpf()
            .BuildApp();

        return true;
    });

    public static void EnsureInitialized() => _ = Initialization.Value;
}
