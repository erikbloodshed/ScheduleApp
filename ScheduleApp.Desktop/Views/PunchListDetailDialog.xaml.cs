using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>Read-only punch list opened from the Summary tab's Orphaned/Unscheduled tiles --
/// see <see cref="ViewModels.Attendance.PunchListDetailViewModel"/>.</summary>
public partial class PunchListDetailDialog
{
    /// <summary>Shown for a PunchListDetailViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public PunchListDetailDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(ViewModel!, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Rows, v => v.RowsGrid.ItemsSource).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ExportCommand, v => v.ExportButton).DisposeWith(d);
        });
    }
}
