using System.Collections.ObjectModel;
using ReactiveUI;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One node in the left-hand tree. Wraps a real Department, or -- when
/// RealDepartment is null -- the synthetic "Unassigned" bucket of employees
/// with no department yet.
/// </summary>
public class DepartmentGroupViewModel : ReactiveObject
{
    public required string Name { get; init; }
    public Department? RealDepartment { get; init; }
    public List<EmployeeNodeViewModel> Employees { get; init; } = new();

    /// <summary>The employees a search box hasn't hidden (IsVisible), in Employees' order --
    /// what a filtered tree binds its rows to, since SfTreeView can't hide a row itself. Kept
    /// in step by EmployeeTreeSearchFilter.Apply; every employee until that first runs.</summary>
    public ObservableCollection<EmployeeNodeViewModel> VisibleEmployees { get; } = new();

    /// <summary>Brings VisibleEmployees in line with each employee's IsVisible.</summary>
    public void RefreshVisibleEmployees() => CollectionSync.Sync(VisibleEmployees, Employees.Where(e => e.IsVisible));

    public bool IsUnassignedBucket => RealDepartment is null;

    /// <summary>
    /// Tri-state "select all employees in this department" checkbox. true/false
    /// when every employee agrees, null (indeterminate) when only some are
    /// checked. Setting it from the UI (always true or false -- WPF only
    /// produces null for IsThreeState="False" checkboxes programmatically, never
    /// from a click) pushes that value down to every employee in the group.
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            OnIsSelectedChanged(value);
        }
    }

    private bool? _isSelected = false;

    /// <summary>True unless the report-scope tree's search box has hidden every
    /// employee in this department and the department's own name doesn't match either
    /// (see ReportScopeViewModel.SearchText/ApplySearchFilter) -- bound to the TreeView's
    /// ItemContainerStyle in AttendanceView.xaml, the same way EmployeeNodeViewModel.
    /// IsVisible is. Always true while the search box is empty, and always true on the
    /// Schedule tab's tree, which shares this same view-model shape but has no search box
    /// of its own.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private bool _isVisible = true;

    /// <summary>Bound two-way to the TreeViewItem's own IsExpanded via
    /// ItemContainerStyle, so a manual click still flows back here. ApplySearchFilter
    /// force-expands a department while it still has a visible match, so the match is
    /// actually on screen instead of tucked behind a collapsed node -- it never forces a
    /// collapse itself, so clearing the search box leaves whatever the person had open
    /// exactly as they left it.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    private bool _isExpanded;

    private bool _suppressChildSync;

    /// <summary>Call once after Employees is populated so the group checkbox stays
    /// in sync as individual employee checkboxes are toggled.</summary>
    public void AttachChildNotifications()
    {
        RefreshVisibleEmployees();

        foreach (var node in Employees)
        {
            node.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EmployeeNodeViewModel.IsSelected))
                    RecomputeIsSelected();
            };
        }

        RecomputeIsSelected();
    }

    private void OnIsSelectedChanged(bool? value)
    {
        if (_suppressChildSync || value is null) return;

        foreach (var node in Employees)
            node.IsSelected = value.Value;
    }

    private void RecomputeIsSelected()
    {
        _suppressChildSync = true;
        try
        {
            if (Employees.Count == 0) IsSelected = false;
            else if (Employees.All(e => e.IsSelected)) IsSelected = true;
            else if (Employees.All(e => !e.IsSelected)) IsSelected = false;
            else IsSelected = null;
        }
        finally
        {
            _suppressChildSync = false;
        }
    }
}
