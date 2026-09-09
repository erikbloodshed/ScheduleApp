using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ScheduleApp.Desktop.Services;
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
    private readonly IStatusBarService _statusBarService;

    public EmployeesPage(MainViewModel viewModel, IStatusBarService statusBarService)
    {
        _viewModel = viewModel;
        _statusBarService = statusBarService;
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

            var node = employeeToRestore is not null
                ? departmentGroup.Employees.FirstOrDefault(n => n.Employee.Id == employeeToRestore.Id)
                : null;

            SelectDepartmentAndEmployee(departmentGroup, node);
            return;
        }
    }

    /// <summary>Selects departmentGroup's own TreeViewItem, then -- if employeeToSelect
    /// is given -- dispatches to select and scroll to that employee's row in
    /// EmployeesGrid too. Shared by RestoreTreeSelection (reselecting after a
    /// Departments reload) and SearchEmployee (jumping to a search match): both need
    /// exactly this "select the department now, the employee once the grid has caught
    /// up" two-step, just triggered from different callers. The dispatch is required,
    /// not just convenient -- EmployeesGrid.ItemsSource only catches up with the new
    /// DepartmentTree.SelectedItem once the binding engine and grid have had a turn, so
    /// picking a row out of EmployeesGrid.Items synchronously here would still be
    /// reading the *previous* department's roster.</summary>
    private void SelectDepartmentAndEmployee(DepartmentGroupViewModel departmentGroup, EmployeeNodeViewModel? employeeToSelect)
    {
        if (DepartmentTree.ItemContainerGenerator.ContainerFromItem(departmentGroup) is TreeViewItem container)
        {
            container.IsSelected = true;
            container.BringIntoView();
        }

        if (employeeToSelect is null) return;

        Dispatcher.BeginInvoke(() =>
        {
            EmployeesGrid.SelectedItem = employeeToSelect;
            EmployeesGrid.ScrollIntoView(employeeToSelect);
        }, DispatcherPriority.ContextIdle);
    }

    private void SearchEmployeeButton_Click(object sender, RoutedEventArgs e)
    {
        EmployeeSuggestionsPopup.IsOpen = false;
        SearchEmployee();
    }

    private void EmployeeSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        EmployeeSuggestionsPopup.IsOpen = false;
        SearchEmployee();
        e.Handled = true;
    }

    /// <summary>Set true only while EmployeeSuggestionList_PreviewMouseLeftButtonUp writes
    /// the picked suggestion's text back into the box, so that write doesn't bounce
    /// through EmployeeSearchBox_TextChanged and re-open the dropdown that was just
    /// dismissed by the pick.</summary>
    private bool _suppressEmployeeSuggestions;

    private void EmployeeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEmployeeSuggestions) return;

        var term = EmployeeSearchBox.Text.Trim();
        if (term.Length == 0)
        {
            EmployeeSuggestionList.ItemsSource = null;
            EmployeeSuggestionsPopup.IsOpen = false;
            return;
        }

        var suggestions = BuildEmployeeSuggestions(term);
        EmployeeSuggestionList.ItemsSource = suggestions;
        EmployeeSuggestionsPopup.IsOpen = suggestions.Count > 0;
    }

    /// <summary>Picking a suggestion jumps straight to that employee: fills the box with
    /// their "LastName, FirstName (PIN)" text (so it shows what was chosen) and selects
    /// their department plus grid row via the same SelectDepartmentAndEmployee two-step
    /// SearchEmployee uses -- no second text match, since the pick already names the exact
    /// person. Same code-behind-finds-the-clicked-item shape as
    /// PunchRecordsView.xaml.cs's own suggestion handler, since ListBox has no built-in
    /// "item clicked" hook.</summary>
    private void EmployeeSuggestionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(EmployeeSuggestionList, source) is not ListBoxItem { Content: EmployeeSuggestion suggestion })
            return;

        EmployeeSuggestionsPopup.IsOpen = false;

        _suppressEmployeeSuggestions = true;
        EmployeeSearchBox.Text = suggestion.Display;
        _suppressEmployeeSuggestions = false;

        SelectDepartmentAndEmployee(suggestion.Department, suggestion.Node);
        e.Handled = true;
    }

    /// <summary>Up to 8 employees whose PIN (substring), first name, or last name contains
    /// term, across every department, alphabetized by "LastName, FirstName (PIN)".
    /// Substring PIN matching -- unlike SearchEmployee's Enter-key path, which matches a
    /// numeric term exactly -- so typing "34" still surfaces "342" in the dropdown.
    /// Blacklisted employees are included, same reasoning as SearchEmployee (see its doc
    /// comment).</summary>
    private List<EmployeeSuggestion> BuildEmployeeSuggestions(string term)
    {
        var matches = new List<EmployeeSuggestion>();

        foreach (var department in _viewModel.Departments)
        {
            foreach (var node in department.Employees)
            {
                var employee = node.Employee;

                bool isMatch =
                    employee.Pin.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
                    || employee.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || employee.LastName.Contains(term, StringComparison.OrdinalIgnoreCase);

                if (isMatch)
                    matches.Add(new EmployeeSuggestion($"{employee.LastName}, {employee.FirstName} ({employee.Pin})", department, node));
            }
        }

        return matches
            .OrderBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>One row in the employee search box's autosuggest dropdown -- an employee
    /// as "LastName, FirstName (PIN)". Department/Node ride along so a pick jumps straight
    /// to that exact person without a second text match. ToString() is what the ListBox
    /// renders (no DataTemplate/DisplayMemberPath on it), same as
    /// PunchSearchSuggestion.</summary>
    private sealed record EmployeeSuggestion(string Display, DepartmentGroupViewModel Department, EmployeeNodeViewModel Node)
    {
        public override string ToString() => Display;
    }

    /// <summary>Finds the first employee across every department matching the box's
    /// text and jumps to it -- selects (and scrolls to) their department in
    /// DepartmentTree and their row in EmployeesGrid, via the same
    /// SelectDepartmentAndEmployee two-step RestoreTreeSelection uses. Deliberately a
    /// one-shot "find and jump to it" rather than a live filter the way the Schedule/
    /// Attendance trees' own search boxes work (EmployeeTreeSearchFilter.Apply): see
    /// DepartmentTree's own doc comment in the XAML for why silently hiding a
    /// department here -- even just one caught by a stale term left in this box -- is
    /// exactly what this page's tree was built to avoid. A miss leaves the tree/grid
    /// showing whatever they already were, not an empty state.
    ///
    /// Matching reuses EmployeeTreeSearchFilter.EmployeeMatchesSearchTerm -- the same
    /// exact-Pin-or-substring-on-name rule those other trees' search boxes use per
    /// term -- rather than a second copy of it. Unlike Apply, a blacklisted employee is
    /// not excluded here: this grid is deliberately the one place a blacklisted
    /// employee still shows (see EmployeesGrid.RowStyle's own doc comment), specifically
    /// so they can be found and unblacklisted later -- excluding them from the one
    /// search feature built to find someone would defeat that.
    ///
    /// Falls back to a department-name match (selecting just the department, no
    /// specific row) when no employee matches -- the same fallback Apply gives a
    /// department-name term on the other trees, just without also selecting whichever
    /// employee happens to be first in it, since there's no single "the" match to jump
    /// to in that case.</summary>
    private void SearchEmployee()
    {
        var term = EmployeeSearchBox.Text.Trim();
        if (term.Length == 0)
        {
            _statusBarService.ShowCaution("Type a PIN, name, or department to search for.");
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