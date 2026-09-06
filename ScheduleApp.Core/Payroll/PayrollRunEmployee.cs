namespace ScheduleApp.Core.Payroll;

/// <summary>
/// One employee's membership in a saved <see cref="PayrollRun"/> -- literally
/// what PayrollViewModel.BatchScopeEmployees held in memory before this existed,
/// one row per employee, now durable. Deliberately carries no computed Gross Pay/
/// Deductions/Net Pay figures: see PayrollRun's own doc comment for why
/// reopening a run recomputes live via PayrollCalculator instead of reading a
/// frozen number from here.
/// </summary>
public class PayrollRunEmployee
{
    public int Id { get; set; }

    public int PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }

    /// <summary>Employee.Pin -- same convention as PayrollAdjustment.EmployeeId/
    /// AttendanceLog.EmployeeId, not a ScheduleApp database key.</summary>
    public int EmployeeId { get; set; }
}
