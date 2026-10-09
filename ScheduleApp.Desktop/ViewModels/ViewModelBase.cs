using System.Reactive.Linq;
using ReactiveUI;
using ScheduleApp.Desktop.Services;
using Serilog;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Shared behaviour for every screen's ViewModel: a failed action or load is shown on the status
/// bar in <see cref="Failure"/>'s words and logged, instead of failing silently or reaching
/// ReactiveUI (whose unhandled-error path ends at App's DispatcherUnhandledException), and the
/// Yes/No questions and notices a screen asks go through <see cref="Confirm"/> and
/// <see cref="Notify"/>, which the View answers with a MessageBox (see
/// Views/ViewInteractions.cs) -- so the ViewModel itself never shows a window.
///
/// Commands are [ReactiveCommand]s whose CanExecute is an observable built from WhenAnyValue
/// (shared state included -- AttendanceBusyState.IsRunning and the like), and whose failures
/// reach the status bar through <see cref="ReportFailuresOf(IHandleObservableErrors[])"/>. Row and
/// node ViewModels (a tree node, a grid row) derive from ReactiveObject directly: they have
/// nothing to report.
/// </summary>
public abstract class ViewModelBase(IStatusBarService statusBarService) : ReactiveViewModel
{
    private ILogger? _logger;

    protected IStatusBarService StatusBar { get; } = statusBarService;

    protected ILogger Logger => _logger ??= Log.ForContext(GetType());

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
    /// Shows and logs whatever <paramref name="commands"/> fail with: a cancellation (a newer run
    /// took over, or the app is closing) is quiet, anything else goes to
    /// <see cref="ShowFailure"/> under <paramref name="title"/>. A command's failure
    /// arrives on its ThrownExceptions, and one nothing observes there is rethrown -- ending at
    /// App's DispatcherUnhandledException -- so every command that can fail is passed here.
    ///
    /// Call it last in the constructor: a [ReactiveCommand] is created the first time it's read,
    /// and the CanExecute observable it names has to be assigned by then.
    /// </summary>
    protected void ReportFailuresOf(string? title, params IHandleObservableErrors[] commands)
    {
        foreach (var command in commands)
        {
            command.ThrownExceptions
                .Where(exception => exception is not OperationCanceledException)
                .Subscribe(exception => ShowFailure(exception, title));
        }
    }

    /// <summary><see cref="ReportFailuresOf(string?, IHandleObservableErrors[])"/> under the
    /// default title.</summary>
    protected void ReportFailuresOf(params IHandleObservableErrors[] commands) => ReportFailuresOf(null, commands);

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
}

/// <summary>A Yes/No question for <see cref="ReactiveViewModel.Confirm"/>. A warning is one whose Yes can't be taken back.</summary>
public sealed record Confirmation(string Title, string Message, bool IsWarning = false);

/// <summary>What <see cref="ReactiveViewModel.PickFileToOpen"/>/<see cref="ReactiveViewModel.PickFileToSave"/>
/// ask for: the file types to offer (a file dialog's filter string), the name to start with, the
/// dialog's title, and the folder to start in.</summary>
public sealed record FileRequest(string Filter, string? FileName = null, string? Title = null, string? InitialDirectory = null);

/// <summary>A message for <see cref="ReactiveViewModel.Notify"/>.</summary>
public sealed record Notice(string Title, string Message, NoticeKind Kind = NoticeKind.Information);

public enum NoticeKind
{
    Information,
    Warning,
    Error,
}
