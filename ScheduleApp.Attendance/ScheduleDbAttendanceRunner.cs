using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Repositories;

namespace ScheduleApp.Attendance;

/// <summary>
/// Real implementation of <see cref="IAttendanceRunner"/>: reads schedule and
/// employee data from ScheduleApp's SQL Server database (via the same
/// IScheduleRepository the Schedule tab uses) and punch logs from the same
/// database's AttendanceLogs table (via IAttendanceLogRepository -- populated
/// separately by AttendanceViewModel's import command, not read here from a
/// file), then runs <see cref="AttendanceWorkflowService"/> against them.
/// Registered in App.xaml.cs alongside IScheduleRepository.
/// </summary>
public sealed class ScheduleDbAttendanceRunner(
    IScheduleRepository scheduleRepository,
    IAttendanceLogRepository attendanceLogRepository,
    IManualAttendanceLogRepository manualAttendanceLogRepository,
    IDayPunchPairingRepository dayPunchPairingRepository) : IAttendanceRunner
{
    public Task<AttendanceRunResult> RunAsync(AttendanceRunRequest request, IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var workflowService = new AttendanceWorkflowService(
            scheduleRepository, attendanceLogRepository, manualAttendanceLogRepository,
            dayPunchPairingRepository, request.Policy);
        return workflowService.RunAsync(
            request.PeriodStart, request.PeriodEnd, request.TargetPins, progress, cancellationToken);
    }
}
