using System.Windows;
using System.Windows.Threading;
using Syncfusion.Licensing;

namespace ScheduleApp.Desktop.Tests;

/// <summary>
/// One WPF UI thread for the whole test run, with an Application carrying the app's own
/// resources (App.xaml's dictionaries), so a test can build a real window or control, show it
/// off-screen so its WhenActivated runs, and check its bindings. Every view test runs its body
/// on this thread through <see cref="RunAsync"/>.
/// </summary>
internal static class UiThread
{
    /// <summary>App.xaml's merged dictionaries, in its order.</summary>
    private static readonly string[] AppDictionaries = ["ControlDefaults", "Tokens", "AppStyles"];

    private static readonly Lazy<Dispatcher> Dispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One view test at a time: they share this one UI thread, so two running at once
    /// would interleave at every await -- one's modal dialog and queued interactions reaching
    /// the other's windows. xUnit runs test classes in parallel; this serializes only the view
    /// tests, leaving ViewModel tests parallel.</summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>How long a view test may run before <see cref="RunAsync"/> gives up on it.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Interactions waiting for a window of their type to load (see
    /// <see cref="WhenLoaded{T}"/>), oldest first.</summary>
    private static readonly List<(Type WindowType, Action<Window> Interact)> PendingInteractions = [];

    /// <summary>What a queued interaction threw, if anything, during the current test -- it
    /// runs from the dispatcher, where nothing else would see it.</summary>
    private static Exception? _interactionFailure;

    /// <summary>
    /// Runs <paramref name="test"/> on the UI thread. One still running after
    /// <see cref="TestTimeout"/> -- typically stuck behind a modal dialog nothing closed --
    /// has every open window closed, which also ends any modal loop, and fails with a
    /// TimeoutException rather than hanging the run.
    /// </summary>
    public static async Task RunAsync(Func<Task> test)
    {
        await OneAtATime.WaitAsync();
        try
        {
            await RunAloneAsync(test);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static async Task RunAloneAsync(Func<Task> test)
    {
        var dispatcher = Dispatcher.Value;
        var run = dispatcher.InvokeAsync(async () =>
        {
            _interactionFailure = null;
            try
            {
                await test();
            }
            finally
            {
                // An interaction a test queued but never used mustn't reach the next test's window.
                PendingInteractions.Clear();
            }

            if (_interactionFailure is { } failure)
                throw new InvalidOperationException("A window interaction failed.", failure);
        }).Task.Unwrap();
        if (await Task.WhenAny(run, Task.Delay(TestTimeout)) == run)
        {
            await run;
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            PendingInteractions.Clear();
            foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
                window.Close();
        });

        // Let the stuck test unwind (closing its windows ends any modal loop) before the next
        // one starts on this thread.
        await Task.WhenAny(run, Task.Delay(TestTimeout));
        throw new TimeoutException($"The view test didn't finish within {TestTimeout.TotalSeconds:0} seconds.");
    }

    /// <summary>
    /// Runs <paramref name="interact"/> on the next window of type <typeparamref name="T"/> to
    /// load, once its bindings have settled -- queued before the code that opens it, so it
    /// also reaches a modal dialog, from inside that dialog's own message loop. Call from the
    /// UI thread (inside <see cref="RunAsync"/>).
    /// </summary>
    public static void WhenLoaded<T>(Action<T> interact) where T : Window =>
        PendingInteractions.Add((typeof(T), window => interact((T)window)));

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        var index = PendingInteractions.FindIndex(p => p.WindowType.IsInstanceOfType(window));
        if (index < 0)
            return;

        var interact = PendingInteractions[index].Interact;
        PendingInteractions.RemoveAt(index);
        window.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                interact(window);
            }
            catch (Exception ex)
            {
                _interactionFailure = ex;
                window.Close();
            }
        }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Shows <paramref name="window"/> off-screen and waits until it has loaded, so its
    /// WhenActivated block has run.</summary>
    public static async Task<T> ShowAsync<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        await IdleAsync();
        return window;
    }

    /// <summary>
    /// Opens <paramref name="window"/> modally, off-screen, runs <paramref name="interact"/>
    /// inside the dialog's own message loop once it has loaded, and returns what ShowDialog
    /// returned -- for a test that drives a dialog to close itself (DialogResult only works on
    /// a window opened with ShowDialog). If <paramref name="interact"/> leaves the dialog open,
    /// it's closed afterwards, so the test still ends.
    /// </summary>
    public static async Task<bool?> ShowDialogAsync<T>(T window, Func<T, Task> interact) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;

        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await IdleAsync();
                await interact(window);
                await IdleAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (window.IsVisible)
                    window.Close();
            }
        };

        var result = window.ShowDialog();
        if (failure is not null)
            throw new InvalidOperationException("The dialog interaction failed.", failure);
        return result;
    }

    /// <summary>Lets the dispatcher run everything already queued, layout and bindings included.</summary>
    public static async Task IdleAsync() =>
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // The same licence App registers, so a Syncfusion control doesn't stop to show
                // its trial notice.
                if (Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY") is { Length: > 0 } key)
                    SyncfusionLicenseProvider.RegisterLicense(key);

                ReactiveTestSetup.EnsureInitialized();
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var dictionary in AppDictionaries)
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/ScheduleApp;component/Themes/{dictionary}.xaml"),
                    });
                }

                ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
