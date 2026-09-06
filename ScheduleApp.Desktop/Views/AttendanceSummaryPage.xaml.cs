using System.Windows.Controls;
using ScheduleApp.Desktop.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ScheduleApp.Desktop.Views;

/// <summary>One of the three pages the "Attendance" drawer item's submenu navigates
/// between (see MainWindow.xaml) -- Summary, Punch Records (PunchRecordsPage), and
/// Manual Entries (ManualEntriesPage), replacing the old single AttendancePage's
/// internal TabControl. All three take the exact same DI-Scoped AttendanceViewModel
/// instance (see App.xaml.cs's own comment on that registration -- Scoped behaves like
/// a per-session Singleton here, since the app only ever creates one IServiceScope), so
/// navigating between them keeps whatever was already loaded rather than re-querying
/// the database, the same as switching TabItems used to.</summary>
public partial class AttendanceSummaryPage : Page, INavigationAware
{
    private readonly AttendanceViewModel _viewModel;

    public AttendanceSummaryPage(AttendanceViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // EnsureInitializedAsync is idempotent (see its own doc comment) -- whichever of the
    // three Attendance pages the person navigates to first is the one that actually pays
    // for the employee-tree load; the other two's own calls are then no-ops. Awaited here
    // regardless, so ActivateSummaryTab below never runs before the tab-activation gate
    // it depends on is open.
    public async Task OnNavigatedToAsync()
    {
        await _viewModel.EnsureInitializedAsync();
        _viewModel.ActivateSummaryTab();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
