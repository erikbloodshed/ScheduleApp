namespace ScheduleApp.Core.Exceptions;

/// <summary>
/// Thrown by AddEmployeeAsync/UpdateEmployeeAsync when the given Employee ID
/// (Employee.Pin) is already assigned to a different employee. The
/// Add/Edit Employee dialog already blocks this before it gets here (see
/// EmployeeDialog's takenEmployeeIds check) -- this is a second line of
/// defense for anything that calls the repository directly, e.g. two people
/// using the app against the same database at the same time.
/// </summary>
public class DuplicateEmployeeIdException(int employeeId) : Exception($"Employee ID {employeeId} is already assigned to another employee.")
{
    public int EmployeeId { get; } = employeeId;
}
