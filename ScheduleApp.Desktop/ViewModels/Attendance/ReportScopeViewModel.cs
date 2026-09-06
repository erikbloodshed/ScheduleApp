using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Backs the Attendance tab's report-scope Department/Employee checkbox tree.
/// Same tree shape as the Schedule tab (see EmployeeTreeBuilder), but a separate
/// instance/selection state -- checking boxes here scopes a report, it doesn't select
/// employees for a schedule assignment, so the two trees can't share selection even
/// though they can share how they're built. Checkboxes are always visible (there's no
/// single-employee calendar view on this tab for an unchecked tree to protect, unlike the
/// Schedule tab's multi-select toggle).
///
/// Every employee starts checked -- see LoadEmployeeTreeAsync -- since the common case is
/// a report for everyone, and starting from "all" makes that the zero-effort default
/// instead of something you have to opt into. Un-checking a department's checkbox
/// un-checks every employee in it (and un-checking any one employee flips the department
/// checkbox to its indeterminate state, same tri-state behavior as the Schedule tab -- see
/// DepartmentGroupViewModel). Un-checking individual employees narrows the report to
/// everyone still checked. GetSelectedPins turns whatever's checked into a report
/// request's TargetPins (or null for "everyone" when every loaded employee is still
/// checked) -- ReportViewModel calls it when building a run and listens for
/// SelectedEmployeeCount/TotalEmployeeCount changes to know when it can run.
/// SelectionScopeText describes the current scope for display.</summary>
public partial class ReportScopeViewModel : ObservableObject
{
    /// <summary>Replaces the raw IScheduleRepository this class used to read
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync through
    /// directly -- see ActiveRosterProvider's own doc comment for why this tree's load (one
    /// of five call sites that used to independently re-fetch the same Active-only pair) now
    /// goes through the shared, RosterVersion-gated cache instead.</summary>
    private readonly ActiveRosterProvider _rosterProvider;
    private readonly ViewStateStore _viewStateStore;

    public ReportScopeViewModel(ActiveRosterProvider rosterProvider, ViewStateStore viewStateStore)
    {
        _rosterProvider = rosterProvider;
        _viewStateStore = viewStateStore;
    }

    public ObservableCollection<DepartmentGroupViewModel> Departments { get; } = new();

    /// <summary>Set once LoadEmployeeTreeAsync has applied the saved report-scope
    /// selection for the first time this run -- guards against a later reload (the "↻
    /// Refresh" button also calls LoadEmployeeTreeAsync, and is documented to reset the
    /// tree back to everyone checked) silently reapplying last launch's narrowed scope
    /// instead of honoring that reset. Also gates AttendanceViewModel.SaveViewState the
    /// same way it did before the split into child ViewModels -- see that method's own
    /// comment for why.</summary>
    public bool HasRestoredScope { get; private set; }

    /// <summary>The flat employee list this tree was just built from -- i.e. exactly what
    /// GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync (the same
    /// pair AttendanceEmployeeDirectory.GetAllAsync calls) returned, flattened the same
    /// way. Exposed purely so AttendanceViewModel.InitializeAsync can hand this straight to
    /// AttendanceEmployeeDirectory.SeedCache instead of that class firing the identical pair
    /// of queries a second time on every Attendance-tab open -- see SeedCache's own doc
    /// comment. Empty until the first LoadEmployeeTreeAsync completes; replaced (not
    /// merged) on every subsequent reload, including via the "↻ Refresh" button.</summary>
    public IReadOnlyList<Employee> LoadedEmployees { get; private set; } = [];

    /// <summary>Bumped every time a checkbox in the tree actually flips (see
    /// OnEmployeeNodeSelectionChanged) -- i.e. every real change to what
    /// GetSelectedPins() would return, including the initial tree load/reload
    /// itself. ReportViewModel folds this into its own "has anything this tab cares
    /// about actually changed since the last successful run" check (see
    /// ReportViewModel.ShouldAutoReload) so that narrowing/widening the report scope and
    /// then switching back to the Summary tab picks up the new scope, the same way it
    /// did before that check existed -- without this, comparing only PeriodStart/
    /// PeriodEnd there would miss a scope-only change entirely.</summary>
    public int SelectionVersion { get; private set; }

    /// <summary>Free-text filter for the tree, comma-separated same as the Punch Records
    /// tab's search box (see PunchRecordsViewModel.LogViewSearchText/
    /// ResolveMatchingPins) -- each term is matched against Employee ID, first name,
    /// last name, or department name, and terms are OR'd together. Narrows which nodes
    /// are visible (EmployeeNodeViewModel.IsVisible, DepartmentGroupViewModel.IsVisible)
    /// rather than which are checked -- searching and scoping a report are independent,
    /// so typing here never changes what a report would actually include.</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    /// <summary>See EmployeeTreeSearchFilter -- shared with the Schedule tab's tree
    /// (MainViewModel) so the two don't carry duplicate copies of the same matching
    /// rule.</summary>
    private void ApplySearchFilter() => EmployeeTreeSearchFilter.Apply(Departments, SearchText);

    [RelayCommand]
    private async Task LoadEmployeeTreeAsync()
    {
        Departments.Clear();

        var (departments, unassigned) = await _rosterProvider.GetAsync();

        LoadedEmployees = [.. departments.SelectMany(d => d.Employees), .. unassigned];

        foreach (var group in EmployeeTreeBuilder.Build(departments, unassigned, OnEmployeeNodeSelectionChanged))
            Departments.Add(group);

        // Default every employee (and therefore every department) to checked,
        // representing the whole company -- the opposite of the Schedule tab's tree,
        // which intentionally starts empty since bulk-assigning a schedule to literally
        // everyone by default would be a footgun there.
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = true;

        // Then narrow back down to whatever was checked when a report was last generated
        // (see ViewStateStore's null/empty-means-everyone convention) -- but only the
        // first time this runs. A later reload via the "↻ Refresh" button intentionally
        // skips this and leaves everyone checked, matching what that button's own
        // tooltip already promises.
        if (!HasRestoredScope)
        {
            HasRestoredScope = true;

            if (_viewStateStore.Attendance.SelectedPins is { Count: > 0 } savedIds)
            {
                var savedSet = savedIds.ToHashSet();
                foreach (var node in Departments.SelectMany(d => d.Employees))
                    node.IsSelected = savedSet.Contains(node.Employee.Pin);
            }
        }

        // Any prior (re)load's selection is gone -- make sure the scope display and
        // buttons reflect the fresh, fully-checked (or just-restored) state.
        // ReportViewModel's own TryAutoRun re-evaluates itself off SelectedEmployeeCount
        // changing (see its constructor), so there's no need to reach into it directly
        // here the way the pre-split code once did.
        OnPropertyChanged(nameof(SelectedEmployeeCount));
        OnPropertyChanged(nameof(SelectionScopeText));
        SelectAllTreeCommand.NotifyCanExecuteChanged();
        ClearTreeSelectionCommand.NotifyCanExecuteChanged();

        // Freshly-built nodes all default to IsVisible = true, so a reload (via "↻
        // Refresh") under an already-typed search term needs this to re-hide whatever
        // still doesn't match, rather than briefly showing everyone until the next
        // keystroke.
        ApplySearchFilter();
    }

    private void OnEmployeeNodeSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EmployeeNodeViewModel.IsSelected)) return;

        SelectionVersion++;
        OnPropertyChanged(nameof(SelectedEmployeeCount));
        OnPropertyChanged(nameof(SelectionScopeText));
        SelectAllTreeCommand.NotifyCanExecuteChanged();
        ClearTreeSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Employees currently checked in the report-scope tree, across every
    /// department (including Unassigned).</summary>
    public int SelectedEmployeeCount => Departments.SelectMany(d => d.Employees).Count(n => n.IsSelected);

    /// <summary>Every employee currently loaded into the tree, across every department
    /// (including Unassigned) -- i.e. what SelectedEmployeeCount equals when everything
    /// is checked.</summary>
    public int TotalEmployeeCount => Departments.Sum(d => d.Employees.Count);

    /// <summary>Plain-language description of the current report scope, shown under the
    /// tree: "Whole company" when every loaded employee is checked (the default), the
    /// department name(s) when one or more departments are checked in full and nothing
    /// else is, "Nothing selected" when the tree's been cleared, or a plain headcount
    /// otherwise.</summary>
    public string SelectionScopeText
    {
        get
        {
            int total = SelectedEmployeeCount;
            int all = TotalEmployeeCount;

            if (all == 0) return "No employees loaded yet";
            if (total == all) return "Whole company (all employees checked)";
            if (total == 0) return "Nothing selected -- check at least one employee to generate a report";

            var fullyCheckedDepartments = Departments
                .Where(d => d.IsSelected == true && d.Employees.Count > 0)
                .ToList();

            if (fullyCheckedDepartments.Count > 0 && fullyCheckedDepartments.Sum(d => d.Employees.Count) == total)
            {
                return fullyCheckedDepartments.Count == 1
                    ? $"Department: {fullyCheckedDepartments[0].Name}"
                    : $"{fullyCheckedDepartments.Count} departments: {string.Join(", ", fullyCheckedDepartments.Select(d => d.Name))}";
            }

            return total == 1 ? "1 employee selected" : $"{total} employees selected";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllTree))]
    private void SelectAllTree()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = true;
    }

    private bool CanSelectAllTree() => SelectedEmployeeCount < TotalEmployeeCount;

    [RelayCommand(CanExecute = nameof(CanClearTreeSelection))]
    private void ClearTreeSelection()
    {
        foreach (var node in Departments.SelectMany(d => d.Employees))
            node.IsSelected = false;
    }

    private bool CanClearTreeSelection() => SelectedEmployeeCount > 0;

    /// <summary>Employee IDs (Pin) for whichever employees are checked in the
    /// report-scope tree. Every loaded employee checked (the tree's default state) maps
    /// to null -- "everyone scheduled in the period" -- rather than an explicit list, so
    /// a report still covers anyone added since this tree was last loaded, not just who
    /// happened to be checked at load time.</summary>
    public HashSet<int>? GetSelectedPins()
    {
        if (TotalEmployeeCount == 0 || SelectedEmployeeCount == TotalEmployeeCount)
            return null;

        var ids = Departments
            .SelectMany(d => d.Employees)
            .Where(n => n.IsSelected)
            .Select(n => n.Employee.Pin)
            .ToHashSet();

        return ids.Count > 0 ? ids : null;
    }

    /// <summary>True if the given punch-clock employee code (AttendanceSummaryRow.
    /// EmployeeId, i.e. Employee.Pin) is currently checked in the report-scope
    /// tree. Backs ReportViewModel's live grid filter -- unlike GetSelectedPins
    /// above (whose null-for-"everyone-or-nobody-loaded" return is a DB-query
    /// convention), this has no such special case: checking zero employees really
    /// does mean "hide every row," matching a person actively narrowing down an
    /// already-generated report rather than describing what the next Generate
    /// Reports click should fetch.</summary>
    public bool IsChecked(int employeeId) =>
        Departments
            .SelectMany(d => d.Employees)
            .Any(n => n.IsSelected && n.Employee.Pin == employeeId);
}
