using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using Syncfusion.UI.Xaml.ScrollAxis;
using Syncfusion.UI.Xaml.TreeView;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>Department and employee management: a department tree, the selected department's
/// employee grid, and the commands that edit them -- over the same shared ViewModel and
/// selection as the Schedule tab (<see cref="MainViewModel"/>), so picking someone here picks
/// them there. No load of its own: the Schedule page, the app's default, has already loaded
/// the tree by the time this one can be opened.
///
/// SfTreeView and SfDataGrid raise SelectionChanged only for clicks, not when this code sets
/// SelectedItem, so every place below that selects something in code also tells the
/// ViewModel itself.</summary>
public partial class EmployeesPage : INavigationAware
{
    private readonly IStatusBarService _statusBarService;

    public EmployeesPage(MainViewModel viewModel, IStatusBarService statusBarService)
    {
        ViewModel = viewModel;
        _statusBarService = statusBarService;
        InitializeComponent();

        EmployeeSearchBox.Filter = EmployeeSuggestionFilter;

        this.WhenActivated((MultipleDisposable d) =>
        {
            var tree = viewModel.Tree;

            // Delete/Blacklist ask first, and Import can have problems to list.
            ViewInteractions.Register(tree, this).DisposeWith(d);
            ViewInteractions.Register(viewModel.ImportExport, this).DisposeWith(d);

            DepartmentTree.ItemsSource = tree.Departments;

            this.BindCommand(ViewModel, vm => vm.Tree.AddDepartmentCommand, v => v.AddDepartmentButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.DeleteDepartmentCommand, v => v.DeleteDepartmentButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.AddEmployeeCommand, v => v.AddEmployeeButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.EditEmployeeCommand, v => v.EditEmployeeButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.DeleteEmployeeCommand, v => v.DeleteEmployeeButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.BlacklistEmployeeCommand, v => v.BlacklistButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Tree.UnblacklistEmployeeCommand, v => v.UnblacklistButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ImportExport.ImportEmployeesCommand, v => v.ImportEmployeesButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ImportExport.ExportEmployeesCommand, v => v.ExportEmployeesButton).DisposeWith(d);

            // A department picked: none of its employees yet.
            Observable.FromEventPattern<EventHandler<ItemSelectionChangedEventArgs>, ItemSelectionChangedEventArgs>(
                    handler => DepartmentTree.SelectionChanged += handler,
                    handler => DepartmentTree.SelectionChanged -= handler)
                .Select(_ => DepartmentTree.SelectedItem)
                .Where(item => item is DepartmentGroupViewModel)
                .InvokeCommand(tree.SelectNodeCommand)
                .DisposeWith(d);

            Observable.FromEventPattern<EventHandler<GridSelectionChangedEventArgs>, GridSelectionChangedEventArgs>(
                    handler => EmployeesGrid.SelectionChanged += handler,
                    handler => EmployeesGrid.SelectionChanged -= handler)
                .Select(_ => EmployeesGrid.SelectedItem)
                .InvokeCommand(tree.SelectNodeCommand)
                .DisposeWith(d);

            // Double-clicking a row is a shortcut for selecting it and clicking Edit.
            Observable.FromEventPattern<EventHandler<GridCellDoubleTappedEventArgs>, GridCellDoubleTappedEventArgs>(
                    handler => EmployeesGrid.CellDoubleTapped += handler,
                    handler => EmployeesGrid.CellDoubleTapped -= handler)
                .Where(e => e.EventArgs.Record is EmployeeNodeViewModel)
                .Select(_ => RxVoid.Default)
                .InvokeCommand(tree.EditEmployeeCommand)
                .DisposeWith(d);

            Observable.Merge(
                    Observable.FromEventPattern<RoutedEventHandler, RoutedEventArgs>(
                            handler => SearchButton.Click += handler,
                            handler => SearchButton.Click -= handler)
                        .Select(_ => Unit.Default),
                    Observable.FromEventPattern<KeyEventHandler, KeyEventArgs>(
                            handler => EmployeeSearchBox.KeyDown += handler,
                            handler => EmployeeSearchBox.KeyDown -= handler)
                        .Where(e => e.EventArgs.Key == Key.Enter)
                        .Do(e => e.EventArgs.Handled = true)
                        .Select(_ => Unit.Default))
                .Subscribe(_ => SearchEmployee())
                .DisposeWith(d);

            // Every load replaces the departments wholesale -- including the reloads this
            // page's own buttons trigger -- dropping the selected one along with its old
            // instance; without reselecting, the grid (which reads the tree's selection) would
            // go blank after every edit. The search suggestions are rebuilt from the new roster.
            Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    handler => tree.Departments.CollectionChanged += handler,
                    handler => tree.Departments.CollectionChanged -= handler)
                .Subscribe(_ =>
                {
                    RebuildSuggestions();
                    DispatchRestoreTreeSelection();
                })
                .DisposeWith(d);
        });
    }

    /// <summary>Every visit picks up whatever's selected now -- the Schedule tab may have
    /// changed it meanwhile.</summary>
    public Task OnNavigatedToAsync()
    {
        RebuildSuggestions();
        DispatchRestoreTreeSelection();
        return Task.CompletedTask;
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>Restores the selection once the tree has generated its nodes, with
    /// ExecutionContext flow suppressed for just this BeginInvoke. A reload replaces the
    /// departments from inside the busy state's RunAsync, whose AsyncLocal "nested call" flag
    /// this callback would otherwise capture as true -- and the selection it sets, which
    /// starts a calendar reload under the busy state, would then ride along unserialized
    /// instead of queuing. Same fix as PayrollPage.DispatchRestorePayrollGroupSelection.</summary>
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

    /// <summary>Best effort -- a department or employee that's gone leaves the tree
    /// unselected. Prefers the employee (and their department) over the department alone, so a
    /// specific person stays highlighted. A selected "(Unassigned)" bucket has no department to
    /// restore and reads like nothing selected -- it's left alone rather than guessed
    /// at.</summary>
    private void RestoreTreeSelection()
    {
        var tree = ViewModel!.Tree;
        var employeeToRestore = tree.SelectedEmployee;
        var departmentToRestore = tree.SelectedDepartment;

        if (employeeToRestore is null && departmentToRestore is null) return;

        foreach (var departmentGroup in tree.Departments)
        {
            var node = employeeToRestore is not null
                ? departmentGroup.Employees.FirstOrDefault(n => n.Employee.Id == employeeToRestore.Id)
                : null;
            var isMatch = employeeToRestore is not null
                ? node is not null
                : departmentGroup.RealDepartment?.Id == departmentToRestore!.Id;

            if (!isMatch) continue;

            SelectDepartmentAndEmployee(departmentGroup, node);
            return;
        }
    }

    /// <summary>Selects the department in the tree now and the employee's row once the grid
    /// has caught up -- its ItemsSource follows the tree's selection only after the binding
    /// engine has had a turn, so selecting a row right away would be against the previous
    /// department's roster.</summary>
    private void SelectDepartmentAndEmployee(DepartmentGroupViewModel departmentGroup, EmployeeNodeViewModel? employeeToSelect)
    {
        var tree = ViewModel!.Tree;
        DepartmentTree.SelectedItem = departmentGroup;
        tree.SelectNode(departmentGroup);
        if (DepartmentTree.Nodes.FirstOrDefault(n => n.Content == departmentGroup) is { } treeNode)
            DepartmentTree.BringIntoView(treeNode);

        if (employeeToSelect is null) return;

        Dispatcher.BeginInvoke(() =>
        {
            EmployeesGrid.SelectedItem = employeeToSelect;
            tree.SelectNode(employeeToSelect);

            var rowIndex = EmployeesGrid.ResolveToRowIndex(employeeToSelect);
            if (rowIndex >= 0)
                EmployeesGrid.ScrollInView(new RowColumnIndex(rowIndex, 0));
        }, DispatcherPriority.ContextIdle);
    }

    // ---- Search ----

    /// <summary>One suggestion in the search box -- an employee as "LastName, FirstName
    /// (PIN)" (Display, the box's SearchItemPath). Department/Node ride along so a pick
    /// jumps straight to that exact person without a second text match.</summary>
    private sealed record EmployeeSuggestion(string Display, DepartmentGroupViewModel Department, EmployeeNodeViewModel Node)
    {
        public override string ToString() => Display;
    }

    /// <summary>Every employee, blacklisted included (see SearchEmployee), alphabetized by
    /// "LastName, FirstName (PIN)"; the box shows the first 8 matches.</summary>
    private void RebuildSuggestions() =>
        EmployeeSearchBox.AutoCompleteSource = ViewModel!.Tree.Departments
            .SelectMany(department => department.Employees.Select(node => new EmployeeSuggestion(
                $"{node.Employee.LastName}, {node.Employee.FirstName} ({node.Employee.Pin})", department, node)))
            .OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>An employee whose PIN (substring), first name, or last name contains what's
    /// typed. Substring PIN matching -- unlike SearchEmployee's Enter-key path, which
    /// matches a numeric term exactly -- so typing "34" still surfaces "342".</summary>
    private static bool EmployeeSuggestionFilter(string search, object item)
    {
        if (item is not EmployeeSuggestion { Node.Employee: var employee }) return false;

        var term = search.Trim();
        return term.Length > 0 && (
            employee.Pin.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
            || employee.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || employee.LastName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Picking a suggestion jumps straight to that employee; the box keeps their
    /// "LastName, FirstName (PIN)" text, so it shows what was chosen.</summary>
    private void EmployeeSearchBox_SelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is EmployeeSuggestion suggestion)
            SelectDepartmentAndEmployee(suggestion.Department, suggestion.Node);
    }

    /// <summary>
    /// Jumps to the first employee matching the box's text -- their department in the tree,
    /// their row in the grid -- or, with no employee matching, the first department whose name
    /// does. Text that is exactly a picked suggestion goes straight to that person. A
    /// one-shot find, not a live filter like the Schedule/Attendance trees' search boxes:
    /// silently hiding a department is exactly what this page avoids. A miss leaves the tree
    /// and grid as they were.
    ///
    /// Matching is EmployeeTreeSearchFilter.EmployeeMatchesSearchTerm, the per-term rule those
    /// trees use -- except that a blacklisted employee is found too: this grid is the one place
    /// they still show, so they can be unblacklisted.
    /// </summary>
    private void SearchEmployee()
    {
        var term = EmployeeSearchBox.Text.Trim();
        if (term.Length == 0)
        {
            _statusBarService.ShowCaution("Type a PIN, name, or department to search for.");
            return;
        }

        if (EmployeeSearchBox.SelectedItem is EmployeeSuggestion picked && picked.Display == term)
        {
            SelectDepartmentAndEmployee(picked.Department, picked.Node);
            return;
        }

        var departments = ViewModel!.Tree.Departments;
        foreach (var departmentGroup in departments)
        {
            var node = departmentGroup.Employees.FirstOrDefault(
                n => EmployeeTreeSearchFilter.EmployeeMatchesSearchTerm(n.Employee, term));

            if (node is null) continue;

            SelectDepartmentAndEmployee(departmentGroup, node);
            return;
        }

        if (departments.FirstOrDefault(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) is { } department)
        {
            SelectDepartmentAndEmployee(department, null);
            return;
        }

        _statusBarService.ShowCaution($"No employee or department found matching \"{term}\".");
    }
}
