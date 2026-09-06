using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// One row in PayrollPage's "Not in group" table — the lower half of the Payroll Group
/// panel (see PayrollViewModel.AvailableEmployeeRows), one per roster employee who is
/// NOT currently in BatchScopeEmployees. Deliberately a plain sealed class rather than
/// an ObservableObject the way PayrollGroupRow is: nothing on a row here is ever patched
/// in place after construction (there's no NetPay to fill in later) -- Payroll
/// ViewModel.RebuildAvailableEmployeeRows just replaces the whole AvailableEmployeeRows
/// collection wholesale on every BatchScopeEmployees change instead.
/// </summary>
public sealed class AvailableEmployeeRow
{
    public required Employee Employee { get; init; }

    /// <summary>Same LastName, FirstName format as PayrollGroupRow.EmployeeName -- keeps
    /// this table's Employee column reading identically to the "Included" grid above
    /// it.</summary>
    public string EmployeeName => Employee.DisplayName;

    /// <summary>Same Department nav fallback as PayrollGroupRow.DepartmentName.</summary>
    public string DepartmentName =>
        Employee.Department?.Name ?? EmployeeTreeBuilder.UnassignedGroupName;

    /// <summary>Same PayTypeText idea as PayrollGroupRow.PayTypeText -- "Daily"/"Monthly"
    /// straight off Employee.EmployeeType via plain ToString(), no converter needed (see
    /// that property's own doc comment for why).</summary>
    public string PayTypeText => Employee.EmployeeType.ToString();
}
