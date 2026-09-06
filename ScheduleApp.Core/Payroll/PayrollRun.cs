namespace ScheduleApp.Core.Payroll;

/// <summary>
/// A saved period + employee-scope grouping -- what the "New Payroll Run…" wizard
/// on PayrollPage creates (PayrollWizardViewModel) and "Load Payroll Group…"
/// retrieves (IPayrollRunRepository.ListAsync/GetByIdAsync). Deliberately just
/// membership: no computed Gross Pay/Deductions/Net Pay figures are stored here or
/// on <see cref="PayrollRunEmployee"/> -- reopening a run still re-asks
/// PayrollCalculator live, the same way the Payroll tab always has (see
/// ScheduleApp.Payroll.PayrollResult's own doc comment on there being no
/// "finalize and lock" run record), so a later attendance correction or
/// adjustment edit is reflected the next time this run is opened rather than
/// silently going stale. What this actually buys over the old session-only
/// PayrollViewModel.BatchScopeEmployees is durability -- the period+scope
/// survives closing the app -- not a locked/frozen snapshot.
/// </summary>
public class PayrollRun
{
    public int Id { get; set; }

    /// <summary>Shown in "Load Payroll Group…"'s list and as the wizard's own
    /// default title text -- auto-filled from PeriodStart/PeriodEnd (e.g. "May
    /// 1-15, 2026") but freely editable, same "starts sensible, not locked"
    /// spirit as PayrollAdjustment.Description.</summary>
    public string Label { get; set; } = string.Empty;

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>When this run was saved -- what "Load Payroll Group…" sorts by
    /// (newest first), same role as PayrollAdjustment.CreatedAt.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Free-text name of whoever ran the wizard -- same no-user-system
    /// convention as PayrollAdjustment.EnteredBy/ManualAttendanceLog.EnteredBy.
    /// Defaults to Environment.UserName in PayrollWizardViewModel, same as
    /// those.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Every employee this run covers -- see <see
    /// cref="PayrollRunEmployee"/>'s own doc comment for why this is membership
    /// only, no computed figures. Populated by IPayrollRunRepository.GetByIdAsync;
    /// empty (not null) from a plain query that doesn't need it (e.g.
    /// ListAsync's own row projection).</summary>
    public List<PayrollRunEmployee> Employees { get; set; } = [];
}
