using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Wraps an Employee for display in a tree, adding a checkbox-backed
/// IsSelected flag -- used for multi-select bulk schedule assignment on the
/// Schedule tab, and for scoping a report to specific employees on the
/// Attendance tab (see EmployeeTreeBuilder, which both tabs' trees are built
/// from). Employee itself is a plain Core model with no change notification,
/// so this wrapper is what the checkbox actually binds to.
/// </summary>
public partial class EmployeeNodeViewModel : ObservableObject
{
    public required Employee Employee { get; init; }

    /// <summary>Checked in the tree view -- included when "Set Schedule for
    /// Selected Days" is clicked while multi-select mode is on (see
    /// MainViewModel.AssignScheduleToCheckedEmployeesAsync), or when generating an
    /// attendance report scoped to specific employees on the Attendance tab.
    /// Independent of TreeView.SelectedItem, which still drives the
    /// single-employee calendar view on the Schedule tab's right pane.</summary>
    [ObservableProperty]
    private bool isSelected;

    /// <summary>Always true now -- every employee has an Employee ID (Pin) set (see
    /// Employee.Pin's own doc comment), so there's no longer a "can never be matched to a
    /// punch log" employee for this to flag. Kept (rather than removed) only because
    /// AttendanceView.xaml still has a DataTrigger bound to it -- now permanently inert,
    /// since it only ever fired on False -- that dims/tooltips an unmatchable employee in
    /// the tree; harmless to leave as dead XAML, but worth removing there too next time
    /// that view is touched.</summary>
    public bool HasPin => true;

    /// <summary>True when this employee is Monthly-rated (see Employee.EmployeeType).
    /// Only meaningful on the Payroll tab's employee-picker trees (AddToPayrollGroupDialog/
    /// PayslipScopeDialog/PayrollWizardDialog -- Schedule's and Attendance's own trees don't
    /// bind it), which use it to badge Monthly employees so they're visible at a glance
    /// while picking who to include. Flags the minority state (Monthly) rather than the
    /// majority (Daily is the default -- see EmployeeType's own doc comment).</summary>
    public bool IsMonthly => Employee.EmployeeType == EmployeeType.Monthly;

    /// <summary>True unless the report-scope tree's search box has hidden this
    /// employee (see ReportScopeViewModel.SearchText/ApplySearchFilter) -- bound to the
    /// TreeView's ItemContainerStyle in AttendanceView.xaml so a non-matching employee
    /// simply collapses out of the tree instead of being removed from Employees, which
    /// would lose its checkbox state. Independent of IsSelected -- searching only changes
    /// what's shown, never what's checked. Always true on the Schedule tab's tree, which
    /// shares this same view-model shape but has no search box of its own.</summary>
    [ObservableProperty]
    private bool isVisible = true;
}
