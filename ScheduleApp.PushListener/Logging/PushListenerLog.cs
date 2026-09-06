using System.Net;

namespace ScheduleApp.PushListener.Logging;

/// <summary>
/// Every ILogger message this service writes, as [LoggerMessage] source-generated
/// methods rather than the LoggerExtensions.Log* extension methods they replace
/// (CA1848). The generator emits a cached LoggerMessage.Define delegate per entry,
/// so a call that ends up below the configured MinimumLevel costs an IsEnabled check
/// instead of boxing every argument into an object[] and allocating the params array.
/// That matters most on the /iclock/* path, which every device hits on a polling
/// interval, all day, forever.
///
/// Named PushListenerLog rather than the conventional "Log" because Program.cs uses
/// Serilog's own static Log class for bootstrap logging -- see Program.cs.
///
/// EventIds are grouped by area so a filter on the Event Log stays readable:
/// 1000s = device protocol (/iclock/*), 2000s = admin API, 3000s = startup.
/// </summary>
internal static partial class PushListenerLog
{
    // ---- 1000s: device protocol, IClockController ----

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Handshake from SN={SerialNumber}, remote={RemoteAddress}")]
    public static partial void Handshake(ILogger logger, string? serialNumber, IPAddress? remoteAddress);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "ATTLOG SN={SerialNumber} received={Received} new={New} duplicate={Duplicate} " +
                  "skippedNonNumericPin={SkippedNonNumericPin} statuses=[{Statuses}]")]
    public static partial void AttLogProcessed(ILogger logger, string? serialNumber, int received,
        int @new, int duplicate, int skippedNonNumericPin, string statuses);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "ATTLOG SN={SerialNumber} received=0 usable records, skippedNonNumericPin={SkippedNonNumericPin}")]
    public static partial void AttLogAllSkipped(ILogger logger, string? serialNumber, int skippedNonNumericPin);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Sending queued command to SN={SerialNumber}: C:{CommandId}:{CommandText}")]
    public static partial void SendingQueuedCommand(ILogger logger, string? serialNumber, int commandId, string commandText);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
        Message = "Command result from SN={SerialNumber}: {Body}")]
    public static partial void CommandResult(ILogger logger, string? serialNumber, string body);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "fdata upload from SN={SerialNumber}: {ByteCount} bytes (not stored)")]
    public static partial void BiometricUpload(ILogger logger, string? serialNumber, long byteCount);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug,
        Message = "Ping from SN={SerialNumber}")]
    public static partial void Ping(ILogger logger, string? serialNumber);

    // ---- 2000s: admin API, AdminController ----

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Could not read log file {LogFile} for /admin/logs.")]
    public static partial void LogFileUnreadable(ILogger logger, Exception exception, string logFile);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information,
        Message = "Queued command {CommandId} for SN={SerialNumber}: {CommandText}")]
    public static partial void QueuedCommand(ILogger logger, int commandId, string serialNumber, string commandText);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information,
        Message = "Queued resync for SN={SerialNumber}: {Command}")]
    public static partial void QueuedResync(ILogger logger, string serialNumber, string command);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information,
        Message = "Force-recheck queued for SN={SerialNumber}: stamps reset, CommandId={CommandId}")]
    public static partial void QueuedForceRecheck(ILogger logger, string serialNumber, int commandId);

    // ---- 3000s: startup, Program ----

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "{PendingCount} pending EF Core migration(s) on ScheduleAppDb. This listener " +
                  "does not apply migrations itself -- run ScheduleApp.Desktop (or `dotnet ef " +
                  "database update`) at least once first, since it owns the schema.")]
    public static partial void PendingMigrations(ILogger logger, int pendingCount);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "Connected to ScheduleAppDb -- schema is up to date.")]
    public static partial void SchemaUpToDate(ILogger logger);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "Could not verify ScheduleAppDb's schema. Check the connection string, that the " +
                  "service account has a SQL Server login (see README), and that SQL Server Express " +
                  "is running and reachable from this machine.")]
    public static partial void SchemaCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information,
        Message = "ScheduleApp.PushListener listening on {ListenUrl} -- waiting for device pushes at /iclock/*.")]
    public static partial void Listening(ILogger logger, string listenUrl);
}
