namespace ScheduleApp.Core.Attendance;

/// <summary>
/// How a persisted <see cref="AttendanceLog"/> row got into the database.
/// File is a manually-exported .dat import (see AttendanceViewModel's Import
/// Punch Log command). Network is a direct pull over the ZKTeco wire protocol
/// (CMD_ATTLOG_RRQ) initiated from ScheduleApp itself -- see
/// ScheduleApp.Data.Attendance.ZkTecoAttendanceLogReader and
/// AttendanceViewModel's Fetch from Device command. Adms remains reserved for
/// a *different* integration model: a future listener that passively accepts
/// pushes from the device's own ADMS/cloud-push protocol, rather than
/// ScheduleApp actively connecting out to the device -- adding it now costs
/// nothing and means that path can call IAttendanceLogRepository.AddLogsAsync
/// directly later without a schema change or a way to tell the three apart
/// after the fact.
///
/// Manual is different from the other three: it never labels a row actually
/// stored in the AttendanceLogs table. A manually-entered punch lives in its
/// own ManualAttendanceLog row/table instead (see that class and
/// IManualAttendanceLogRepository) -- Manual only appears on an in-memory
/// AttendanceLog produced by ManualAttendanceLog.ToAttendanceLog() when a
/// caller (AttendanceWorkflowService, AttendanceViewModel's Punch Records
/// query) merges the two sources together for matching/display. See
/// AttendanceLog.Reason for the one other property that's only ever
/// populated on that same in-memory, Manual-sourced projection.
/// </summary>
public enum AttendanceLogSource
{
    File = 0,
    Adms = 1,
    Network = 2,
    Manual = 3
}
