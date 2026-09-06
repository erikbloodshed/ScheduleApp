namespace ScheduleApp.Core.Exceptions;

/// <summary>
/// Thrown by IHolidayRepository.AddAsync/UpdateAsync when the given date is already
/// listed as a different holiday. HolidayDialog already blocks this before it gets
/// here (see its own existingDates check) -- this is a second line of defense for
/// anything that calls the repository directly, e.g. two people using the app
/// against the same database at the same time. Same shape as
/// DuplicateEmployeeIdException/DuplicateUsernameException.
/// </summary>
public class DuplicateHolidayDateException : Exception
{
    public DateOnly Date { get; }
    public string ExistingName { get; }

    public DuplicateHolidayDateException(DateOnly date, string existingName)
        : base($"{date:MMMM d, yyyy} is already listed as a holiday (\"{existingName}\"). " +
               "Edit or delete the existing one instead of adding another.")
    {
        Date = date;
        ExistingName = existingName;
    }
}
