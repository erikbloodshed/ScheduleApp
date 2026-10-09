using ReactiveUI.Primitives.Disposables;

namespace ScheduleApp.Desktop.Reactive;

/// <summary>
/// <c>DisposeWith</c> for the <see cref="MultipleDisposable"/> a view's <c>WhenActivated</c> hands
/// its block: ReactiveUI 26 collects activation-scoped subscriptions in its own disposable bag
/// rather than System.Reactive's CompositeDisposable, and ships no DisposeWith for it.
/// </summary>
public static class DisposableExtensions
{
    extension<T>(T disposable) where T : IDisposable
    {
        /// <summary>Adds this to <paramref name="bag"/>, so it's disposed when the view deactivates.</summary>
        public T DisposeWith(MultipleDisposable bag)
        {
            bag.Add(disposable);
            return disposable;
        }
    }
}
