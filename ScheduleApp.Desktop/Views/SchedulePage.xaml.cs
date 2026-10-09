using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.UI.Xaml.TreeView.Engine;

namespace ScheduleApp.Desktop.Views;

/// <summary>The Schedule tab: the employee tree on the left, the selected employee's calendar
/// on the right -- see <see cref="MainViewModel"/>.</summary>
public partial class SchedulePage : INavigationAware
{
    public SchedulePage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        MonthCalendar.ViewModel = viewModel;

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Remove Schedule/Holiday ask first, the imports can have problems to list, and the
            // calendar's tiles open dialogs of their own.
            ViewInteractions.Register(viewModel.Assignment, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.ImportExport, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.ManualEntryEditor, this).DisposeWith(d);

            // Left: the tree.
            this.OneWayBind(ViewModel, vm => vm.MultiSelectButtonText, v => v.MultiSelectButton.Label).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ToggleMultiSelectModeCommand, v => v.MultiSelectButton).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Tree.SearchText, v => v.TreeSearchBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsMultiSelectMode, v => v.MultiSelectPanel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Tree.SelectedEmployeeCount, v => v.CheckedCountText.Text,
                count => $"{count} employee(s) checked").DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.ClearEmployeeSelectionCommand, v => v.ClearCheckedButton).DisposeWith(d);
            EmployeeTree.ItemsSource = viewModel.Tree.VisibleDepartments;

            // SfTreeView raises this for clicks only, never for SelectedItem set in code.
            Observable.FromEventPattern<EventHandler<ItemSelectionChangedEventArgs>, ItemSelectionChangedEventArgs>(
                    handler => EmployeeTree.SelectionChanged += handler,
                    handler => EmployeeTree.SelectionChanged -= handler)
                .Select(_ => EmployeeTree.SelectedItem)
                .Where(item => item is not null)
                .InvokeCommand(viewModel.Tree.SelectNodeCommand)
                .DisposeWith(d);

            // Right: the calendar.
            this.BindCommand(ViewModel, vm => vm.Calendar.PreviousMonthCommand, v => v.PreviousMonthButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Calendar.NextMonthCommand, v => v.NextMonthButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Calendar.DisplayedMonthText, v => v.DisplayedMonthText.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ImportExport.ImportScheduleCommand, v => v.ImportScheduleButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ImportExport.ExportScheduleCommand, v => v.ExportScheduleButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Calendar.RefreshOrCancelScheduleCommand, v => v.RefreshOrCancelButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Calendar.RefreshOrCancelGlyph, v => v.RefreshOrCancelButton.Tag).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Calendar.RefreshOrCancelToolTip, v => v.RefreshOrCancelButton.ToolTip).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CalendarHeaderText, v => v.CalendarHeaderText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Calendar.SetScheduleButtonText, v => v.SetScheduleButton.Label).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Assignment.SetScheduleForSelectionCommand, v => v.SetScheduleButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Assignment.ClearScheduleForSelectionCommand, v => v.ClearScheduleButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Calendar.ClearCalendarSelectionCommand, v => v.ClearCalendarSelectionButton).DisposeWith(d);
        });
    }

    /// <summary>Loads the first time the page is shown each run (later visits find it
    /// loaded), then highlights the employee or department restored from last time -- the
    /// calendar already follows the restored selection, but the tree's own highlight isn't
    /// bound to it.</summary>
    public async Task OnNavigatedToAsync()
    {
        await ViewModel!.OpenCommand.Execute();

        // Once the tree has generated its nodes from the freshly loaded departments.
        _ = Dispatcher.BeginInvoke(HighlightSelection, DispatcherPriority.ContextIdle);
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Best effort: a node the search box hides just stays unhighlighted. Selecting in
    /// code raises no SelectionChanged, so this doesn't feed back into the ViewModel.</summary>
    private void HighlightSelection()
    {
        var tree = ViewModel!.Tree;
        foreach (var departmentGroup in tree.VisibleDepartments)
        {
            if (tree.SelectedEmployee is { } employee)
            {
                var employeeNode = departmentGroup.VisibleEmployees.FirstOrDefault(n => n.Employee.Id == employee.Id);
                if (employeeNode is null) continue;

                departmentGroup.IsExpanded = true;
                EmployeeTree.SelectedItem = employeeNode;
                BringIntoView(employeeNode);
                return;
            }

            if (tree.SelectedDepartment is { } department && departmentGroup.RealDepartment?.Id == department.Id)
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

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
