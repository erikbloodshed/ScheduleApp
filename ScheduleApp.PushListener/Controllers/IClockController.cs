using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using ScheduleApp.Core.Attendance;
using ScheduleApp.PushListener.Services;

namespace ScheduleApp.PushListener.Controllers;

/// <summary>
/// Implements ZKTeco's ADMS ("push") protocol, all under /iclock/ -- ported from the
/// AdmsServer prototype. The device calls these endpoints; there's no "connect to device IP"
/// step on this server's side at all. See docs/ZKTeco-Attendance-PUSH-Communication-Protocol-v3.7.pdf
/// for the spec this follows; section numbers referenced below are from that document.
///
/// The one substantive change from AdmsServer: DataUpload below writes into ScheduleApp's own
/// AttendanceLog via IAttendanceLogRepository.AddLogsAsync, tagged Source=Adms, instead of
/// AdmsServer's separate AttendancePunch/AttendanceDbContext -- so a pushed punch lands in the
/// exact same AttendanceLogs table (and the exact same dedup index) that "Import Punch Log"
/// and "Fetch from Device" already write to. See ZkTecoAttendanceLogReader in
/// ScheduleApp.Data/Attendance for the pull-path equivalent this mirrors.
///
/// Logs via ILogger, not Console.WriteLine -- the latter never reaches the Windows Event Log
/// provider Program.cs registers for a service-hosted run, so it would silently go nowhere
/// once this is actually deployed as a service (see this project's README).
/// </summary>
[ApiController]
[Route("iclock")]
public class IClockController(
    DeviceRegistry devices,
    RawUploadLogger rawLog,
    IAttendanceLogRepository attendanceLogs,
    ILogger<IClockController> logger)
    : ControllerBase
{
    private const string PlainText = "text/plain";

    private readonly DeviceRegistry _devices = devices;
    private readonly RawUploadLogger _rawLog = rawLog;
    private readonly IAttendanceLogRepository _attendanceLogs = attendanceLogs;
    private readonly ILogger<IClockController> _logger = logger;

    /// <summary>
    /// 1) Handshake — GET /iclock/cdata?SN=...&amp;options=all&amp;pushver=...
    /// The very first thing a device does when it powers on (or reconnects) is call this with
    /// options=all. We must answer with a plain-text config block telling it how often to
    /// talk to us and that we want real-time push.
    /// </summary>
    [HttpGet("cdata")]
    public IActionResult Handshake([FromQuery(Name = "SN")] string? sn)
    {
        if (string.IsNullOrWhiteSpace(sn))
        {
            return Content("ERROR\r\n", PlainText);
        }

        var state = _devices.GetOrAdd(sn);
        state.LastSeenUtc = DateTime.UtcNow;

        _logger.LogInformation("Handshake from SN={SerialNumber}, remote={RemoteAddress}",
            sn, HttpContext.Connection.RemoteIpAddress);

        var response = new StringBuilder()
            .Append("GET OPTION FROM: ").Append(sn).Append("\r\n")
            // Spec (section 5) uses per-data-type stamp fields, not a generic
            // "Stamp"/"OpStamp" pair — corrected per the official PUSH SDK doc in docs/.
            // Untested firmware may not accept the generic names.
            .Append("ATTLOGStamp=").Append(state.AttLogStamp).Append("\r\n")
            .Append("OPERLOGStamp=").Append(state.OperLogStamp).Append("\r\n")
            .Append("ErrorDelay=30\r\n")
            .Append("Delay=10\r\n")                 // seconds between the device's routine check-ins
            .Append("TransTimes=00:00;23:59\r\n")   // upload window(s) — all day
            .Append("TransInterval=1\r\n")          // minutes between batched uploads
                                                    // Format II per spec section 5 — "During new server development: only
                                                    // Format II needs to be supported." Tab-separated data type names,
                                                    // matching the spec's own worked example exactly.
            .Append("TransFlag=TransData AttLog\tOpLog\tAttPhoto\tEnrollUser\tChgUser\tEnrollFP\tChgFP\tUserPic\r\n")
            // UTC+8 (Philippines) — same offset the rest of ScheduleApp already assumes for
            // punch timestamps (see ZkTecoAttendanceLogReader). Adjust if this ever runs
            // somewhere else. Spec: -12 < TimeZone < 12 is a whole-hour offset (section 5).
            .Append("TimeZone=8\r\n")
            .Append("Realtime=1\r\n")               // push new punches immediately, don't batch
            .Append("Encrypt=0\r\n");

        return Content(response.ToString(), PlainText);
    }

    /// <summary>
    /// 2) Data upload — POST /iclock/cdata?SN=...&amp;table=ATTLOG&amp;Stamp=...
    /// Body is tab-separated, one record per line. For ATTLOG:
    /// PIN &lt;TAB&gt; yyyy-MM-dd HH:mm:ss &lt;TAB&gt; Status &lt;TAB&gt; VerifyMethod &lt;TAB&gt; WorkCode
    /// e.g. 1001\t2026-07-08 08:01:15\t0\t1\t0
    /// The device only cares that the HTTP response body contains "OK".
    /// </summary>
    [HttpPost("cdata")]
    public async Task<IActionResult> DataUpload([FromQuery(Name = "SN")] string? sn, [FromQuery] string? table)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(sn))
        {
            return Content("ERROR\r\n", PlainText);
        }

        var state = _devices.GetOrAdd(sn);
        state.LastSeenUtc = DateTime.UtcNow;

        _rawLog.Append($"POST cdata SN={sn} table={table}", body);

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var processed = 0;

        if (string.IsNullOrEmpty(table) || table.Equals("ATTLOG", StringComparison.OrdinalIgnoreCase))
        {
            // Collected into one batch and written with a single AddLogsAsync call below,
            // rather than one call per line -- the same reasoning as ZkTecoAttendanceLogReader's
            // fetch: AddLogsAsync's dedup check is one query per batch, not one per row, and a
            // device's periodic push can legitimately carry more than one line at a time.
            var batch = new List<AttendanceLog>(lines.Length);
            var skippedNonNumericPins = 0;

            foreach (var line in lines)
            {
                var fields = line.Split('\t');
                if (fields.Length < 2)
                {
                    continue;
                }

                var pin = fields[0].Trim();
                if (!DateTime.TryParse(fields[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var punchTime))
                {
                    continue;
                }

                // Same PIN -> EmployeeId contract as ZkTecoAttendanceLogReader (the pull-path
                // equivalent): Employee.Pin is always stored as an int, so a PIN that
                // isn't a plain integer can never match an employee and is skipped -- reported
                // below, not silently imported as garbage -- rather than guessed at.
                if (!int.TryParse(pin, out var employeeId))
                {
                    skippedNonNumericPins++;
                    continue;
                }

                // fields[2] ("Status" in the spec's ATTLOG layout) is passed straight through
                // as PunchType, unchanged from the raw device value -- deliberately NOT
                // translated or clamped here. Multiple independent ADMS server
                // implementations agree on 0=Check In, 1=Check Out, 2=Break Out, 3=Break In,
                // 4=Overtime In, 5=Overtime Out for this field (see PunchTypeLabel in
                // ScheduleApp.Core, which now maps all six for display), and the tab-separated
                // PIN/Timestamp/Status/VerifyMode/WorkCode field order below matches those
                // same sources -- but neither has been confirmed against this app's own
                // device/firmware or its protocol PDF. That's lower-stakes than it sounds:
                // PunchMatching (ScheduleApp.Attendance) matches a punch to a shift purely by
                // Timestamp + Source, never PunchType, so an unexpected status value here
                // can't corrupt a hours calculation -- worst case it shows as
                // "Unknown (n)" in the grid/export until PunchTypeLabel is taught that value.
                var status = fields.Length > 2 && int.TryParse(fields[2], out var s) ? s : 0;

                batch.Add(new AttendanceLog
                {
                    EmployeeId = employeeId,
                    Timestamp = punchTime,
                    PunchType = status,
                    Source = AttendanceLogSource.Adms,
                    DeviceSerialNumber = sn
                });
                processed++;
            }

            if (batch.Count > 0)
            {
                var result = await _attendanceLogs.AddLogsAsync(batch);
                // distinctStatuses is here specifically so confirming the Status->PunchType
                // mapping against a real device is a glance at this log line, rather than
                // grepping raw_uploads.log by hand -- if this only ever shows {0,1} in
                // practice, PunchTypeLabel's wider 2-5 cases are simply dead code for this
                // device/firmware, which is a fine outcome too.
                var distinctStatuses = batch.Select(l => l.PunchType).Distinct().OrderBy(v => v);
                _logger.LogInformation(
                    "ATTLOG SN={SerialNumber} received={Received} new={New} duplicate={Duplicate} " +
                    "skippedNonNumericPin={SkippedNonNumericPin} statuses=[{Statuses}]",
                    sn, batch.Count, result.NewRecords, result.DuplicateRecords, skippedNonNumericPins,
                    string.Join(",", distinctStatuses));
            }
            else if (skippedNonNumericPins > 0)
            {
                _logger.LogInformation(
                    "ATTLOG SN={SerialNumber} received=0 usable records, skippedNonNumericPin={SkippedNonNumericPin}",
                    sn, skippedNonNumericPins);
            }

            state.AttLogStamp += processed;
        }
        else
        {
            // OPERLOG / BIODATA / USERINFO etc. — not parsed by this listener, but still
            // captured in raw_uploads.log above so nothing is silently lost.
            processed = lines.Length;
            state.OperLogStamp += processed;
        }

        return Content($"OK: {processed}\r\n", PlainText);
    }

    /// <summary>
    /// 3) Command polling — GET /iclock/getrequest?SN=...
    /// The device periodically asks "anything for me to do?". We reply "OK" if nothing is
    /// queued, or "C:&lt;id&gt;:&lt;command text&gt;" if something was queued via the admin
    /// API's /admin/devices/{sn}/command, /resync-attendance, or /force-recheck.
    /// </summary>
    [HttpGet("getrequest")]
    public IActionResult Poll([FromQuery(Name = "SN")] string? sn)
    {
        if (string.IsNullOrWhiteSpace(sn))
        {
            return Content("OK\r\n", PlainText);
        }

        var state = _devices.GetOrAdd(sn);
        state.LastSeenUtc = DateTime.UtcNow;

        if (state.PendingCommands.TryDequeue(out var cmd))
        {
            _logger.LogInformation("Sending queued command to SN={SerialNumber}: C:{CommandId}:{CommandText}",
                sn, cmd.Id, cmd.Text);
            return Content($"C:{cmd.Id}:{cmd.Text}\r\n", PlainText);
        }

        return Content("OK\r\n", PlainText);
    }

    /// <summary>
    /// 4) Command result — POST /iclock/devicecmd?SN=... body:
    /// ID=1&amp;Return=0&amp;CMD=CHECK
    /// The device reports back whether the command we queued succeeded.
    /// </summary>
    [HttpPost("devicecmd")]
    public async Task<IActionResult> CommandResult([FromQuery(Name = "SN")] string? sn)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        _rawLog.Append($"POST devicecmd SN={sn}", body);
        _logger.LogInformation("Command result from SN={SerialNumber}: {Body}", sn, body);

        return Content("OK\r\n", PlainText);
    }

    /// <summary>
    /// 5) Biometric / photo upload (stub) — POST /iclock/fdata?SN=...
    /// Some devices push fingerprint templates or capture photos here. This listener just
    /// notes that something arrived; extend this if you actually need to store templates or
    /// photos.
    /// </summary>
    [HttpPost("fdata")]
    public async Task<IActionResult> BiometricUpload([FromQuery(Name = "SN")] string? sn)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        _logger.LogInformation("fdata upload from SN={SerialNumber}: {ByteCount} bytes (not stored)",
            sn, ms.Length);
        return Content("OK\r\n", PlainText);
    }

    /// <summary>
    /// Heartbeat — /iclock/ping?SN=... (spec section 10). Used by the client to keep the
    /// connection alive during large uploads. The spec's own request/annotation example is
    /// internally inconsistent about GET vs POST (shows a GET request line but labels it
    /// "HTTP request method: POST method"), so both are accepted here rather than guessing
    /// wrong.
    /// </summary>
    [HttpGet("ping")]
    [HttpPost("ping")]
    public IActionResult Ping([FromQuery(Name = "SN")] string? sn)
    {
        _logger.LogDebug("Ping from SN={SerialNumber}", sn);
        return Content("OK\r\n", PlainText);
    }
}
