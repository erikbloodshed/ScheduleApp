using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScheduleApp.Desktop.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ScheduleApp.Desktop.Views;

/// <summary>Hosts the Payroll Group table (left) plus PayrollSummaryView/EmployeeAttendancePanel
/// (right, top and bottom -- see PayrollPage.xaml). Takes the same shared, Scoped
/// MainViewModel SchedulePage/EmployeesPage get (set as this page's own DataContext) rather
/// than a selection state of its own -- see EmployeesPage's own doc comment for the same
/// reasoning; the left column itself now reads PayrollViewModel.PayrollGroupRows (via
/// PayrollGroupRowsView, its own live search-filtered view -- see that property's doc
/// comment) rather than MainViewModel.Departments (see PayrollGroupGrid_OnSelectionChanged/
/// RestorePayrollGroupSelection below), and drives MainViewModel.SelectedEmployee the same
/// way a tree click on Schedule/Employees would have. No load call of its own for the same
/// reason EmployeesPage has none: by the time this page can be navigated to, SchedulePage
/// (the app's default page) has already populated Departments.
///
/// PayrollViewModel is a second, separate Scoped instance carrying only payroll-specific
/// state (selected period, and the itemized breakdown/adjustment list); it
/// reads MainViewModel.SelectedEmployee itself (see PayrollViewModel's constructor) rather
/// than this page having to forward it, but still needs to be set explicitly as
/// PayrollHeaderCard/SummaryViewControl/AttendancePanelControl/EmptyStateOverlay's
/// DataContext below, since this page's own DataContext is MainViewModel, not
/// PayrollViewModel. PayrollHeaderCard is
/// this page's own copy of the page-level command card SchedulePage/EmployeesPage/
/// AttendanceView each have (see PayrollPage.xaml) -- Period pickers plus Print Current
/// Payslip/Print Payslips…, both PayrollViewModel-bound, which is why it needs the same
/// explicit DataContext the other two controls do.</summary>
public partial class PayrollPage : Page, INavigationAware
{
    private readonly MainViewModel _viewModel;

    /// <summary>Kept as a field (unlike PayrollHeaderCard/SummaryViewControl/
    /// AttendancePanelControl, which only ever need PayrollViewModel as a DataContext
    /// assignment) so PayrollGroupGrid_OnSelectionChanged below has something to call
    /// SelectBatchEmployeeCommand on -- a DataGridRow isn't an ICommandSource the way
    /// Button is, so that command has to be invoked from code-behind rather than a
    /// Command/CommandParameter binding in XAML (see PayrollPage.xaml's own comment on the
    /// Payroll Group DataGrid).</summary>
    private readonly PayrollViewModel _payrollViewModel;

    public PayrollPage(MainViewModel viewModel, PayrollViewModel payrollViewModel)
    {
        _viewModel = viewModel;
        _payrollViewModel = payrollViewModel;
        DataContext = viewModel;
        InitializeComponent();

        PayrollHeaderCard.DataContext = payrollViewModel;
        SummaryViewControl.DataContext = payrollViewModel;
        AttendancePanelControl.DataContext = payrollViewModel;
        PayrollGroupPanel.DataContext = payrollViewModel;
        EmptyStateOverlay.DataContext = payrollViewModel;

        // Re-sync the DataGrid's highlighted row after PayrollGroupRows is rebuilt
        // (period change, scope change) — same DispatcherPriority.ContextIdle pattern
        // SchedulePage/EmployeesPage use for their own tree-restore after Departments
        // rebuilds. PayrollGroupRows rebuilds are incremental (rows added one by one),
        // so the CollectionChanged fires multiple times per refresh; BeginInvoke with
        // ContextIdle coalesces those into a single re-sync pass once the batch settles.
        _payrollViewModel.PayrollGroupRows.CollectionChanged += (_, _) => DispatchRestorePayrollGroupSelection();
    }

    // Runs on every visit (not just the first), same as EmployeesPage.OnNavigatedToAsync --
    // MainViewModel.SelectedEmployee/SelectedDepartment can change from the Schedule/
    // Employees/Attendance tabs while this page isn't visible, and the Payroll Group table
    // here should pick up whatever's currently selected each time this page is shown.
    //
    // RecheckOnPageRevisitAsync() is the same idea for a schedule edit rather than a
    // selection change -- there's nowhere on this page itself to edit a schedule from, so
    // the only way a currently-loaded payroll group's numbers could be stale is a trip to
    // the Schedule page and back, which this is the hook for. Genuinely awaited here (this
    // method is async specifically for that reason) rather than fired unawaited the way
    // RestorePayrollGroupSelection's own BeginInvoke is -- see PayrollGroupViewModel.
    // RecheckOnPageRevisitAsync's own doc comment for the race that awaiting closes:
    // RestorePayrollGroupSelection is dispatched via BeginInvoke, so it can only run once
    // this method actually returns, and awaiting the recheck all the way through here is
    // what guarantees that doesn't happen until any resulting database work has genuinely
    // finished, rather than leaving that guarantee to depend on exactly how the shared
    // busy-state's own gating happens to interleave with a separately-queued dispatcher
    // callback.
    public async Task OnNavigatedToAsync()
    {
        await _payrollViewModel.RecheckOnPageRevisitAsync();
        DispatchRestorePayrollGroupSelection();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Forwards a DataGrid row click to MainViewModel.SelectedEmployee —
    /// same _mainViewModel.SelectedEmployee assignment the old
    /// EmployeeTree_OnSelectedItemChanged made from a tree click. Never nulls
    /// SelectedEmployee out (no "deselect" concept in the table, same as the old
    /// checklist never nulled it out either). The re-entrant call produced when
    /// PayrollGroupRows is rebuilt and RestorePayrollGroupSelection sets
    /// SelectedItem is harmless: AddedItems is empty in that call, so the
    /// pattern match below simply skips it.</summary>
    private void PayrollGroupGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is PayrollGroupRow row)
            _payrollViewModel.SelectBatchEmployeeCommand.Execute(row.Employee);
    }

    /// <summary>Dispatches RestorePayrollGroupSelection with ExecutionContext flow suppressed
    /// for just this one BeginInvoke call -- the scoped fix for a race first found and fixed
    /// (then, elsewhere, reverted -- see AttendanceBusyState.RunAsync's own doc comment for
    /// that full history) via AttendanceBusyState.RunAction's own, much broader suppression.
    /// This is the actual dangerous call site that motivated that fix: the
    /// PayrollGroupRows.CollectionChanged subscription above fires synchronously from inside
    /// PayrollGroupViewModel.RefreshPayrollGroupRowsAsync's own AttendanceBusyState.RunAsync
    /// action (PayrollGroupRows.Clear()/Add() there triggers it), so without suppression here,
    /// this BeginInvoke call captures _isNestedCall.Value == true as part of its own
    /// ExecutionContext. By the time this deferred callback actually runs -- ContextIdle
    /// priority, so well after that action has finished and _isNestedCall.Value is back to
    /// false on the real call chain -- RestorePayrollGroupSelection's own
    /// PayrollGroupGrid.SelectedItem assignment fires PayrollGroupGrid_OnSelectionChanged ->
    /// SelectBatchEmployeeCommand, and without this suppression that command's own
    /// AttendanceBusyState.RunAsync call would wrongly inherit the stale "nested" flag and
    /// ride along unserialized -- skipping _gate entirely -- racing whatever else is
    /// genuinely running against the shared, app-lifetime-scoped ScheduleDbContext at that
    /// moment. Suppressing flow here means this dispatch instead starts with a clean,
    /// not-nested ExecutionContext, so SelectBatchEmployeeCommand correctly queues on _gate
    /// like any other unrelated caller once its own turn comes.
    ///
    /// Scoped to just this one known call site rather than reintroducing
    /// AttendanceBusyState.RunAction's own suppression: that broader version also stripped
    /// _isNestedCall's flowed value from a RunAsync action's own ordinary internal awaits (not
    /// just from things it explicitly schedules), which produced a guaranteed, permanent,
    /// un-cancellable deadlock reachable through completely ordinary use -- see RunAction's
    /// own doc comment for the confirmed mechanism. This narrower fix only touches the one
    /// BeginInvoke call that's actually the source of the problem.
    ///
    /// IsFlowSuppressed()-guarded the same way RunAction's own version was -- SuppressFlow()
    /// throws InvalidOperationException if flow is already suppressed on this call chain.</summary>
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

    /// <summary>Best-effort re-sync of PayrollGroupGrid's highlighted row to
    /// MainViewModel.SelectedEmployee — called from OnNavigatedToAsync (so the
    /// right row is highlighted when the user returns to this tab) and from the
    /// PayrollGroupRows.CollectionChanged handler (so a period/scope rebuild
    /// doesn't leave the grid unselected while the same employee is still active).
    /// If no row matches (e.g. the selected employee is not in the current Payroll
    /// Group) the grid simply stays unselected.</summary>
    private void RestorePayrollGroupSelection()
    {
        if (_viewModel.SelectedEmployee is not { } employee) return;

        var row = _payrollViewModel.PayrollGroupRows
            .FirstOrDefault(r => r.Employee.Id == employee.Id);

        if (row is not null)
        {
            PayrollGroupGrid.SelectedItem = row;
            PayrollGroupGrid.ScrollIntoView(row);
        }
    }
}
