using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.Grid.Helpers;
using Syncfusion.UI.Xaml.ScrollAxis;
using Syncfusion.UI.Xaml.TreeView;

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
/// a department here sets SelectedDepartment, selecting a row in the grid sets
/// SelectedEmployee, same as SchedulePage's tree does for its own tree. No load call
/// of its own -- by the time this page can be navigated to, SchedulePage (the app's
/// default page, see MainWindow.xaml.cs) has already populated Departments.
///
/// SfTreeView and SfDataGrid raise SelectionChanged only for the user's own clicks, not
/// when this code sets SelectedItem, so every place below that selects something in
/// code also tells the ViewModel itself (ShowDepartment/ShowEmployee).</summary>
public partial class EmployeesPage : Page, INavigationAware
{
    private readonly MainViewModel _viewModel;
    private readonly IStatusBarService _statusBarService;

    public EmployeesPage(MainViewModel viewModel, IStatusBarService statusBarService)
    {
        _viewModel = viewModel;
        _statusBarService = statusBarService;
        DataContext = viewModel;
        InitializeComponent();

        // Delete/Blacklist ask first; the questions are the tree ViewModel's.
        MessageBoxInteractions.Register(viewModel.Tree, this);

        EmployeeSearchBox.Filter = EmployeeSuggestionFilter;

        // Departments gets torn down and rebuilt wholesale on every load (see
        // EmployeeTreeViewModel.LoadAsync) -- including the reloads that Add/Edit/Delete
        // Department/Employee trigger from this page's own toolbar -- which drops whatever
        // was selected along with the old DepartmentGroupViewModel instance it belonged to.
        // Without re-selecting afterward, EmployeesGrid (which reads off
        // DepartmentTree.SelectedItem) would just go blank every time one of those buttons
        // was used. The search box's suggestions are rebuilt from the new roster too.
        _viewModel.Departments.CollectionChanged += (_, _) =>
        {
            RebuildSuggestions();
            DispatchRestoreTreeSelection();
        };
    }

    // Unlike SchedulePage's once-per-run restore, this runs on every visit --
    // MainViewModel.SelectedEmployee/SelectedDepartment can change from the
    // Schedule tab while this page isn't visible, and the tree/grid here should
    // pick up whatever's currently selected each time this page is shown.
    // Dispatched rather than called inline so DepartmentTree's nodes have actually been
    // generated from Departments by the time this runs.
    public Task OnNavigatedToAsync()
    {
        RebuildSuggestions();
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

    /// <summary>Best-effort only -- if the department/employee no longer exists, the tree
    /// just starts unselected. Prefers matching on SelectedEmployee (and the department it
    /// belongs to) over SelectedDepartment alone, so a specific employee stays highlighted
    /// rather than just the department row.
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

            var node = employeeToRestore is not null
                ? departmentGroup.Employees.FirstOrDefault(n => n.Employee.Id == employeeToRestore.Id)
                : null;

            SelectDepartmentAndEmployee(departmentGroup, node);
            return;
        }
    }

    /// <summary>Selects departmentGroup in DepartmentTree, then -- if employeeToSelect is
    /// given -- dispatches to select and scroll to that employee's row in EmployeesGrid
    /// too. Shared by RestoreTreeSelection (reselecting after a Departments reload) and
    /// the search box (jumping to a match): both need exactly this "select the department
    /// now, the employee once the grid has caught up" two-step. The dispatch is required,
    /// not just convenient -- EmployeesGrid.ItemsSource only catches up with the new
    /// DepartmentTree.SelectedItem once the binding engine and grid have had a turn, so
    /// selecting a row synchronously here would still be against the *previous*
    /// department's roster.</summary>
    private void SelectDepartmentAndEmployee(DepartmentGroupViewModel departmentGroup, EmployeeNodeViewModel? employeeToSelect)
    {
        DepartmentTree.SelectedItem = departmentGroup;
        ShowDepartment(departmentGroup);
        if (DepartmentTree.Nodes.FirstOrDefault(n => n.Content == departmentGroup) is { } treeNode)
            DepartmentTree.BringIntoView(treeNode);

        if (employeeToSelect is null) return;

        Dispatcher.BeginInvoke(() =>
        {
            EmployeesGrid.SelectedItem = employeeToSelect;
            ShowEmployee(employeeToSelect);

            var rowIndex = EmployeesGrid.ResolveToRowIndex(employeeToSelect);
            if (rowIndex >= 0)
                EmployeesGrid.ScrollInView(new RowColumnIndex(rowIndex, 0));
        }, DispatcherPriority.ContextIdle);
    }

    private void DepartmentTree_SelectionChanged(object? sender, ItemSelectionChangedEventArgs e)
    {
        if (DepartmentTree.SelectedItem is DepartmentGroupViewModel group)
            ShowDepartment(group);
    }

    private void EmployeesGrid_SelectionChanged(object? sender, GridSelectionChangedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is EmployeeNodeViewModel node)
            ShowEmployee(node);
        else
            _viewModel.SelectedEmployee = null;
    }

    /// <summary>A department picked in the tree: no employee selected in it yet.</summary>
    private void ShowDepartment(DepartmentGroupViewModel group)
    {
        _viewModel.SelectedDepartment = group.RealDepartment;
        _viewModel.SelectedEmployee = null;
    }

    private void ShowEmployee(EmployeeNodeViewModel node)
    {
        _viewModel.SelectedEmployee = node.Employee;
        _viewModel.SelectedDepartment = node.Employee.Department;
    }

    /// <summary>Double-clicking a row is a shortcut for selecting it and then clicking Edit.</summary>
    private void EmployeesGrid_CellDoubleTapped(object? sender, GridCellDoubleTappedEventArgs e)
    {
        if (e.Record is not EmployeeNodeViewModel) return;

        ICommand edit = _viewModel.EditEmployeeCommand;
        if (edit.CanExecute(null))
            edit.Execute(null);
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
        EmployeeSearchBox.AutoCompleteSource = _viewModel.Departments
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

    private void SearchEmployeeButton_Click(object sender, RoutedEventArgs e) => SearchEmployee();

    private void EmployeeSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        SearchEmployee();
        e.Handled = true;
    }

    /// <summary>Jumps to the first employee across every department matching the box's
    /// text -- selects (and scrolls to) their department in DepartmentTree and their row in
    /// EmployeesGrid, via the same SelectDepartmentAndEmployee two-step RestoreTreeSelection
    /// uses. Text that is exactly a picked suggestion goes straight to that person.
    /// Deliberately a one-shot "find and jump to it" rather than a live filter the way the
    /// Schedule/Attendance trees' own search boxes work (EmployeeTreeSearchFilter.Apply):
    /// see DepartmentTree's own comment in the XAML for why silently hiding a department
    /// here is exactly what this page was built to avoid. A miss leaves the tree/grid
    /// showing whatever they already were, not an empty state.
    ///
    /// Matching reuses EmployeeTreeSearchFilter.EmployeeMatchesSearchTerm -- the same
    /// exact-Pin-or-substring-on-name rule those other trees' search boxes use per
    /// term -- rather than a second copy of it. Unlike Apply, a blacklisted employee is
    /// not excluded here: this grid is deliberately the one place a blacklisted
    /// employee still shows, specifically so they can be found and unblacklisted later.
    ///
    /// Falls back to a department-name match (selecting just the department, no
    /// specific row) when no employee matches.</summary>
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

        foreach (var departmentGroup in _viewModel.Departments)
        {
            var node = departmentGroup.Employees.FirstOrDefault(
                n => EmployeeTreeSearchFilter.EmployeeMatchesSearchTerm(n.Employee, term));

            if (node is null) continue;

            SelectDepartmentAndEmployee(departmentGroup, node);
            return;
        }

        foreach (var departmentGroup in _viewModel.Departments)
        {
            if (!departmentGroup.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;

            SelectDepartmentAndEmployee(departmentGroup, null);
            return;
        }

        _statusBarService.ShowCaution($"No employee or department found matching \"{term}\".");
    }
}
