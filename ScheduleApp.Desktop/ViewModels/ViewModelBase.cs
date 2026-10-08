using System.Reactive.Linq;
using ReactiveUI;
using ScheduleApp.Desktop.Services;
using Serilog;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Shared behaviour for every screen's ViewModel: a failed action or load is shown on the status
/// bar in <see cref="Failure"/>'s words and logged, instead of failing silently or reaching
/// ReactiveUI (whose unhandled-error path ends at App's DispatcherUnhandledException), and the
/// Yes/No questions and notices a screen asks go through <see cref="Confirm"/> and
/// <see cref="Notify"/>, which the View answers with a MessageBox (see
/// Views/MessageBoxInteractions.cs) -- so the ViewModel itself never shows a window.
///
/// Row and node ViewModels (a tree node, a grid row) derive from ReactiveObject directly: they
/// have nothing to report.
/// </summary>
public abstract class ViewModelBase(IStatusBarService statusBarService) : ReactiveObject
{
    private ILogger? _logger;

    protected IStatusBarService StatusBar { get; } = statusBarService;

    protected ILogger Logger => _logger ??= Log.ForContext(GetType());

    /// <summary>A Yes/No question; the output is true for Yes.</summary>
    public Interaction<Confirmation, bool> Confirm { get; } = new();

    /// <summary>A message the user has to acknowledge (OK only), for one too long or too
    /// important for the status bar, such as the problems an import found.</summary>
    public Interaction<Notice, RxVoid> Notify { get; } = new();

    /// <summary>
    /// Runs <paramref name="action"/>. A cancellation (a newer run took over, or the app is
    /// closing) is quiet; anything else is shown and logged (<see cref="ShowFailure"/>).
    /// </summary>
    protected async Task RunSafelyAsync(Func<Task> action, string? failureTitle = null)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex, failureTitle);
        }
    }

    /// <summary>
    /// Loads what each of <paramref name="values"/> needs as it comes (an employee or a date
    /// picked), a newer value cancelling a load still running. A load that fails is shown and
    /// logged, and the next value loads as usual: a failure reaching Switch would end the
    /// subscription, and from then on no pick would load anything until the app restarted. A
    /// load cancelled by a newer value isn't a failure, whatever it threw on its way out
    /// (SqlClient reports a command cancelled mid-read as a SqlException).
    /// </summary>
    protected IDisposable LoadLatest<T>(IObservable<T> values, Func<T, CancellationToken, Task> load) =>
        Observable.Switch(values.Select(value => Observable.FromAsync(async cancellationToken =>
            {
                try
                {
                    await load(value, cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // A newer value took over.
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    ShowFailure(ex);
                }
            })))
            .Subscribe();

    /// <summary>
    /// Shows <paramref name="exception"/> on the status bar in <see cref="Failure"/>'s words, under
    /// <paramref name="title"/>, and logs it: a refusal as a warning, anything else as an error
    /// with its stack.
    /// </summary>
    protected void ShowFailure(Exception exception, string? title = null)
    {
        var failure = Failure.Of(exception);
        StatusBar.ShowError(failure.Text, title ?? "Error");

        if (failure.IsRejection)
            Logger.Warning(exception, "Refused: {Reason}", failure.Text);
        else
            Logger.Error(exception, "Failed: {Reason}", failure.Text);
    }

    /// <summary>Asks <paramref name="message"/> as a Yes/No question (see <see cref="Confirm"/>).</summary>
    protected async Task<bool> ConfirmAsync(string message, string title, bool isWarning = false) =>
        await Confirm.Handle(new Confirmation(title, message, isWarning));

    /// <summary>Shows <paramref name="message"/> until the user acknowledges it (see <see cref="Notify"/>).</summary>
    protected async Task NotifyAsync(string message, string title, NoticeKind kind = NoticeKind.Information) =>
        await Notify.Handle(new Notice(title, message, kind));
}

/// <summary>A Yes/No question for <see cref="ViewModelBase.Confirm"/>. A warning is one whose Yes can't be taken back.</summary>
public sealed record Confirmation(string Title, string Message, bool IsWarning = false);

/// <summary>A message for <see cref="ViewModelBase.Notify"/>.</summary>
public sealed record Notice(string Title, string Message, NoticeKind Kind = NoticeKind.Information);

public enum NoticeKind
{
    Information,
    Warning,
    Error,
}
