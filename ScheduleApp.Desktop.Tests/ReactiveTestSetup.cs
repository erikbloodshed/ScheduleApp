using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;

namespace ScheduleApp.Desktop.Tests;

/// <summary>
/// ReactiveUI set up the way the app does (App's constructor), except that work it would queue
/// on the WPF dispatcher -- a command's results, a binding's writes -- runs straight away
/// instead: no dispatcher is pumping in a test, so queued work would never run.
/// </summary>
internal static class ReactiveTestSetup
{
    private static readonly Lazy<bool> Initialization = new(() =>
    {
        ReactiveUIBootstrapper.EnsureInitialized();
        RxSchedulers.MainThreadScheduler = ImmediateSequencer.Instance;
        return true;
    });

    public static void EnsureInitialized() => _ = Initialization.Value;
}
