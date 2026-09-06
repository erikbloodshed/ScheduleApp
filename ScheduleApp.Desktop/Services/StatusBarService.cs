using System.Windows.Media;
using ScheduleApp.Desktop.Controls;
using Wpf.Ui.Controls;

namespace ScheduleApp.Desktop.Services;

/// <inheritdoc cref="IStatusBarService" />
public class StatusBarService : IStatusBarService
{
    // Same four colors Wpf.Ui's own ControlAppearance brushes used for
    // Success/Info/Caution/Danger, kept as plain brushes here since the status bar
    // is a light-themed strip rather than a themed Snackbar surface.
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x1A, 0x7D, 0x36));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0x2D, 0x6C, 0xDF));
    private static readonly SolidColorBrush CautionBrush = new(Color.FromRgb(0xB2, 0x6A, 0x00));
    private static readonly SolidColorBrush DangerBrush = new(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private StatusBarPresenter? _presenter;

    public void SetStatusBarPresenter(StatusBarPresenter statusBarPresenter) =>
        _presenter = statusBarPresenter;

    public void Show(string title, string message, ControlAppearance appearance, IconElement icon, TimeSpan timeout)
    {
        var color = appearance switch
        {
            ControlAppearance.Success => SuccessBrush,
            ControlAppearance.Info => InfoBrush,
            ControlAppearance.Caution => CautionBrush,
            ControlAppearance.Danger => DangerBrush,
            _ => InfoBrush,
        };

        // No presenter yet (e.g. called before MainWindow's constructor runs) just
        // drops the message rather than throwing -- same tolerance the old
        // ISnackbarService had before SetSnackbarPresenter had been called.
        _presenter?.ShowMessage(title, message, color, icon, timeout);
    }

    public void ShowProgress(string label, double percent) => _presenter?.ShowProgress(label, percent);

    public void ShowProgress(string label) => _presenter?.ShowProgress(label);

    public void ClearProgress() => _presenter?.ClearProgress();
}
