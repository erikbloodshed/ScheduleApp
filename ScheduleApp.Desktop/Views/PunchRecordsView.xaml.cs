using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

/// <summary>The Punch Records page's content: the range and search toolbar, Import…/Fetch from
/// Device (importing punches is what this page is for), and the grid of device punches (see
/// PunchRecordsViewModel). PunchRecordsPage hands it the AttendanceViewModel.</summary>
public partial class PunchRecordsView
{
    public PunchRecordsView()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            this.BindCommand(ViewModel, vm => vm.PunchRecords.PreviousPeriodCommand, v => v.PreviousPeriodButton).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PunchRecords.LogViewStart, v => v.StartPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PunchRecords.LogViewEnd, v => v.EndPicker.DateTime).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.PunchRecords.NextPeriodCommand, v => v.NextPeriodButton).DisposeWith(d);

            this.Bind(ViewModel, vm => vm.PunchRecords.LogViewSearchText, v => v.PunchSearchTextBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchRecords.LogViewSuggestions, v => v.PunchSearchSuggestionList.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PunchRecords.IsLogViewSuggestionsOpen, v => v.SuggestionsPopup.IsOpen).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.PunchRecords.RefreshOrCancelStoredLogsCommand, v => v.RefreshOrCancelButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchRecords.RefreshOrCancelContent, v => v.RefreshOrCancelButton.Label).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchRecords.RefreshOrCancelIcon, v => v.RefreshOrCancelButton.Tag).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchRecords.RefreshOrCancelToolTip, v => v.RefreshOrCancelButton.ToolTip).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.PunchRecords.ExportStoredLogsCommand, v => v.ExportButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Import.ImportPunchLogCommand, v => v.ImportButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.DeviceFetch.FetchFromDeviceCommand, v => v.FetchButton).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.PunchRecords.StoredLogsCount, v => v.CountRun.Text,
                count => count.ToString(CultureInfo.CurrentCulture)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchRecords.HasLoadedStoredLogs, v => v.CountLine.Visibility,
                loaded => loaded ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PunchRecords.StoredLogsView, v => v.LogsGrid.ItemsSource).DisposeWith(d);
        });
    }

    /// <summary>A suggestion clicked in the search dropdown -- a ListBox has no "item clicked"
    /// command hook, so this finds which one and hands it to the ViewModel, which owns the
    /// rest.</summary>
    private void PunchSearchSuggestionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(PunchSearchSuggestionList, source) is not ListBoxItem { Content: PunchSearchSuggestion suggestion })
            return;

        ViewModel?.PunchRecords.SelectLogViewSuggestionCommand.Execute(suggestion).Subscribe();
        e.Handled = true;
    }
}
