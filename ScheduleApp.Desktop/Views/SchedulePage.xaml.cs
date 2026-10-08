using System.Reactive.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.UI.Xaml.TreeView.Engine;

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

        // Remove Schedule/Holiday ask first, and Import Employees/Schedule can have a list of
        // problems to show; both go through the ViewModels' Confirm/Notify.
        MessageBoxInteractions.Register(viewModel.Assignment, this);
        MessageBoxInteractions.Register(viewModel.ImportExport, this);
    }

    // Loads on first navigation to this page rather than eagerly at app
    // startup - MainWindow calls this each time the Schedule item is
    // selected, but the schedule only needs to be pulled from the database
    // once per run.
    public async Task OnNavigatedToAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await _viewModel.LoadCommand.Execute();

        // The calendar's company-wide holiday markers aren't tied to an employee, so they
        // don't load as a side effect of LoadCommand the way the schedule does -- on a
        // fresh run with no employee restored, nothing would fetch them until one is
        // picked. Load them explicitly so they (and the "Remove Holiday" context-menu
        // item) show right away. Cheap -- a handful of rows, once per run.
        await _viewModel.RefreshCalendarHolidaysAsync();

        // LoadCommand may have just restored SelectedEmployee/SelectedDepartment
        // from last launch (see MainViewModel.RestoreSelectionFromViewState) --
        // that alone already drives the calendar and header text correctly, but
        // the tree's own selection isn't bound to it, so the tree itself won't
        // visually highlight that node without this nudge. Dispatched rather than
        // called inline so the tree's nodes have actually been generated from
        // VisibleDepartments by the time this runs.
        _ = Dispatcher.BeginInvoke(TryHighlightRestoredSelection, DispatcherPriority.ContextIdle);
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Best-effort only -- if the node isn't there (hidden by the search box, say),
    /// the tree just starts unhighlighted. The calendar's already showing the right
    /// employee/month regardless (see OnNavigatedToAsync above), so a missed highlight is
    /// a minor cosmetic gap, not a functional one. Selecting in code doesn't raise
    /// SelectionChanged (SfTreeView raises it for clicks only), so this doesn't feed back
    /// into the ViewModel either.</summary>
    private void TryHighlightRestoredSelection()
    {
        foreach (var departmentGroup in _viewModel.VisibleDepartments)
        {
            if (_viewModel.SelectedEmployee is { } employee)
            {
                var employeeNode = departmentGroup.VisibleEmployees.FirstOrDefault(n => n.Employee.Id == employee.Id);
                if (employeeNode is null) continue;

                departmentGroup.IsExpanded = true;
                EmployeeTree.SelectedItem = employeeNode;
                BringIntoView(employeeNode);
                return;
            }

            if (_viewModel.SelectedDepartment is { } department && departmentGroup.RealDepartment?.Id == department.Id)
            {
                EmployeeTree.SelectedItem = departmentGroup;
                BringIntoView(departmentGroup);
                return;
            }
        }
    }

    private void BringIntoView(object content)
    {
        if (FindNode(EmployeeTree.Nodes, content) is { } node)
            EmployeeTree.BringIntoView(node);
    }

    private static TreeViewNode? FindNode(IEnumerable<TreeViewNode> nodes, object content)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.Content, content)) return node;
            if (FindNode(node.ChildNodes, content) is { } child) return child;
        }

        return null;
    }

    private void EmployeeTree_SelectionChanged(object? sender, ItemSelectionChangedEventArgs e)
    {
        switch (EmployeeTree.SelectedItem)
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