namespace ScheduleApp.Core.Models;

/// <summary>
/// One calendar date the company treats as a holiday -- entered by hand (see the
/// Holiday Pay design decisions: no auto-recurring fixed dates, since a handful of
/// Philippine holidays move from year to year and a flat manually-maintained list
/// is simplest to reason about). Deliberately just a date + a label, with no
/// Regular-vs-Special distinction yet -- the Holiday Pay feature currently treats
/// every listed date the same way (see ScheduleApp.Payroll.PayrollCalculator once
/// Phase 2 lands), so there's nothing else here to configure per holiday today.
///
/// Company-wide, not per-employee or per-department -- unlike ScheduleType (which
/// ScheduleEntry sets one row per employee per day), a holiday applies to whoever
/// happens to be scheduled to work that date, so it lives as its own small lookup
/// table rather than as another ScheduleType value.
/// </summary>
public class Holiday
{
    public int Id { get; set; }

    /// <summary>The calendar date this holiday falls on. Unique -- see
    /// ScheduleDbContext's Holiday configuration -- so the same date can't be
    /// listed twice under two different names.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Display label, e.g. "New Year's Day" or "National Heroes Day" --
    /// shown wherever the holiday list itself is shown (ManageHolidaysDialog's
    /// grid today; a Payroll Summary/payslip line once Phase 2/3 read this list).
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
