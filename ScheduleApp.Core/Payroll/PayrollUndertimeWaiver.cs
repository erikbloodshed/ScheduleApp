namespace ScheduleApp.Core.Payroll;

/// <summary>
/// Marks one employee's one payroll period as having its computed Undertime
/// deduction disregarded -- e.g. management decided to waive it for that
/// period even though the hours were genuinely short. One row per
/// employee+period means "waived"; no row means "not waived" (the default,
/// and by far the common case) -- there's no separate false-flag column to
/// keep in sync, only presence/absence of a row (see
/// IPayrollUndertimeWaiverRepository.SetWaivedAsync). Modeled on
/// PayrollAdjustment's own EmployeeId/PeriodStart/PeriodEnd convention, just
/// without an Amount/Type/Description -- this is a pure on/off switch, not
/// an itemized line.
///
/// The Undertime figure itself is always computed the same way regardless of
/// this row's presence (see PayrollCalculator.Calculate) -- waiving only
/// controls whether it's counted into PayrollResult.TotalDeductions/NetPay,
/// never whether the computed amount is shown (see PayrollLineItem.Waived).
/// </summary>
public class PayrollUndertimeWaiver
{
    public int Id { get; set; }

    /// <summary>The punch clock's own employee code -- same EmployeeId
    /// convention as PayrollAdjustment.EmployeeId (matches Employee.Pin, not
    /// a ScheduleApp database key).</summary>
    public int EmployeeId { get; set; }

    /// <summary>Same period convention as PayrollAdjustment.PeriodStart/
    /// PeriodEnd -- a row only ever belongs to the one period it was set
    /// for, not a standing preference that carries forward if the period
    /// pickers move.</summary>
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>When this row was written into ScheduleApp's own database --
    /// same role as PayrollAdjustment.CreatedAt.</summary>
    public DateTime CreatedAt { get; set; }
}
