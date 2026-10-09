using System.Collections.Specialized;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Converters;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.ScrollAxis;

namespace ScheduleApp.Desktop.Views;

/// <summary>The Payroll tab (see <see cref="PayrollViewModel"/>): the page-level command row
/// (period pickers, print/export, new/load run), the Payroll Group table and the "Not in group"
/// roster on the left, and PayrollSummaryView/EmployeeAttendancePanel on the right, both shown
/// the PayrollSummaryViewModel. The selected employee is the app-wide one, so a row click here
/// selects the same employee the Schedule/Employees trees would -- no load call of its own:
/// by the time this page can be navigated to, SchedulePage (the default page) has already
/// loaded the roster.</summary>
public partial class PayrollPage : INavigationAware
{
    public PayrollPage(PayrollViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        SummaryViewControl.ViewModel = viewModel.Summary;
        AttendancePanelControl.ViewModel = viewModel.Summary;

        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(viewModel.Summary, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.Group, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.Run, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.PrintExport, this).DisposeWith(d);

            // A cleared picker leaves the period as it was.
            this.Bind(ViewModel, vm => vm.Scope.PeriodStart, v => v.PeriodStartPicker.DateTime,
                start => start, picked => picked ?? viewModel.Scope.PeriodStart).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Scope.PeriodEnd, v => v.PeriodEndPicker.DateTime,
                end => end, picked => picked ?? viewModel.Scope.PeriodEnd).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.Summary.PrintCurrentPayslipCommand, v => v.PrintCurrentPayslipButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.PrintExport.PrintPayslipsCommand, v => v.PrintPayslipsButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.PrintExport.ExportPayrollReportCommand, v => v.ExportPayrollReportButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Run.NewPayrollRunCommand, v => v.NewPayrollRunButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Run.LoadPayrollGroupCommand, v => v.LoadPayrollGroupButton).DisposeWith(d);

            // Payroll Group panel
            this.Bind(ViewModel, vm => vm.Group.PayrollGroupSearchText, v => v.GroupSearchBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Group.AddEmployeesToGroupCommand, v => v.AddToGroupButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Group.PayrollGroupRowsView, v => v.PayrollGroupGrid.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Group.AvailableEmployeeRowsView, v => v.AvailableEmployeeGrid.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Group.TotalNetPay, v => v.TotalNetPayText.Text, NumberConverter.Format).DisposeWith(d);

            // Include/Exclude act on whichever row is selected in their grid -- a silent no-op
            // with none selected.
            this.BindCommand(ViewModel, vm => vm.Group.AddEmployeeToGroupCommand, v => v.IncludeButton,
                this.WhenAnyValue(v => v.AvailableEmployeeGrid.SelectedItem).Select(item => (item as AvailableEmployeeRow)?.Employee)).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Group.RemoveEmployeeFromGroupCommand, v => v.ExcludeButton,
                this.WhenAnyValue(v => v.PayrollGroupGrid.SelectedItem).Select(item => (item as PayrollGroupRow)?.Employee)).DisposeWith(d);

            // A row click selects that employee app-wide -- never null: there's no "deselect"
            // in the table. SfDataGrid raises SelectionChanged only for the person's own clicks,
            // so RestorePayrollGroupSelection setting SelectedItem doesn't come back through here.
            Observable.FromEventPattern<EventHandler<GridSelectionChangedEventArgs>, GridSelectionChangedEventArgs>(
                    handler => PayrollGroupGrid.SelectionChanged += handler,
                    handler => PayrollGroupGrid.SelectionChanged -= handler)
                .Select(_ => (PayrollGroupGrid.SelectedItem as PayrollGroupRow)?.Employee)
                .WhereNotNull()
                .InvokeCommand(viewModel.Group.SelectBatchEmployeeCommand)
                .DisposeWith(d);

            // Re-highlights the selected employee's row once a rebuild settles.
            Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    handler => viewModel.Group.PayrollGroupRows.CollectionChanged += handler,
                    handler => viewModel.Group.PayrollGroupRows.CollectionChanged -= handler)
                .Subscribe(_ => DispatchRestorePayrollGroupSelection())
                .DisposeWith(d);

            // Covers the whole page until a run is started or loaded this session.
            this.OneWayBind(ViewModel, vm => vm.Group.HasBatchScope, v => v.EmptyStateOverlay.Visibility,
                hasBatch => hasBatch ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Run.NewPayrollRunCommand, v => v.OverlayNewPayrollRunButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Run.LoadPayrollGroupCommand, v => v.OverlayLoadPayrollGroupButton).DisposeWith(d);
        });
    }

    /// <summary>Every visit, not just the first: the selection can change on the other tabs
    /// while this page isn't showing, and a schedule/holiday/attendance edit made elsewhere may
    /// have left the loaded payroll stale. Awaited all the way through, so the database work it
    /// may start has finished before RestorePayrollGroupSelection -- dispatched, so only after
    /// this returns -- can reach the shared ScheduleDbContext through a selection change (see
    /// PayrollGroupViewModel.RecheckOnPageRevisitAsync).</summary>
    public async Task OnNavigatedToAsync()
    {
        await ViewModel!.RecheckOnPageRevisitAsync();
        DispatchRestorePayrollGroupSelection();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>
    /// Dispatches RestorePayrollGroupSelection with ExecutionContext flow suppressed. The rows
    /// change from inside PayrollGroupViewModel's own AttendanceBusyState.RunAsync action, so a
    /// plain BeginInvoke would capture that action's "nested call" flag in its ExecutionContext;
    /// running at ContextIdle, well after the action finished, anything the restore triggered
    /// that called RunAsync would then wrongly ride along unserialized, racing whatever else is
    /// using the shared ScheduleDbContext. Suppressing flow starts it clean. Scoped to this one
    /// call site rather than AttendanceBusyState.RunAction's own, broader suppression, which
    /// deadlocked -- see that method's doc comment. SuppressFlow throws if flow is already
    /// suppressed, hence the check.
    /// </summary>
    private void DispatchRestorePayrollGroupSelection()
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            Dispatcher.BeginInvoke(RestorePayrollGroupSelection, DispatcherPriority.ContextIdle);
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            Dispatcher.BeginInvoke(RestorePayrollGroupSelection, DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>Best-effort re-highlight of the selected employee's row, scrolled into view --
    /// on every visit and after every rebuild of the rows. With the selected employee not in
    /// the group, the grid stays unselected.</summary>
    private void RestorePayrollGroupSelection()
    {
        if (ViewModel?.Summary.SelectedEmployee is not { } employee) return;
        if (ViewModel.Group.PayrollGroupRows.FirstOrDefault(r => r.Employee.Id == employee.Id) is not { } row) return;

        PayrollGroupGrid.SelectedItem = row;

        var rowIndex = PayrollGroupGrid.ResolveToRowIndex(row);
        if (rowIndex >= 0)
            PayrollGroupGrid.ScrollInView(new RowColumnIndex(rowIndex, 0));
    }
}
