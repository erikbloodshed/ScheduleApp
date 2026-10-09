using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Syncfusion.UI.Xaml.Grid;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// Keeps an SfDataGrid's Auto-sized columns fitted to their rows. SfDataGrid measures an Auto
/// column once, when the grid first lays out, and doesn't measure it again when rows arrive
/// later: a grid that loads after it's shown keeps the widths of its headers, and cuts its text
/// off. With <c>controls:GridAutoFit.IsEnabled="True"</c> the grid re-measures them whenever
/// its ItemsSource, or the collection behind it, changes. A load that adds rows one by one is
/// measured once, after the last of them. A grid that isn't showing (collapsed until it has
/// rows, say) has nothing laid out to measure, so it's measured once it shows.
/// </summary>
public static class GridAutoFit
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GridAutoFit), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(GridAutoFit));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SfDataGrid grid || e.NewValue is not true || grid.GetValue(StateProperty) is State)
            return;

        var state = new State(grid);
        grid.SetValue(StateProperty, state);
        grid.ItemsSourceChanged += (_, _) => state.Watch(grid.ItemsSource);
        state.Watch(grid.ItemsSource);
    }

    private sealed class State
    {
        private readonly SfDataGrid _grid;
        private INotifyCollectionChanged? _watched;
        private bool _pending;

        public State(SfDataGrid grid)
        {
            _grid = grid;
            grid.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                    Schedule();
            };
        }

        public void Watch(object? itemsSource)
        {
            _watched?.CollectionChanged -= OnCollectionChanged;

            _watched = itemsSource as INotifyCollectionChanged;
            _watched?.CollectionChanged += OnCollectionChanged;

            Schedule();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Schedule();

        private void Schedule()
        {
            if (_pending)
                return;

            _pending = true;
            _grid.Dispatcher.BeginInvoke(Fit, DispatcherPriority.Background);
        }

        private void Fit()
        {
            _pending = false;
            if (!_grid.IsVisible || _grid.GridColumnSizer is not { } sizer)
                return;

            // The sizer measures with its own default font and padding unless told otherwise,
            // which comes out narrower than the theme's cells. SemiBold so a bold cell (a Status
            // column) fits too; a regular one just gets a little spare room.
            sizer.FontFamily = _grid.FontFamily;
            sizer.FontSize = _grid.FontSize;
            sizer.FontWeight = FontWeights.SemiBold;
            sizer.Margin = new Thickness(10, 0, 10, 0);

            sizer.ResetAutoCalculationforAllColumns();
            sizer.Refresh();
        }
    }
}
