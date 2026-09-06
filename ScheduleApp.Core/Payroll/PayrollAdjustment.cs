using ScheduleApp.Core.Enums;

namespace ScheduleApp.Core.Payroll;

/// <summary>
/// One itemized Gross Pay/Deductions line for one employee's payroll period --
/// e.g. an Allowance, a Cash Advance, an SSS contribution. Modeled directly on
/// ScheduleApp.Core.Attendance.ManualAttendanceLog: a hand-entered row living
/// in its own table (PayrollAdjustments, see ScheduleDbContext), summed by
/// PayrollCalculator per (EmployeeId, PeriodStart, PeriodEnd, Type) rather
/// than derived from anything else. Multiple rows per employee/period/type
/// are expected and normal -- this *is* the itemized list PayrollSummaryView
/// shows for each category, not a constraint to work around.
/// </summary>
public class PayrollAdjustment
{
    public int Id { get; set; }

    /// <summary>The punch clock's own employee code -- matches Employee.Pin,
    /// same convention as AttendanceLog.EmployeeId/ManualAttendanceLog.EmployeeId,
    /// not a ScheduleApp database key.</summary>
    public int EmployeeId { get; set; }

    /// <summary>
    /// The payroll period this line was entered for -- the same period the
    /// Payroll tab's date pickers select (see PayrollViewModel.PeriodStart/
    /// PeriodEnd), stored as plain calendar dates the same way
    /// ScheduleEntry.Date/AttendanceSummary.ShiftDate are, since a payroll
    /// period has no time-of-day component. A row only ever belongs to the
    /// one period it was entered for -- it isn't a running balance that
    /// carries forward if the period pickers move (Cash Advance, for
    /// instance, is a flat one-time deduction per period, not an amortized
    /// balance -- see the Payroll Feature plan's Assumption 4).
    /// </summary>
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    public PayrollAdjustmentType Type { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Free-text itemized label (e.g. "Uniform", "Loan repayment
    /// 2/5") -- not a maintained Charge Types lookup (Assumption 5 in the
    /// Payroll Feature plan). Easy to layer a maintained list on top later if
    /// typed names start drifting, without changing this column.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Free-text name of whoever entered this -- same
    /// no-user-system convention as ManualAttendanceLog.EnteredBy. Defaults
    /// to the current Windows username in the entry dialog, but editable.
    /// </summary>
    public string EnteredBy { get; set; } = string.Empty;

    /// <summary>When this row was written into ScheduleApp's own database --
    /// same role as ManualAttendanceLog.CreatedAt.</summary>
    public DateTime CreatedAt { get; set; }
}