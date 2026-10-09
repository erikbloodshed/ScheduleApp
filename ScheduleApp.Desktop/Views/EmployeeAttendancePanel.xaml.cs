using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>Slim, read-only attendance grid (PayrollSummaryViewModel.AttendanceRows) for whichever
/// employee/period is selected on the Payroll tab -- deliberately not the full AttendanceView
/// (which would nest a second tree/toolbar/tabs redundantly), just the basis-of-the-numbers view
/// sitting below PayrollSummaryView, scoped to one employee. PayrollPage hands it the
/// ViewModel.</summary>
public partial class EmployeeAttendancePanel
{
    public EmployeeAttendancePanel()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            this.OneWayBind(ViewModel, vm => vm.AttendanceEmptyStateMessage, v => v.EmptyStateText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.AttendanceEmptyStateMessage, v => v.EmptyStatePanel.Visibility,
                message => message is null ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.AttendanceRows, v => v.AttendanceGrid.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HasAttendanceRows, v => v.AttendanceGrid.Visibility,
                hasRows => hasRows ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);
        });
    }
}
