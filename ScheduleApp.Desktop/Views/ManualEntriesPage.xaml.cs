using System.Windows.Controls;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>One of the three pages the "Attendance" drawer item's submenu navigates
/// between -- see AttendanceSummaryPage's own doc comment for the shared
/// AttendanceViewModel story all three follow.</summary>
public partial class ManualEntriesPage : Page, INavigationAware
{
    private readonly AttendanceViewModel _viewModel;

    public ManualEntriesPage(AttendanceViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Deleting a manual entry asks first, and an import can have a list of problems to
        // show -- the ViewModels' Confirm/Notify.
        MessageBoxInteractions.Register(viewModel.ManualEntryEditor, this);
        MessageBoxInteractions.Register(viewModel.ManualEntriesTab, this);
    }

    public async Task OnNavigatedToAsync()
    {
        await _viewModel.EnsureInitializedAsync();
        _viewModel.ActivateManualEntriesTab();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
