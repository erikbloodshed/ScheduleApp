using System.Windows;
using System.Windows.Threading;
using ReactiveUI.Binding;
using ReactiveUI.Builder;
using Splat;

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

        // WithWpf() registers AutoDataTemplateBindingHook, which gives any list whose ItemsSource
        // is bound without an ItemTemplate or DisplayMemberPath a template that looks up a view
        // for each item. No list here shows items that have one -- they're strings and plain
        // rows -- so every such list drew blank rows instead of WPF's own ToString() text.
        // It's the only hook WithWpf() registers.
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .WithWpf()
            .WithRegistration(static resolver => resolver.UnregisterAll<IPropertyBindingHook>())
            .BuildApp();
        BindingHooks.Refresh();

        return true;
    });

    public static void EnsureInitialized() => _ = Initialization.Value;
}
