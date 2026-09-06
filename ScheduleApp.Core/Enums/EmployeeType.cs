namespace ScheduleApp.Core.Enums;

/// <summary>
/// How an employee's pay is structured -- read by ScheduleApp.Payroll.
/// PayrollCalculator to decide which rate field (Employee.DailyRate vs.
/// Employee.MonthlyRate) and which Basic Pay computation path applies for a
/// given period (see PayrollCalculator.WorkDays/BasicPayForDay). Defaults to
/// Daily (the existing, pre-this-feature behavior) so every employee already
/// in the database keeps computing exactly as before until someone opts them
/// into Monthly by hand via the Add/Edit Employee dialog.
/// </summary>
public enum EmployeeType
{
    Daily = 0,
    Monthly = 1,
}
