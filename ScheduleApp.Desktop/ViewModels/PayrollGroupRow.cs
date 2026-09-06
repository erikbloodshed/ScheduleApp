using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One row in PayrollPage's Payroll Group table — one per
/// PayrollViewModel.BatchScopeEmployees entry. Built and populated
/// incrementally by PayrollViewModel.RefreshPayrollGroupRowsAsync:
/// the row is added to PayrollGroupRows as soon as its employee is
/// known; NetPay is filled in (via the observable setter) once
/// ComputeOneAsync returns for that employee, so the DataGrid shows
/// progress as each employee's figure arrives rather than waiting for
/// all of them before rendering anything.
///
/// ObservableObject (not a plain sealed class like BatchChecklistRow
/// was) because NetPay is patched in place two ways:
///   1. By RefreshPayrollGroupRowsAsync itself, which sets it right
///      after each ComputeOneAsync call.
///   2. By LoadCoreAsync's opportunistic sync, which updates the row
///      matching the just-selected employee without triggering a full
///      refresh of every other row in the group.
/// </summary>
public sealed partial class PayrollGroupRow : ObservableObject
{
    public required Employee Employee { get; init; }

    /// <summary>Null until ComputeOneAsync has returned for this
    /// employee — displayed as an empty cell while loading. Also null
    /// when the employee has no Pin set (payroll cannot be computed).
    /// NumberConverter's null path returns string.Empty, so the cell
    /// just stays blank rather than showing "0.00" or an error while
    /// the figure is pending.</summary>
    [ObservableProperty]
    private decimal? netPay;

    /// <summary>Same LastName, FirstName format as Employee.DisplayName
    /// — keeps this table's Name column consistent with the tree it
    /// replaces.</summary>
    public string EmployeeName => Employee.DisplayName;

    /// <summary>Department name from the Employee nav property, falling
    /// back to EmployeeTreeBuilder.UnassignedGroupName when the employee
    /// has no department — matching the label the tree used for the same
    /// employees.</summary>
    public string DepartmentName =>
        Employee.Department?.Name ?? EmployeeTreeBuilder.UnassignedGroupName;

    /// <summary>"Daily"/"Monthly" straight off Employee.EmployeeType via plain
    /// ToString() — no converter needed, unlike ScheduleType/PunchStatus, since
    /// EmployeeType's own member names are already the display text (see
    /// PayrollSummaryView.xaml's Pay Type badge, which does the same thing).</summary>
    public string PayTypeText => Employee.EmployeeType.ToString();
}
