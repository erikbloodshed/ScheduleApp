using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data;
using ScheduleApp.PushListener.Services;

namespace ScheduleApp.PushListener.Controllers;

/// <summary>
/// Admin/testing helpers -- NOT part of the ADMS protocol, just for inspecting state,
/// browsing what's landed in AttendanceLogs, and queuing a command without needing real
/// hardware. Ported from the AdmsServer prototype's own AdminController; GetDevices,
/// QueueCommand, ResyncAttendance, and ForceRecheck are unchanged (they only ever touched
/// DeviceRegistry, never the database). GetAttendance is the one rewritten to match this
/// app's schema -- see its own remarks below.
///
/// Talks to ScheduleDbContext directly rather than through IAttendanceLogRepository: this is
/// a read-only diagnostic surface with filters (by device, by employee, by date range, capped
/// row count) that the shared repository interface was never meant to expose, and extending
/// that interface just for an admin/testing endpoint would widen a contract every other
/// caller (File/Network import, this listener's own DataUpload) also depends on.
/// </summary>
[ApiController]
[Route("admin")]
public class AdminController(
    DeviceRegistry devices, ScheduleDbContext db, ILogger<AdminController> logger, IConfiguration configuration)
    : ControllerBase
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private const string WideRangeStart = "2000-01-01 00:00:00";
    private const string WideRangeEnd = "2099-12-31 23:59:59";

    private readonly DeviceRegistry _devices = devices;
    private readonly ScheduleDbContext _db = db;
    private readonly ILogger<AdminController> _logger = logger;
    private readonly IConfiguration _configuration = configuration;

    // Process start time, not app-host start time -- correct under both `dotnet run` and as
    // a Windows Service, since in both cases this IS the whole process, not a wrapper around
    // one. Captured once as a static so every request reports the same value rather than
    // recomputing "now" and calling it StartedAtUtc.
    private static readonly DateTime StartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // Same path Program.cs's Serilog file sink writes to -- see that file's WriteTo.File call.
    // Duplicated here rather than shared via a constant in a common project, since this is a
    // read-only diagnostic reader of the sink's output, not something that should be able to
    // influence where Serilog itself writes by editing a shared value.
    private static readonly string LogsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    private static readonly Dictionary<string, int> LevelRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Verbose"] = 0,
        ["Debug"] = 1,
        ["Information"] = 2,
        ["Warning"] = 3,
        ["Error"] = 4,
        ["Fatal"] = 5
    };

    /// <summary>
    /// Cheap liveness/readiness check for the Push Listener tab's connection bar -- distinct
    /// from GetDevices/GetAttendance in that those tell you what the listener has recorded,
    /// this tells you whether the listener process itself is actually healthy right now.
    /// DbReachable matters because AdminController (and DataUpload/IClockController) talk to
    /// ScheduleDbContext directly -- if SQL Server Express is down or the connection string is
    /// wrong, the listener process is still "up" (this endpoint still answers) but can't do
    /// its actual job, which Status reflects.
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> GetHealth()
    {
        bool dbReachable;
        try
        {
            dbReachable = await _db.Database.CanConnectAsync();
        }
        catch
        {
            // CanConnectAsync already returns false for most failures, but a malformed
            // connection string can still throw rather than return false -- treat that the
            // same as "unreachable" instead of letting it 500 what's meant to be a robust
            // diagnostic endpoint.
            dbReachable = false;
        }

        return Ok(new
        {
            Status = dbReachable ? "Healthy" : "Degraded",
            StartedAtUtc,
            UptimeSeconds = (long)(DateTime.UtcNow - StartedAtUtc).TotalSeconds,
            DbReachable = dbReachable,
            DeviceCount = _devices.Devices.Count(),
            ListenUrl = _configuration["Push:ListenUrl"] ?? "http://0.0.0.0:8080"
        });
    }

    /// <summary>
    /// Tails the current structured log file (see Program.cs's Serilog.Sinks.File setup,
    /// CompactJsonFormatter -- one JSON object per line). "Current" means whichever
    /// pushlistener-*.json file was most recently written to, not a hardcoded today's-date
    /// filename, so this keeps working correctly right at a local-midnight rollover instead of
    /// briefly pointing at a file that's about to stop being written to.
    ///
    /// take/level/sn follow the same conventions as GetAttendance: take caps how many entries
    /// come back (default 200, hard ceiling 1000), newest first; level is a minimum severity,
    /// not an exact match (Warning also returns Error and Fatal); sn matches against the
    /// flattened Details string, since not every log line has a SerialNumber property (e.g.
    /// startup/shutdown lines) and this is meant to help a human scan, not a strict filter.
    /// </summary>
    [HttpGet("logs")]
    public IActionResult GetLogs([FromQuery] int? take, [FromQuery] string? level, [FromQuery] string? sn)
    {
        var limit = Math.Clamp(take ?? 200, 1, 1000);

        string? minLevel = null;
        if (!string.IsNullOrWhiteSpace(level))
        {
            minLevel = level.Trim();
            if (!LevelRank.ContainsKey(minLevel))
            {
                return BadRequest($"level must be one of: {string.Join(", ", LevelRank.Keys)}.");
            }
        }

        if (!Directory.Exists(LogsDirectory))
        {
            return Ok(Array.Empty<object>());
        }

        var latestFile = new DirectoryInfo(LogsDirectory)
            .GetFiles("pushlistener-*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestFile is null)
        {
            return Ok(Array.Empty<object>());
        }

        List<string> lines;
        try
        {
            // FileShare.ReadWrite is required here -- Serilog's file sink holds this file open
            // for writing for as long as the process is up, so a plain File.ReadAllLines
            // (exclusive read by default) would throw on every single call.
            using var stream = new FileStream(latestFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = [];
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read log file {LogFile} for /admin/logs.", latestFile.FullName);
            return Ok(Array.Empty<object>());
        }

        var entries = new List<object>();
        // Walk from the end so "take" means "the most recent N", matching GetAttendance's
        // newest-first ordering, not "the first N ever written to this file".
        for (var i = lines.Count - 1; i >= 0 && entries.Count < limit; i--)
        {
            if (!TryParseCompactJsonLine(lines[i], out var entry))
            {
                // A half-written last line (the file is still being appended to live) or any
                // other malformed line is skipped rather than failing the whole request.
                continue;
            }

            if (minLevel is not null && LevelRank[entry.Level] < LevelRank[minLevel])
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sn) &&
                !(entry.Details?.Contains(sn, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            entries.Add(new
            {
                entry.TimestampUtc,
                entry.Level,
                entry.Message,
                entry.Exception,
                entry.Details
            });
        }

        return Ok(entries);
    }

    private sealed record ParsedLogEntry(DateTime TimestampUtc, string Level, string Message, string? Exception, string? Details);

    /// <summary>
    /// Parses one line of CompactJsonFormatter output. "@t" is the timestamp, "@mt" the
    /// rendered message, "@x" an exception if present. "@l" (level) is CompactJsonFormatter's
    /// own space-saving convention: it's omitted entirely for the default Information level,
    /// not a bug in what's being read. Every other top-level key is a structured property from
    /// the original LogInformation/LogWarning call (SerialNumber, CommandId, etc.) -- those are
    /// folded into a flattened Details string rather than returned as a nested object, since
    /// PushListenerLogEntry on the Desktop side is a flat DataGrid row, not a tree.
    /// </summary>
    private static bool TryParseCompactJsonLine(string line, out ParsedLogEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("@t", out var tProp) || !tProp.TryGetDateTime(out var timestamp))
            {
                return false;
            }

            var level = root.TryGetProperty("@l", out var lProp) ? lProp.GetString() ?? "Information" : "Information";
            var message = root.TryGetProperty("@mt", out var mtProp) ? mtProp.GetString() ?? "" : "";
            var exception = root.TryGetProperty("@x", out var xProp) ? xProp.GetString() : null;

            var detailParts = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.StartsWith('@'))
                {
                    continue;
                }
                detailParts.Add($"{prop.Name}={prop.Value}");
            }

            entry = new ParsedLogEntry(
                timestamp.ToUniversalTime(),
                level,
                message,
                exception,
                detailParts.Count > 0 ? string.Join(", ", detailParts) : null);
            return true;
        }
    }

    [HttpGet("devices")]
    public IActionResult GetDevices() =>
        Ok(_devices.Devices.Select(d => new
        {
            d.SerialNumber,
            d.LastSeenUtc,
            d.AttLogStamp,
            d.OperLogStamp,
            PendingCommandCount = d.PendingCommands.Count
        }));

    /// <summary>
    /// Read-only window into AttendanceLogs -- the DB equivalent of tailing
    /// raw_uploads.log, but parsed and filterable. sn/pin filter (either optional); take
    /// caps how many rows come back, newest first, default 50, hard ceiling 1000.
    ///
    /// "pin" here means the same thing it does on a device: the badge/PIN code, which
    /// ScheduleApp stores as the numeric EmployeeId (Employee.Pin) rather than a raw
    /// string -- see IClockController.DataUpload's own int.TryParse for the same reasoning.
    /// A non-numeric pin can never match a row here, so this returns 400 rather than
    /// silently coming back empty.
    ///
    /// sn matches AttendanceLogs.DeviceSerialNumber, which both this listener's Adms rows
    /// and "Fetch from Device"'s Network rows set -- this deliberately isn't Adms-only, so
    /// it doubles as "what has this specific device recorded, regardless of how it got
    /// here" rather than "what has this listener specifically received."
    ///
    /// Optional startTime/endTime -- same "yyyy-MM-dd HH:mm:ss" format as
    /// resync-attendance -- filter by punch Timestamp, inclusive on both ends. Either can
    /// be given alone (open-ended range).
    /// </summary>
    [HttpGet("attendance")]
    public async Task<IActionResult> GetAttendance(
        [FromQuery] string? sn,
        [FromQuery] string? pin,
        [FromQuery] int? take,
        [FromQuery] string? startTime,
        [FromQuery] string? endTime)
    {
        var limit = Math.Clamp(take ?? 50, 1, 1000);

        int? employeeId = null;
        if (!string.IsNullOrWhiteSpace(pin))
        {
            if (!int.TryParse(pin.Trim(), out var parsedPin))
            {
                return BadRequest(
                    "pin must be numeric -- ScheduleApp matches punches to employees by a " +
                    "numeric EmployeeId (Employee.Pin), the same as every other punch source.");
            }
            employeeId = parsedPin;
        }

        if (!TryParseOptionalTimestamp(startTime, nameof(startTime), out var start, out var startError))
        {
            return BadRequest(startError);
        }

        if (!TryParseOptionalTimestamp(endTime, nameof(endTime), out var end, out var endError))
        {
            return BadRequest(endError);
        }

        var query = _db.AttendanceLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(sn))
        {
            query = query.Where(a => a.DeviceSerialNumber == sn);
        }
        if (employeeId is not null)
        {
            query = query.Where(a => a.EmployeeId == employeeId);
        }
        if (start is not null)
        {
            query = query.Where(a => a.Timestamp >= start);
        }
        if (end is not null)
        {
            query = query.Where(a => a.Timestamp <= end);
        }

        var logs = await query
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .ToListAsync();

        // PunchTypeLabel.ToText is plain C#, not SQL-translatable -- applied here, after
        // the query above has already materialized (at most `limit`) rows, rather than
        // inside the LINQ query itself.
        var results = logs.Select(a => new
        {
            a.Id,
            a.EmployeeId,
            a.Timestamp,
            a.PunchType,
            PunchTypeText = PunchTypeLabel.ToText(a.PunchType),
            Source = a.Source.ToString(),
            a.DeviceSerialNumber,
            // UTC, not Philippine local -- SqlAttendanceLogRepository.AddLogsAsync sets this
            // via DateTime.UtcNow. Timestamp above (the punch itself) is local; this is the
            // one column here that isn't, unlike AdmsServer's now-retired AttendancePunch
            // model, where both of its equivalents were local. Worth keeping in mind if this
            // is ever compared side-by-side against Timestamp.
            a.ImportedAt
        });

        return Ok(results);
    }

    [HttpPost("devices/{sn}/command")]
    public async Task<IActionResult> QueueCommand(string sn)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var commandText = (await reader.ReadToEndAsync()).Trim();
        if (string.IsNullOrEmpty(commandText))
        {
            return BadRequest("Request body must contain the command text, e.g. CHECK or REBOOT.");
        }

        var state = _devices.GetOrAdd(sn);
        var id = state.NextCommandId();
        state.PendingCommands.Enqueue((id, commandText));

        _logger.LogInformation("Queued command {CommandId} for SN={SerialNumber}: {CommandText}", id, sn, commandText);

        return Ok(new { QueuedForDevice = sn, CommandId = id, commandText });
    }

    /// <summary>
    /// Convenience wrapper around the command above: queues the specific ADMS command that
    /// makes a device re-push its stored attendance history, so you don't have to
    /// remember/retype the raw protocol string. Restarting this listener does NOT do this
    /// automatically -- restarting only resets what the LISTENER remembers, it doesn't
    /// instruct the device to do anything. The device only reacts to explicit commands like
    /// this one.
    ///
    /// Safe to call repeatedly: duplicate punches are detected and skipped at the database
    /// layer (see SqlAttendanceLogRepository.AddLogsAsync), so re-running this won't leave
    /// you with duplicate rows for punches you already have.
    ///
    /// Note: this asks the device for what it currently has stored -- if the device (or a
    /// prior manual action) already deleted old records, they're gone and no command can
    /// bring back data that no longer exists on it.
    ///
    /// Optional startTime/endTime query params scope the request to a specific period (spec
    /// section 12.1.3) instead of asking for everything. Both must be in
    /// "yyyy-MM-dd HH:mm:ss" format (the same format ATTLOG timestamps use). Omit either/both
    /// to fall back to a wide "everything" range.
    /// </summary>
    [HttpPost("devices/{sn}/resync-attendance")]
    public IActionResult ResyncAttendance(string sn, [FromQuery] string? startTime, [FromQuery] string? endTime)
    {
        var start = string.IsNullOrWhiteSpace(startTime) ? WideRangeStart : startTime.Trim();
        var end = string.IsNullOrWhiteSpace(endTime) ? WideRangeEnd : endTime.Trim();

        if (!DateTime.TryParseExact(start, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !DateTime.TryParseExact(end, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return BadRequest($"startTime/endTime must be in '{TimestampFormat}' format, e.g. 2026-06-01 00:00:00.");
        }

        var state = _devices.GetOrAdd(sn);
        var id = state.NextCommandId();
        // Spec section 12.1.3: DATA QUERY ATTLOG requires StartTime/EndTime (tab-separated).
        var resyncCommand = $"DATA QUERY ATTLOG StartTime={start}\tEndTime={end}";
        state.PendingCommands.Enqueue((id, resyncCommand));

        _logger.LogInformation("Queued resync for SN={SerialNumber}: {Command}", sn, resyncCommand);

        return Ok(new
        {
            QueuedForDevice = sn,
            CommandId = id,
            Command = resyncCommand,
            RangeStart = start,
            RangeEnd = end,
            Note = "Device will pick this up on its next /iclock/getrequest poll (per the Delay=10s handshake setting) and re-push attendance records in the given range."
        });
    }

    /// <summary>
    /// The spec's own documented resync mechanism (section 12.3.1), distinct from the DATA
    /// QUERY approach above: reset the device's ATTLOG/OPERLOG stamps to 0, then queue a
    /// CHECK command. The device re-reads configuration (sees ATTLOGStamp=0 on its next
    /// handshake-equivalent check) and re-uploads everything.
    /// </summary>
    [HttpPost("devices/{sn}/force-recheck")]
    public IActionResult ForceRecheck(string sn)
    {
        var state = _devices.GetOrAdd(sn);
        state.AttLogStamp = 0;
        state.OperLogStamp = 0;
        var id = state.NextCommandId();
        const string checkCommand = "CHECK";
        state.PendingCommands.Enqueue((id, checkCommand));

        _logger.LogInformation("Force-recheck queued for SN={SerialNumber}: stamps reset, CommandId={CommandId}", sn, id);

        return Ok(new
        {
            QueuedForDevice = sn,
            CommandId = id,
            Command = checkCommand,
            Note = "Stamps reset to 0 and a CHECK command queued -- the device should treat this like a fresh connection and re-upload its full history."
        });
    }

    private static bool TryParseOptionalTimestamp(string? value, string paramName, out DateTime? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTime.TryParseExact(value.Trim(), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            error = $"{paramName} must be in '{TimestampFormat}' format, e.g. 2026-06-01 00:00:00.";
            return false;
        }

        parsed = result;
        return true;
    }
}
