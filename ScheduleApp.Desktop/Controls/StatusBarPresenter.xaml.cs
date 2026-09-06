using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// The status bar's notification area -- see MainWindow.xaml (docked along the
/// bottom of the window, right-aligned within the bar) and StatusBarService,
/// which owns the single instance of this control (RootStatusBarPresenter) the
/// same way RootSnackbarPresenter used to be owned by Wpf.Ui's ISnackbarService.
/// A new ShowMessage call always replaces whatever's currently shown and resets
/// the auto-clear timer, same "one at a time" behavior the Snackbar had.
/// </summary>
public partial class StatusBarPresenter : UserControl
{
    private readonly DispatcherTimer _clearTimer = new();

    public StatusBarPresenter()
    {
        InitializeComponent();

        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            Clear();
        };
    }

    /// <summary>Shows title/message right-aligned in the bar with the given icon and
    /// color, then auto-clears after timeout -- icon is typically a fresh
    /// Wpf.Ui.Controls.SymbolIcon per call (see StatusBarNotificationExtensions), so
    /// no reuse/parent-detach concerns swapping IconHost.Content here.</summary>
    public void ShowMessage(string title, string message, Brush color, UIElement icon, TimeSpan timeout)
    {
        _clearTimer.Stop();

        IconHost.Content = icon;
        TitleRun.Text = string.IsNullOrWhiteSpace(title) ? string.Empty : title;
        TitleRun.Foreground = color;
        MessageRun.Text = string.IsNullOrWhiteSpace(title) ? message : $": {message}";
        MessageRun.Foreground = color;
        MessagePanel.Visibility = Visibility.Visible;

        _clearTimer.Interval = timeout;
        _clearTimer.Start();
    }

    public void Clear()
    {
        _clearTimer.Stop();
        MessagePanel.Visibility = Visibility.Collapsed;
        IconHost.Content = null;
    }

    /// <summary>Shows/updates the left-aligned progress indicator -- a short label plus
    /// PercentProgressBar's determinate fill (see that control). Deliberately has
    /// no auto-clear timer of its own the way ShowMessage/_clearTimer does: the caller
    /// (e.g. PayrollViewModel's PayrollGroupLoadPercent/IsPayrollGroupLoading handlers)
    /// knows exactly when its own compute loop finishes and calls ClearProgress itself,
    /// rather than this guessing a timeout for a run that could take anywhere from
    /// under a second to several seconds depending on how many employees are
    /// involved.</summary>
    public void ShowProgress(string label, double percent)
    {
        ProgressBarControl.IsIndeterminate = false;
        ProgressLabelRun.Text = label;
        ProgressBarControl.Value = percent;
        ProgressPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Same left-aligned indicator as the determinate overload above, but for a
    /// caller with no meaningful percent to report -- AttendanceBusyState.IsVisiblyRunning
    /// chief among them, the single shared "something's running" flag every page's own
    /// Import/Fetch/Generate Reports/Load/Save/etc. reduces to (see that class's own
    /// OnIsVisiblyRunningChanged). Switches PercentProgressBar into its own
    /// IsIndeterminate mode (WPF's built-in marquee animation, percent label hidden --
    /// see that control's own doc comment) rather than showing a 0%/100% figure that
    /// would just be noise. Same no-auto-clear-timer reasoning as the determinate
    /// overload: the caller pairs this with its own ClearProgress once whatever's running
    /// finishes.</summary>
    public void ShowProgress(string label)
    {
        ProgressBarControl.IsIndeterminate = true;
        ProgressLabelRun.Text = label;
        ProgressPanel.Visibility = Visibility.Visible;
    }

    public void ClearProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
    }
}
