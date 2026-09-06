using System.Windows;
using System.Windows.Controls;

namespace ScheduleApp.Desktop.Controls;

/// <summary>Progress bar with a centered percentage label - used by StatusBarPresenter's
/// left-aligned progress area (see that control's ShowProgress/ClearProgress) for two
/// different shapes of caller: a determinate compute loop that has a real 0-100 figure to
/// report (PayrollGroupViewModel.PayrollGroupLoadPercent, via the 2-argument ShowProgress
/// overload), and the shared "something's running, no per-item percent to report" flag
/// every other page's own busy state reduces to (AttendanceBusyState.IsVisiblyRunning, via
/// the 1-argument overload -- see IsIndeterminate below). See PercentProgressBar.xaml for
/// why the label sits centered on the bar rather than riding the fill's edge, and for how
/// IsIndeterminate swaps the percent label out for WPF's own built-in indeterminate
/// animation instead. This class only owns the Value/IsIndeterminate dependency properties
/// everything else in the control binds to via ElementName=Root.</summary>
public partial class PercentProgressBar : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(PercentProgressBar), new PropertyMetadata(0.0));

    /// <summary>False (the default) is the original determinate shape: a plain 0-100
    /// ProgressBar with Value's own percentage centered on top of it. True switches the
    /// underlying ProgressBar into WPF's own built-in indeterminate animation instead, and
    /// hides the percent label (see PercentProgressBar.xaml's DataTrigger) -- there's no
    /// meaningful percent to show a caller like AttendanceBusyState, which only ever knows
    /// "something is running," not how far along it is. Value itself is simply ignored by
    /// the underlying ProgressBar while this is true (WPF's own IsIndeterminate semantics),
    /// so callers don't need to also reset Value back to 0 when switching modes.</summary>
    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(
            nameof(IsIndeterminate), typeof(bool), typeof(PercentProgressBar), new PropertyMetadata(false));

    public PercentProgressBar()
    {
        InitializeComponent();
    }

    /// <summary>0-100. Set from StatusBarPresenter.ShowProgress each time
    /// PayrollViewModel.PayrollGroupLoadPercent changes -- see that property's own doc
    /// comment. Meaningless (and ignored by the underlying ProgressBar) while
    /// IsIndeterminate is true.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>See this field's own doc comment above the DependencyProperty
    /// registration.</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }
}
