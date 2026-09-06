using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Picker dialog that lets a person add employees to an already-saved payroll group.
/// Reuses the same DepartmentGroupViewModel/EmployeeNodeViewModel tree shape as
/// PayrollWizardDialog's Step 2 and PayslipScopeDialog -- built by the caller via
/// EmployeeTreeBuilder and passed in, same "no live repository read inside the dialog"
/// convention LoadPayrollGroupDialog follows.
///
/// Employees already in the group are pre-checked so the person can see who's already a
/// member -- but PayrollViewModel.AddEmployeesToGroupAsync filters those out before
/// calling AddEmployeeAsync, so clicking Add with only existing members checked is a
/// no-op rather than an error.
///
/// SelectedEmployees is the full set of checked employees on OK (existing members
/// included); PayrollViewModel.AddEmployeesToGroupAsync is responsible for filtering to
/// only the genuinely new ones. That keeps this dialog's own job simple: show the tree,
/// return whoever's checked.
/// </summary>
public partial class AddToPayrollGroupDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<DepartmentGroupViewModel> _departments;

    /// <summary>Every checked employee when AddButton_Click sets DialogResult = true.
    /// Null if the dialog was cancelled -- consistent with LoadPayrollGroupDialog.SelectedRun's
    /// own null-on-cancel convention.</summary>
    public List<Employee>? SelectedEmployees { get; private set; }

    /// <param name="departments">Pre-built tree from EmployeeTreeBuilder.Build --
    /// already filtered to departments with employees and an optional Unassigned group.
    /// Built by the caller (PayrollViewModel.AddEmployeesToGroupAsync) before opening
    /// this dialog, same "caller owns the async data load" pattern PayrollWizardViewModel
    /// follows for its own Departments list.</param>
    /// <param name="currentMembers">Employees already in the group -- pre-checked in the
    /// tree so the person can see who's already in.</param>
    public AddToPayrollGroupDialog(
        List<DepartmentGroupViewModel> departments,
        IReadOnlyList<Employee> currentMembers)
    {
        InitializeComponent();

        _departments = departments;
        EmployeeTree.ItemsSource = departments;

        // Pre-check employees that are already in the group, so they appear checked
        // but the person isn't forced to re-add them -- AddEmployeesToGroupAsync filters
        // them out on the way back in.
        var currentIds = currentMembers.Select(e => e.Id).ToHashSet();
        foreach (var dept in departments)
        foreach (var node in dept.Employees)
        {
            if (currentIds.Contains(node.Employee.Id))
                node.IsSelected = true;

            node.PropertyChanged += (_, _) => RefreshScopeText();
        }

        // Expand all departments on open so the person sees the full roster without
        // having to click each one -- same "start expanded" approach the wizard's Step 2
        // uses when presetSelection is non-null.
        foreach (var dept in departments)
            dept.IsExpanded = true;

        RefreshScopeText();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        EmployeeTreeSearchFilter.Apply(_departments, SearchBox.Text);
        RefreshScopeText();
    }

    private void RefreshScopeText()
    {
        var checkedCount = _departments
            .SelectMany(d => d.Employees)
            .Count(n => n.IsSelected);

        ScopeText.Text = checkedCount == 0
            ? "No employees checked."
            : checkedCount == 1 ? "1 employee checked." : $"{checkedCount} employees checked.";
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedEmployees = _departments
            .SelectMany(d => d.Employees)
            .Where(n => n.IsSelected)
            .Select(n => n.Employee)
            .ToList();

        DialogResult = true;
    }
}
