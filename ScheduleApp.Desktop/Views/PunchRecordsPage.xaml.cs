using System.Windows.Controls;
using ScheduleApp.Desktop.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ScheduleApp.Desktop.Views;

/// <summary>One of the three pages the "Attendance" drawer item's submenu navigates
/// between -- see AttendanceSummaryPage's own doc comment for the shared
/// AttendanceViewModel story all three follow.</summary>
public partial class PunchRecordsPage : Page, INavigationAware
{
    private readonly AttendanceViewModel _viewModel;

    public PunchRecordsPage(AttendanceViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public async Task OnNavigatedToAsync()
    {
        await _viewModel.EnsureInitializedAsync();
        _viewModel.ActivatePunchRecordsTab();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
