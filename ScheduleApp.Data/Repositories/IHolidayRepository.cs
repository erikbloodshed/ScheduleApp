using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Data.Repositories;

/// <summary>
/// Abstraction over ScheduleApp's own Holidays table (see HolidayRepository).
/// Interface and implementation live together here in Data/Repositories, same
/// co-location as IPayrollRunRepository/PayrollRunRepository. Started out as
/// just list/add/update/delete -- ManageHolidaysDialog was Phase 1's only
/// consumer -- with ListDatesForPeriodAsync below added once Holiday Pay's
/// Phase 2/3 gave PayrollComputationService a second, period-scoped consumer.
/// </summary>
public interface IHolidayRepository
{
    /// <summary>Every saved holiday, ordered by Date -- what ManageHolidaysDialog's
    /// grid binds to directly. Not paged, same "single-user desktop app" reasoning
    /// as IPayrollRunRepository.ListAsync -- a company adds a handful of holidays a
    /// year, so this list never grows large enough to need it.</summary>
    Task<List<Holiday>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Just the Date column, for every holiday whose Date falls within
    /// [periodStart, periodEnd] -- what PayrollComputationService feeds into
    /// PayrollCalculator.Calculate/CalculateHolidayPay's own holidayDates
    /// parameter (Holiday Pay plan, Phase 2/3) rather than ListAsync's full,
    /// whole-table Holiday list plus an in-memory filter/Select. Not that
    /// ListAsync would be expensive to filter client-side -- see that method's
    /// own "never grows large enough to need paging" reasoning, which applies
    /// here too -- this is a separate method mainly so a payroll caller reads
    /// as "give me this period's holiday dates" without also pulling in the
    /// Name column and the whole-table scope it doesn't need. Ordered by Date,
    /// same convention as ListAsync.</summary>
    Task<List<DateOnly>> ListDatesForPeriodAsync(
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default);

    /// <summary>Adds a new holiday. Throws <see cref="DuplicateHolidayDateException"/>
    /// if holiday.Date is already taken -- checked here rather than left to the
    /// database's own unique index (see ScheduleDbContext's Holiday configuration)
    /// so ManageHolidaysDialog can show a friendly message instead of a raw
    /// DbUpdateException. Returns the holiday back with Id assigned.</summary>
    Task<Holiday> AddAsync(Holiday holiday, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing holiday's Date/Name in place (matched by
    /// holiday.Id). Same duplicate-date guard as AddAsync, excluding the row being
    /// updated itself so re-saving a holiday under its own existing date isn't
    /// mistaken for a conflict with itself. No-op if the id doesn't exist.</summary>
    Task UpdateAsync(Holiday holiday, CancellationToken cancellationToken = default);

    /// <summary>Removes a holiday. No-op if the id doesn't exist, same convention
    /// as IPayrollRunRepository.DeleteAsync.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
