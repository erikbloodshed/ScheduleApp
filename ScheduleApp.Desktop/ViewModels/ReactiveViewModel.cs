using System.ComponentModel;
using System.Reactive.Linq;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// A ReactiveObject whose commands' CanExecute is a plain bool method (CanSaveX()), re-asked
/// whenever this ViewModel's own properties change and whenever <see cref="RequeryCanExecute"/>
/// is called -- the ReactiveUI form of a CommunityToolkit [RelayCommand(CanExecute = ...)] and
/// the NotifyCanExecuteChanged() calls that kept it current -- and the Yes/No questions and
/// notices a ViewModel asks (Confirm, Notify). The base of ViewModelBase, and of the few
/// ViewModels with commands but nothing to report on the status bar.
/// </summary>
public abstract class ReactiveViewModel : ReactiveObject
{
    /// <summary>A Yes/No question; the output is true for Yes. The View answers it with a
    /// MessageBox (Views/MessageBoxInteractions.cs), so the ViewModel never shows a window.</summary>
    public Interaction<Confirmation, bool> Confirm { get; } = new();

    /// <summary>A message the user has to acknowledge (OK only), for one too long or too
    /// important for the status bar, such as the problems an import found.</summary>
    public Interaction<Notice, RxVoid> Notify { get; } = new();

    /// <summary>Asks <paramref name="message"/> as a Yes/No question (see <see cref="Confirm"/>).</summary>
    protected async Task<bool> ConfirmAsync(string message, string title, bool isWarning = false) =>
        await Confirm.Handle(new Confirmation(title, message, isWarning));

    /// <summary>Shows <paramref name="message"/> until the user acknowledges it (see <see cref="Notify"/>).</summary>
    protected async Task NotifyAsync(string message, string title, NoticeKind kind = NoticeKind.Information) =>
        await Notify.Handle(new Notice(title, message, kind));

    /// <summary>Raised by <see cref="RequeryCanExecute"/>.</summary>
    private event EventHandler? Requeried;

    /// <summary>Re-asks every command built with <see cref="CanExecuteFrom"/> whether it can
    /// run now -- for a change to something the commands read that this ViewModel doesn't
    /// raise PropertyChanged for itself, such as shared state another object owns.</summary>
    protected void RequeryCanExecute() => Requeried?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// A command's CanExecute from <paramref name="canExecute"/>, re-asked whenever this
    /// ViewModel raises PropertyChanged, <see cref="RequeryCanExecute"/> is called, or any of
    /// <paramref name="sources"/> raises PropertyChanged. Asked first when the command
    /// subscribes, not when this is called, so it can be built before every field the condition
    /// reads is set.
    /// </summary>
    protected IObservable<bool> CanExecuteFrom(Func<bool> canExecute, params INotifyPropertyChanged[] sources) =>
        Observable.Defer(() => Observable.Return(canExecute()))
            .Concat(Observable.Merge(
                    [
                        Observable.FromEventPattern(handler => Requeried += handler, handler => Requeried -= handler)
                            .Select(_ => RxVoid.Default),
                        PropertyChangedOf(this),
                        .. sources.Select(PropertyChangedOf),
                    ])
                .Select(_ => canExecute()))
            .DistinctUntilChanged();

    /// <summary>
    /// A command's CanExecute that re-asks <paramref name="canExecute"/> whenever any of
    /// <paramref name="sources"/> raises PropertyChanged -- for a condition read off shared state
    /// some other object owns (AttendanceBusyState.IsRunning, MultiSelectModeState, a sibling's
    /// selection), where a WhenAnyValue on this ViewModel's own properties can't see the change.
    /// </summary>
    protected static IObservable<bool> CanExecuteWhen(Func<bool> canExecute, params INotifyPropertyChanged[] sources) =>
        Observable.Defer(() => Observable.Return(canExecute()))
            .Concat(sources.Select(PropertyChangedOf).Merge().Select(_ => canExecute()))
            .DistinctUntilChanged();

    private static IObservable<RxVoid> PropertyChangedOf(INotifyPropertyChanged source) =>
        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => source.PropertyChanged += handler,
                handler => source.PropertyChanged -= handler)
            .Select(_ => RxVoid.Default);
}
