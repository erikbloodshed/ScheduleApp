using ReactiveUI;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>One of the three pages the "Attendance" drawer item's submenu navigates
/// between -- see AttendanceSummaryPage's own doc comment for the shared
/// AttendanceViewModel story all three follow.</summary>
public partial class ManualEntriesPage : INavigationAware
{
    public ManualEntriesPage(AttendanceViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        EntriesView.ViewModel = viewModel;

        // Deleting a manual entry asks first, Add/Edit open the entry dialog, and an import
        // can have a list of problems to show.
        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(viewModel.ManualEntryEditor, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.ManualEntriesTab, this).DisposeWith(d);
        });
    }

    public async Task OnNavigatedToAsync()
    {
        await ViewModel!.EnsureInitializedAsync();
        ViewModel.ActivateManualEntriesTab();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}
