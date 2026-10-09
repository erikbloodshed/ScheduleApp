using ReactiveUI;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>One of the three pages the "Attendance" drawer item's submenu navigates
/// between (see MainWindow.xaml) -- Summary, Punch Records (PunchRecordsPage), and
/// Manual Entries (ManualEntriesPage). All three take the exact same DI-Scoped
/// AttendanceViewModel instance (Scoped behaves like a per-session Singleton here, since the
/// app only ever creates one IServiceScope), so navigating between them keeps whatever was
/// already loaded rather than re-querying the database.</summary>
public partial class AttendanceSummaryPage : INavigationAware
{
    public AttendanceSummaryPage(AttendanceViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        SummaryView.ViewModel = viewModel;

        // The punch lists, the export's save picker, Add Manual Entry/Edit Punches from a row.
        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(viewModel.Report, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.ManualEntryEditor, this).DisposeWith(d);
        });
    }

    // EnsureInitializedAsync is idempotent -- whichever of the three Attendance pages is
    // visited first pays for the employee-tree load. Awaited regardless, so ActivateSummaryTab
    // never runs before the tab-activation gate it depends on is open.
    public async Task OnNavigatedToAsync()
    {
        await ViewModel!.EnsureInitializedAsync();
        ViewModel.ActivateSummaryTab();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
