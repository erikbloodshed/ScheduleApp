using ScheduleApp.Desktop.Controls;
using Wpf.Ui.Controls;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Replaces Wpf.Ui's ISnackbarService -- same shape (a presenter the host window
/// hands over once, then a single Show call every ViewModel goes through), just
/// backed by the bottom status bar's right-aligned notification area instead of a
/// popup. See StatusBarService for the ControlAppearance-to-color mapping and
/// Controls/StatusBarPresenter for the actual UI.
/// </summary>
public interface IStatusBarService
{
    void SetStatusBarPresenter(StatusBarPresenter statusBarPresenter);

    void Show(string title, string message, ControlAppearance appearance, IconElement icon, TimeSpan timeout);

    /// <summary>Left-aligned determinate progress, independent of Show's right-aligned
    /// notification -- see StatusBarPresenter.ShowProgress. Callers own their own
    /// 0-100 percent (e.g. PayrollViewModel.PayrollGroupLoadPercent) and must pair this
    /// with a matching ClearProgress once their work finishes; there's no timeout here
    /// the way Show's notifications have.</summary>
    void ShowProgress(string label, double percent);

    /// <summary>Same left-aligned area, indeterminate overload -- for a caller with no
    /// meaningful percent to report, like AttendanceBusyState.IsVisiblyRunning (the one
    /// shared "something's running" flag every page's own busy state reduces to). See
    /// StatusBarPresenter.ShowProgress's own indeterminate overload for the visual this
    /// drives. Same ClearProgress pairing requirement as the determinate overload.</summary>
    void ShowProgress(string label);

    void ClearProgress();
}
