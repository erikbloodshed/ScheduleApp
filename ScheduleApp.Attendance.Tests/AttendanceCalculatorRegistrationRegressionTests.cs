using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Phase 2.5's exit check: "existing schedule types' output is byte-for-byte
/// unchanged." Phase 2.2 only added one dictionary entry
/// (Strategies[ScheduleType.RestDay]) to AttendanceCalculator -- this is a
/// cheap sanity check that doing so didn't disturb how a Normal entry still
/// routes and computes, since there are no pre-existing Attendance unit
/// tests/fixtures for this project to regression-check against otherwise
/// (this is the first test project ScheduleApp.Attendance has had).
/// </summary>
public class AttendanceCalculatorRegistrationRegressionTests
{
    [Fact]
    public void NormalScheduleType_StillRoutesToSingleWindowStrategyAndComputesAsBefore()
    {
        var employee = new Employee
        {
            Pin = 1001,
            LastName = "Cruz",
            FirstName = "Juan",
        };

        var schedule = new ScheduleEntry
        {
            EmployeeId = employee.Pin,
            Employee = employee,
            ScheduleType = ScheduleType.Normal,
            Date = RestDayTestFixtures.Date,
            TimeIn = new TimeOnly(8, 0),
            WorkTimeHours = 8m, // scheduled TimeOut = 4:00 PM
        };

        var punches = new List<AttendanceLog>
        {
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(8, 0)),
            RestDayTestFixtures.Punch(employee.Pin, new TimeOnly(16, 0)),
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, RestDayTestFixtures.DefaultPolicy);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(ScheduleType.Normal, summary.ScheduleType);
        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(8.0, summary.Worked_H);
        Assert.Equal(0.0, summary.Overtime_H);
        Assert.Equal(0.0, summary.Remain_H);
    }
}
