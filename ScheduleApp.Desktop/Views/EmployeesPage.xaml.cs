using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScheduleApp.Desktop.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ScheduleApp.Desktop.Views;

/// <summary>Hosts the Departments/Employees command card plus its own department
/// tree and employee grid. Deliberately takes the same MainViewModel SchedulePage
/// does (both are Scoped -- see App.xaml.cs's registration comment -- so DI hands
/// back the exact same instance) rather than a ViewModel of its own: the commands
/// here (Add/Delete Department, Add/Edit/Delete/Import Employee) and the
/// SelectedEmployee/SelectedDepartment they act on already live on MainViewModel,
/// and splitting that state across two ViewModels would just need two-way syncing
/// for no benefit.
///
/// DepartmentTree and EmployeesGrid are a second, independent view over the same
/// MainViewModel.Departments collection the Schedule tab's tree reads -- selecting
/// a department here sets SelectedDepartment (see DepartmentTree_OnSelectedItemChanged),
/// selecting a row in the grid sets SelectedEmployee (see
/// EmployeesGrid_OnSelectionChanged), same as SchedulePage's tree does for its own
/// TreeView. No load call of its own -- by the time this page can be navigated to,
/// SchedulePage (the app's default page, see MainWindow.xaml.cs) has already
/// populated Departments.</summary>
public partial class EmployeesPage : Page, INavigationAware
{
    private readonly MainViewModel _viewModel;

    public EmployeesPage(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Departments gets torn down and rebuilt wholesale on every LoadAsync (see
        // MainViewModel.LoadAsync) -- including the reloads that Add/Edit/Delete
        // Department/Employee trigger from this page's own toolbar -- which drops
        // whatever TreeViewItem was selected along with the old
        // DepartmentGroupViewModel instance it belonged to. Without re-selecting
        // afterward, EmployeesGrid (which reads off DepartmentTree.SelectedItem)
        // would just go blank every time one of those buttons was used.
        _viewModel.Departments.CollectionChanged += (_, _) => DispatchRestoreTreeSelection();
    }

    // Unlike SchedulePage's once-per-run restore, this runs on every visit --
    // MainViewModel.SelectedEmployee/SelectedDepartment can change from the
    // Schedule tab while this page isn't visible, and the tree/grid here should
    // pick up whatever's currently selected each time this page is shown.
    // Dispatched rather than called inline so DepartmentTree's containers (one per
    // department row) have actually been generated from Departments by the time
    // this runs.
    public Task OnNavigatedToAsync()
    {
        DispatchRestoreTreeSelection();
        return Task.CompletedTask;
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Dispatches RestoreTreeSelection with ExecutionContext flow suppressed for
    /// just this one BeginInvoke call -- same fix, same reasoning, and same history as
    /// PayrollPage.DispatchRestorePayrollGroupSelection (see that method's own doc comment
    /// for the full mechanism and why this is scoped here rather than reintroduced in
    /// AttendanceBusyState.RunAction). The dangerous call site is the
    /// Departments.CollectionChanged subscription above: it fires synchronously from inside
    /// EmployeeTreeViewModel's own AttendanceBusyState.RunAsync action (Departments.Clear()/
    /// Add() there triggers it), so without suppression this BeginInvoke would capture
    /// _isNestedCall.Value == true, and RestoreTreeSelection's own SelectedEmployee/
    /// SelectedDepartment assignments -- reached once this deferred callback actually runs,
    /// well after that action has finished -- would let whatever AttendanceBusyState.RunAsync
    /// call they trigger downstream (e.g. ScheduleCalendarViewModel's own SelectedEmployee
    /// subscription) wrongly ride along unserialized instead of correctly queuing on
    /// _gate.</summary>
    private void DispatchRestoreTreeSelection()
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            Dispatcher.BeginInvoke(RestoreTreeSelection, DispatcherPriority.ContextIdle);
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            Dispatcher.BeginInvoke(RestoreTreeSelection, DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>Best-effort only -- if a container isn't ready, or the department/
    /// employee no longer exists, the tree just starts unselected. Prefers matching
    /// on SelectedEmployee (and the department it belongs to) over SelectedDepartment
    /// alone, so a specific employee stays highlighted rather than just the
    /// department row.
    ///
    /// One known gap: MainViewModel.SelectedDepartment is null both when nothing's
    /// selected and when the "(Unassigned)" bucket is selected (it has no
    /// RealDepartment) -- that ambiguity is pre-existing, not introduced here, and
    /// this treats both as "nothing to restore" rather than guessing wrong.</summary>
    private void RestoreTreeSelection()
    {
        var employeeToRestore = _viewModel.SelectedEmployee;
        var departmentToRestore = _viewModel.SelectedDepartment;

        if (employeeToRestore is null && departmentToRestore is null) return;

        foreach (var departmentGroup in _viewModel.Departments)
        {
            bool isMatch = employeeToRestore is not null
                ? departmentGroup.Employees.Any(n => n.Employee.Id == employeeToRestore.Id)
                : departmentGroup.RealDepartment?.Id == departmentToRestore!.Id;

            if (!isMatch) continue;

            if (DepartmentTree.ItemContainerGenerator.ContainerFromItem(departmentGroup) is TreeViewItem container)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }

            if (employeeToRestore is not null)
            {
                // EmployeesGrid.ItemsSource only catches up with the new
                // DepartmentTree.SelectedItem once the binding engine and grid have
                // had a turn -- dispatch again (same ContextIdle trick as above) so
                // EmployeesGrid.Items actually contains this department's roster
                // before picking a row out of it.
                var employeeId = employeeToRestore.Id;
                Dispatcher.BeginInvoke(() =>
                {
                    var node = departmentGroup.Employees.FirstOrDefault(n => n.Employee.Id == employeeId);
                    if (node is not null) EmployeesGrid.SelectedItem = node;
                }, DispatcherPriority.ContextIdle);
            }

            return;
        }
    }

    private void DepartmentTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DepartmentGroupViewModel group)
        {
            _viewModel.SelectedDepartment = group.RealDepartment;
            _viewModel.SelectedEmployee = null;
        }
    }

    private void EmployeesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is EmployeeNodeViewModel node)
        {
            _viewModel.SelectedEmployee = node.Employee;
            _viewModel.SelectedDepartment = node.Employee.Department;
        }
        else
        {
            _viewModel.SelectedEmployee = null;
        }
    }

    /// <summary>Double-clicking a row is a shortcut for selecting it and then
    /// clicking "Edit Employee" above.</summary>
    private void EmployeesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is EmployeeNodeViewModel && _viewModel.EditEmployeeCommand.CanExecute(null))
            _viewModel.EditEmployeeCommand.Execute(null);
    }
}