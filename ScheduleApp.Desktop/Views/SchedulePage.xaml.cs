using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScheduleApp.Desktop.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ScheduleApp.Desktop.Views;

public partial class SchedulePage : Page, INavigationAware
{
    private readonly MainViewModel _viewModel;
    private bool _loaded;

    public SchedulePage(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // Loads on first navigation to this page rather than eagerly at app
    // startup - NavigationView calls this each time the Schedule item is
    // selected, but the schedule only needs to be pulled from the database
    // once per run.
    public async Task OnNavigatedToAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await _viewModel.LoadCommand.ExecuteAsync(null);

        // The calendar's company-wide holiday markers aren't tied to an employee, so they
        // don't load as a side effect of LoadCommand the way the schedule does -- on a
        // fresh run with no employee restored, nothing would fetch them until one is
        // picked. Load them explicitly so they (and the "Remove Holiday" context-menu
        // item) show right away. Cheap -- a handful of rows, once per run.
        await _viewModel.RefreshCalendarHolidaysAsync();

        // LoadCommand may have just restored SelectedEmployee/SelectedDepartment
        // from last launch (see MainViewModel.RestoreSelectionFromViewState) --
        // that alone already drives the calendar and header text correctly, but
        // TreeView.SelectedItem has no built-in two-way binding, so the tree
        // itself won't visually highlight that node without this nudge.
        // Dispatched rather than called inline so the tree's containers (one
        // per department/employee row) have actually been generated from
        // Departments by the time this runs.
        _ = Dispatcher.BeginInvoke(TryHighlightRestoredSelection, DispatcherPriority.ContextIdle);
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Best-effort only -- if a container isn't ready for some reason,
    /// the tree just starts unhighlighted. The calendar's already showing the
    /// right employee/month regardless (see OnNavigatedToAsync above), so a
    /// missed highlight is a minor cosmetic gap, not a functional one.</summary>
    private void TryHighlightRestoredSelection()
    {
        foreach (var departmentGroup in _viewModel.Departments)
        {
            if (EmployeeTree.ItemContainerGenerator.ContainerFromItem(departmentGroup) is not TreeViewItem departmentContainer)
                continue;

            if (_viewModel.SelectedEmployee is { } employee)
            {
                var employeeNode = departmentGroup.Employees.FirstOrDefault(n => n.Employee.Id == employee.Id);
                if (employeeNode is null) continue;

                departmentContainer.IsExpanded = true;
                departmentContainer.UpdateLayout();

                if (departmentContainer.ItemContainerGenerator.ContainerFromItem(employeeNode) is TreeViewItem employeeContainer)
                {
                    employeeContainer.IsSelected = true;
                    employeeContainer.BringIntoView();
                    return;
                }
            }
            else if (_viewModel.SelectedDepartment is { } department && departmentGroup.RealDepartment?.Id == department.Id)
            {
                departmentContainer.IsSelected = true;
                departmentContainer.BringIntoView();
                return;
            }
        }
    }

    private void EmployeeTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case EmployeeNodeViewModel node:
                _viewModel.SelectedEmployee = node.Employee;
                _viewModel.SelectedDepartment = node.Employee.Department;
                break;

            case DepartmentGroupViewModel group:
                _viewModel.SelectedDepartment = group.RealDepartment;
                _viewModel.SelectedEmployee = null;
                break;
        }
    }
}