using System.Windows;
using System.Windows.Media;
using ScheduleApp.Desktop.Controls;

namespace ScheduleApp.Desktop.Services;

/// <inheritdoc cref="IStatusBarService" />
public class StatusBarService : IStatusBarService
{
    // Each kind's colour is its status token's text shade (Themes/Tokens.xaml), looked up
    // per call rather than cached so the bar always matches the tokens; the fallbacks are
    // only for a call before App.xaml has loaded, which doesn't happen in practice. The
    // glyphs are Segoe Fluent Icons: CompletedSolid, InfoSolid, Warning, StatusErrorFull.
    private static Brush TokenBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private StatusBarPresenter? _presenter;

    public void SetStatusBarPresenter(StatusBarPresenter statusBarPresenter) =>
        _presenter = statusBarPresenter;

    public void Show(string title, string message, StatusKind kind, TimeSpan timeout)
    {
        var (color, glyph) = kind switch
        {
            StatusKind.Success => (TokenBrush("SuccessForegroundBrush", Color.FromRgb(0x2D, 0x70, 0x1E)), "\uEC61"),
            StatusKind.Caution => (TokenBrush("WarningForegroundBrush", Color.FromRgb(0x91, 0x5C, 0x13)), "\uE7BA"),
            StatusKind.Error => (TokenBrush("ErrorForegroundBrush", Color.FromRgb(0xBD, 0x0E, 0x33)), "\uEB90"),
            _ => (TokenBrush("LinkForegroundBrush", Color.FromRgb(0x1B, 0x5C, 0xDC)), "\uF167"),
        };

        // No presenter yet (e.g. called before MainWindow's constructor runs) just
        // drops the message rather than throwing -- same tolerance the old
        // ISnackbarService had before SetSnackbarPresenter had been called.
        _presenter?.ShowMessage(title, message, color, glyph, timeout);
    }

    public void ShowProgress(string label, double percent) => _presenter?.ShowProgress(label, percent);

    public void ShowProgress(string label) => _presenter?.ShowProgress(label);

    public void ClearProgress() => _presenter?.ClearProgress();
}
