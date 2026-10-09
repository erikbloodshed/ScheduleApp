using ReactiveUI;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>One of the three pages the "Attendance" drawer item's submenu navigates
/// between -- see AttendanceSummaryPage's own doc comment for the shared
/// AttendanceViewModel story all three follow.</summary>
public partial class PunchRecordsPage : INavigationAware
{
    public PunchRecordsPage(AttendanceViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        RecordsView.ViewModel = viewModel;

        // Import… asks which punch log to read; Export asks where to save, then opens it.
        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(viewModel.Import, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.DeviceFetch, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.PunchRecords, this).DisposeWith(d);
        });
    }

    public async Task OnNavigatedToAsync()
    {
        await ViewModel!.EnsureInitializedAsync();
        ViewModel.ActivatePunchRecordsTab();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
