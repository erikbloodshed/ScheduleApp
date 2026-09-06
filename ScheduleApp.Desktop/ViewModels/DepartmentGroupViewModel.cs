using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One node in the left-hand tree. Wraps a real Department, or -- when
/// RealDepartment is null -- the synthetic "Unassigned" bucket of employees
/// with no department yet.
/// </summary>
public partial class DepartmentGroupViewModel : ObservableObject
{
    public required string Name { get; init; }
    public Department? RealDepartment { get; init; }
    public List<EmployeeNodeViewModel> Employees { get; init; } = new();

    public bool IsUnassignedBucket => RealDepartment is null;

    /// <summary>
    /// Tri-state "select all employees in this department" checkbox. true/false
    /// when every employee agrees, null (indeterminate) when only some are
    /// checked. Setting it from the UI (always true or false -- WPF only
    /// produces null for IsThreeState="False" checkboxes programmatically, never
    /// from a click) pushes that value down to every employee in the group.
    /// </summary>
    [ObservableProperty]
    private bool? isSelected = false;

    /// <summary>True unless the report-scope tree's search box has hidden every
    /// employee in this department and the department's own name doesn't match either
    /// (see ReportScopeViewModel.SearchText/ApplySearchFilter) -- bound to the TreeView's
    /// ItemContainerStyle in AttendanceView.xaml, the same way EmployeeNodeViewModel.
    /// IsVisible is. Always true while the search box is empty, and always true on the
    /// Schedule tab's tree, which shares this same view-model shape but has no search box
    /// of its own.</summary>
    [ObservableProperty]
    private bool isVisible = true;

    /// <summary>Bound two-way to the TreeViewItem's own IsExpanded via
    /// ItemContainerStyle, so a manual click still flows back here. ApplySearchFilter
    /// force-expands a department while it still has a visible match, so the match is
    /// actually on screen instead of tucked behind a collapsed node -- it never forces a
    /// collapse itself, so clearing the search box leaves whatever the person had open
    /// exactly as they left it.</summary>
    [ObservableProperty]
    private bool isExpanded;

    private bool _suppressChildSync;

    /// <summary>Call once after Employees is populated so the group checkbox stays
    /// in sync as individual employee checkboxes are toggled.</summary>
    public void AttachChildNotifications()
    {
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

    partial void OnIsSelectedChanged(bool? value)
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
