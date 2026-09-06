# ScheduleApp.PushListener

Implements ZKTeco's ADMS ("push") protocol so a terminal can push attendance punches
directly into ScheduleApp's own database, as an alternative to the existing pull-based
"Fetch from Device" and file-based "Import Punch Log" paths in ScheduleApp.Desktop.

This is a standalone ASP.NET Core app, not a WPF window or a background thread inside
ScheduleApp.Desktop. It's meant to run continuously (see **Deployment** below), independent
of whether anyone has ScheduleApp.Desktop open. See the diagram from planning: the terminal
pushes to this process; this process writes to the same `AttendanceLogs` table
ScheduleApp.Desktop already reads; the two processes never talk to each other directly.

Ported from the standalone `AdmsServer` prototype -- the wire protocol is unchanged, but
`IClockController.DataUpload` now writes into `ScheduleApp.Core.Attendance.AttendanceLog` via
`IAttendanceLogRepository.AddLogsAsync` (tagged `Source = Adms`), instead of AdmsServer's own
separate `AttendancePunch`/`AttendanceDbContext`. That's the one thing that actually changed;
everything else here is the same protocol handling AdmsServer already had working.

## What's done (this pass)

- Project scaffolded, referencing `ScheduleApp.Core` and `ScheduleApp.Data` directly -- no
  separate database, no separate `AttendancePunch` model.
- `IClockController` ported: handshake, data upload (ATTLOG), command poll, command result,
  biometric-upload stub, ping.
- `DataUpload` parses ATTLOG lines into `AttendanceLog` and writes them in one
  `AddLogsAsync` batch call per upload -- same dedup path (`EmployeeId`, `Timestamp`,
  `PunchType` unique index) every other punch source already goes through.
- `DeviceRegistry` / `DeviceState` / `RawUploadLogger` ported unchanged.
- Configurable listen port and connection string (`appsettings.json`), rather than
  hardcoded like the AdmsServer prototype was.
- Registered as a Windows Service host (`UseWindowsService()`) so it can be installed to
  survive reboots (see **Deployment**).
- Logs via `ILogger`, mirrored to the Windows Event Log when actually running as a service
  (see **Windows Service logging** below) -- not `Console.WriteLine`, which goes nowhere
  once there's no console attached.
- Startup check that warns (doesn't apply) if `ScheduleAppDb` has pending EF Core migrations
  -- this project deliberately never calls `Database.Migrate()` itself; ScheduleApp.Desktop
  owns the schema.

## Status update (Phase 3 -- ATTLOG status mapping)

The ATTLOG `Status` field is still passed through to `PunchType` unchanged (see
`IClockController.DataUpload`), but two things changed:

- The tab-separated field order this code parses (`PIN\tTimestamp\tStatus\tVerifyMode\tWorkCode`)
  and the 0=Check In/1=Check Out/2=Break Out/3=Break In/4=Overtime In/5=Overtime Out status
  convention are both corroborated by multiple independent, real-world ADMS server
  implementations -- not just this app's own guess. Still not confirmed against this app's
  own device/firmware or its protocol PDF, but no longer resting on one assumption alone.
- `ScheduleApp.Core.Attendance.PunchTypeLabel` now maps all six of those values for display
  (grid + Excel export), replacing the old `== 0 ? "Clock In" : "Clock Out"` checks that
  collapsed anything else into "Clock Out." Turns out this only ever mattered for display:
  `PunchMatching` (ScheduleApp.Attendance) matches a punch to a shift by `Timestamp` and
  `Source` alone -- it never reads `PunchType` -- so nothing about hours/overtime calculation
  depended on this mapping being right. Worst case if it's ever wrong: a punch shows
  `"Unknown (n)"` in the grid until `PunchTypeLabel` is taught that value; the reported hours
  for that day are unaffected either way.
- The ATTLOG log line now includes every distinct status value seen in a batch -- the
  fastest way to confirm what this device is actually sending once it's live.

## Status update (Windows Service logging)

A separate, updated build of the old `AdmsServer` prototype (moved from LocalDB to a real
SQL Server Express instance, plus a full Windows Service deployment writeup) surfaced a real
gap here: `Console.WriteLine` -- which is what every log line in this project used until now
-- never reaches a service's Windows Event Log provider, because it bypasses
`Microsoft.Extensions.Logging` entirely. Registering `AddEventLog` in `Program.cs` (as this
project already did) doesn't help if nothing actually logs through `ILogger`.

Fixed by:
- Injecting `ILogger<IClockController>` into `IClockController` and using it for every log
  line, instead of `Console.WriteLine`.
- Injecting `ILogger<Program>` in `Program.cs` for its own startup/warning messages.
- Registering `builder.Logging.AddEventLog(...)` (source name
  `ScheduleAppPushListener`) only when `WindowsServiceHelpers.IsWindowsService()` is true --
  a no-op for local `dotnet run`, so nothing about local development changed.

One setup step this adds -- see **Deployment** step 2 below: the Event Log source has to
exist before a non-admin service account can write to it.

## Status update (Phase 4 -- concurrent-writer hardening)

`SqlAttendanceLogRepository.AddLogsAsync` (in `ScheduleApp.Data`, shared by every import
path -- File, Network, and this listener's Adms path) now survives a genuinely concurrent
writer instead of just this listener's own single-process case. Its pre-check (an in-memory
`seenKeys` set, loaded once per batch) can only ever see what had already committed at the
moment of that query -- it can't see a row this listener and, say, a Desktop-triggered
"Fetch from Device" both insert at effectively the same instant. Previously that collision
would throw a raw `DbUpdateException` out of `SaveChangesAsync`, and since `SaveChangesAsync`
wraps the whole batch in one transaction, it would silently roll back *every* row in that
batch -- not just the one that collided.

Fixed with a two-tier approach: the common case (no collision) is still exactly one
`SaveChangesAsync` round trip, unchanged. Only on an actual unique-constraint violation
(SQL error 2601/2627 -- the same check `AdmsServer`'s `AttendanceStore.TryRecordAsync` used
against its own, now-retired table) does it fall back to detaching and retrying the batch's
rows one at a time, so only the row(s) that actually lost the race end up counted as
duplicates -- every other row in the batch still lands.

This lives entirely in `ScheduleApp.Data`, not in this project -- nothing about
`IClockController` changed to get this; it was already just calling `AddLogsAsync` like every
other caller.

## Status update (admin API + Control Panel)

A more complete reference build of the old `AdmsServer` prototype (a full admin API, plus a
standalone WPF Control Panel) turned out to be mostly storage-agnostic: everything except one
endpoint only ever touched `DeviceRegistry`/`DeviceState`, which this project already had.

- **`AdminController`** ported (`GET /admin/devices`, `GET /admin/attendance`, `POST
  /admin/devices/{sn}/command`, `/resync-attendance`, `/force-recheck`). `GetDevices` and the
  three command endpoints are unchanged from the reference build. `GetAttendance` is the one
  rewritten -- it queries `ScheduleDbContext.AttendanceLogs` directly (not through
  `IAttendanceLogRepository`, which has no filtered/paged query -- widening that shared
  interface just for a diagnostic endpoint seemed like the wrong tradeoff), matches `pin`
  against the numeric `EmployeeId` the same way `IClockController.DataUpload` does, and
  includes `PunchTypeText` via `PunchTypeLabel` in its response.
- **`ScheduleApp.PushListener.ControlPanel`** -- a new WPF project, peer to this one, ported
  from the reference build's own Control Panel. Pure `HttpClient` over the admin API above, no
  project reference to this listener or to `ScheduleApp.Core`/`ScheduleApp.Data` -- same
  decoupling as the original had, since in a real deployment it'll usually run on a different
  machine from the headless listener. This is "Option 3" from the original planning
  conversation (a real status check against the listener process itself, not just derived
  from the database) -- and, per that conversation, this is now the app's actual answer to
  "how do I check on this." Option 2 (a small "Last push: [time] from [SN]" line inside
  ScheduleApp.Desktop's Attendance tab, derived from `AttendanceLogs` alone) was considered
  and explicitly decided against in favor of this -- nothing on the Desktop side needs
  touching for push status; this Control Panel is it.

One reshaping worth knowing about if you're comparing against the reference build: its
`AttendancePunchInfo` (`Pin`/`DeviceSerial`/`Status`/`VerifyMethod`/`WorkCode`/
`ReceivedAtLocal`) doesn't line up field-for-field with this project's `AttendanceLogInfo`.
`ScheduleApp.Core.Attendance.AttendanceLog` never stored `VerifyMethod` or `WorkCode` --
`IClockController.DataUpload` parses them off the wire but only `Status` (as `PunchType`)
makes it into the database -- so the Control Panel's Attendance grid doesn't have those two
columns. And `ImportedAt` (standing in for `ReceivedAtLocal`) is UTC, not Philippine local --
see the property comment on `AttendanceLogInfo` for why.

## Where this leaves the original plan

Every open item from the original planning conversation is now resolved: the handshake/data
upload/poll protocol (Phase 1-2), the ATTLOG status mapping (Phase 3), concurrent-writer
safety in `AddLogsAsync` (Phase 4), Windows Service logging, and the admin API + Control
Panel as the app's answer to "how do I check on this" (Option 3, over Option 2). What's left
from here on is deployment and testing against a real device, not further design decisions --
see **Deployment** below.

## Running locally

```
dotnet run --project ScheduleApp.PushListener
```

Listens on `http://0.0.0.0:80` by default -- set via `Push:ListenUrl` in `appsettings.json`
(the code itself falls back to 8080 only if that key is ever removed entirely, so don't rely
on that fallback matching what's actually deployed). Binding port 80 typically needs an
elevated/admin shell for a plain `dotnet run` on Windows; if you hit a permission error
locally, either run elevated or drop `Push:ListenUrl` down to something like
`http://0.0.0.0:8080` in your own `appsettings.Development.json` (and update
`ScheduleApp.PushListener.http`'s host line to match, since it's hardcoded to port 80 too).

Use `ScheduleApp.PushListener.http` to exercise the endpoints manually (VS Code's REST Client
extension or Visual Studio's built-in `.http` support both run these directly).

Requires `ScheduleAppDb`'s schema to already exist -- run ScheduleApp.Desktop at least once
first (it calls `Database.Migrate()` on startup), or run `dotnet ef database update` from
`ScheduleApp.Data` yourself. This project only ever reads/writes existing tables.

## Deployment (Windows Service, same machine as SQL Server Express)

### 1. Grant the service's account a SQL Server login

The app connects with Windows Authentication (`Trusted_Connection=True`), so whichever
Windows account the service runs as needs a login in SQL Server. Unlike `ScheduleApp.Desktop`
(which creates/migrates the schema, and unlike the old `AdmsServer` prototype, which called
`EnsureCreated()`), this listener only ever reads/writes rows in tables that already exist --
so it needs `db_datareader` + `db_datawriter` on `ScheduleAppDb`, not `dbcreator`/`db_owner`.

Run this in SQL Server Management Studio (or `sqlcmd`), connected to your SQLEXPRESS instance
as an admin. Example for the default **LocalSystem** account:

```sql
CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
USE ScheduleAppDb;
CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datareader ADD MEMBER [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datawriter ADD MEMBER [NT AUTHORITY\SYSTEM];
```

Substitute a dedicated service account's name instead if you're using one (recommended for
anything beyond a quick test -- see step 7).

### 2. Create the Windows Event Log source

Do this once, in an elevated PowerShell prompt -- a running service typically doesn't have
permission to create a new Event Log source itself:

```powershell
New-EventLog -LogName Application -Source "ScheduleAppPushListener"
```

### 3. Publish the app

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o C:\Services\ScheduleApp.PushListener
```

Drop `--self-contained true` (or set it to `false`) if the target machine already has the
ASP.NET Core 10 Runtime installed.

Set `Push:ListenUrl` in the published copy's `appsettings.json` to whatever port the
terminal's Comm -> Cloud Server (ADMS) setting expects (80 or 8080 are both common defaults --
check the device). `ConnectionStrings:ScheduleDb` can stay here too for a quick test, but for
a real deployment set it up once via the shared config file in step 4 instead, so it doesn't
also have to be hand-copied into `ScheduleApp.Desktop`'s `appsettings.json` and kept in sync.

### 4. Set up the shared config file (optional but recommended)

Both this project and `ScheduleApp.Desktop` read `ConnectionStrings:ScheduleDb` from their own
local `appsettings.json` by default. If the two are ever left to drift -- most commonly
because the database moved and only one side's file got updated -- this listener quietly
points at the wrong (or an unreachable) database while Desktop keeps working fine, so pushed
punches just stop showing up with nothing pointing at why.

Instead, create one shared file both processes load *after* their own `appsettings.json` (so
it wins if present), outside either project's publish folder so redeploying never touches it:

```powershell
New-Item -ItemType Directory -Force -Path C:\ProgramData\ScheduleApp | Out-Null
@'
{
  "ConnectionStrings": {
    "ScheduleDb": "Server=.\\SQLEXPRESS;Database=ScheduleAppDb;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
'@ | Set-Content -Encoding utf8 C:\ProgramData\ScheduleApp\shared.appsettings.json
```

That default path (`%ProgramData%\ScheduleApp\shared.appsettings.json`) is what
`ScheduleApp.Core.Configuration.SharedConfigFile` resolves to on both processes; it's
`%ProgramData%` rather than `%LocalAppData%` specifically because this service and
`ScheduleApp.Desktop` normally run as different Windows accounts, and `%ProgramData%` is the
one location both agree on regardless. Set the `SCHEDULEAPP_SHARED_CONFIG` environment
variable on both machines instead if you need the file somewhere else (e.g. it isn't
`%ProgramData%` on this deployment). Skipping this step entirely is fine too -- both apps keep
working exactly as before, off their own local `appsettings.json`, until the file exists.

**After this one-time setup**, `ScheduleApp.Desktop` has its own Settings dialog (the gear icon,
top right) that edits this same file -- connection string, Attendance device defaults, and
Attendance Policy -- without hand-editing JSON again. That dialog only writes the fields you
actually change, and only takes effect the next time each app starts.

That dialog runs as whichever Windows account is logged into Desktop, though, and
`%ProgramData%` isn't writable by standard accounts by default -- only the PowerShell snippet
above (run by whoever has rights to create the folder) is guaranteed to work as-is. If people
without admin rights need to use the Settings dialog themselves later, grant them write access
to the folder once, e.g.:

```powershell
icacls C:\ProgramData\ScheduleApp /grant "Users:(OI)(CI)M"
```

Adjust `Users` to whatever group should be able to change these settings on this machine --
anyone who can write to this file can change the database connection and device settings for
everyone else who runs Schedule Manager or Push Listener here, so don't grant it more broadly
than you actually want.

### 5. Install the service

```powershell
sc.exe create ScheduleAppPushListener binPath= "C:\Services\ScheduleApp.PushListener\ScheduleApp.PushListener.exe" start= auto DisplayName= "ScheduleApp Push Listener"
sc.exe description ScheduleAppPushListener "Receives ZKTeco ADMS push-protocol uploads and writes attendance punches into ScheduleAppDb."
```

(`sc.exe` requires a space *after* each `=` -- `start= auto`, not `start=auto`.)

### 6. Make it start after SQL Server Express

```powershell
sc.exe config ScheduleAppPushListener depend= 'MSSQL$SQLEXPRESS'
```

The single quotes around the service name are load-bearing in PowerShell, not optional
style -- unquoted (or double-quoted), PowerShell expands `$SQLEXPRESS` as a variable
reference before `sc.exe` ever sees it. Since no such variable exists, it silently
expands to nothing, so what actually reaches `sc.exe` is `depend= MSSQL`. That's a
dependency on a service that doesn't exist, and starting the service then fails with
`[SC] StartService FAILED 1075: The dependency service does not exist or has been marked
for deletion.` -- even if `MSSQL$SQLEXPRESS` is the exact right name. Single-quoting the
whole value prevents the expansion.

`MSSQL$SQLEXPRESS` is also just the service name for a SQL Server Express instance
literally named `SQLEXPRESS` (SQL Server's default instance name when you don't pick
one during setup). If yours is a differently-named instance, the default (unnamed)
instance, or a full SQL Server rather than Express, confirm the real service name first
with `Get-Service *MSSQL*` and substitute it (still single-quoted if it contains `$`) --
and make sure `Server=` in `appsettings.json`'s connection string matches the same
instance, or the service will start but fail to log in to SQL right after.

Without this dependency, the listener can start and try to connect before SQL Server is
ready, especially right after a reboot.

### 7. Configure the run-as account (optional but recommended)

The default, LocalSystem, works with no extra config -- it can already bind the configured
port and, once granted the SQL login in step 1, connect to the database. To run under a
dedicated least-privilege account instead:

```powershell
sc.exe config ScheduleAppPushListener obj= ".\svc-pushlistener" password= "..."
netsh http add urlacl url=http://+:8080/ user=".\svc-pushlistener"
```

The `urlacl` grant is required for any non-LocalSystem account to bind a port below 1024, and
still recommended above 1024 rather than granting to `Everyone`.

### 8. Configure restart-on-failure

Since this is the thing your time clocks phone home to, it's worth auto-restarting if it ever
crashes:

```powershell
sc.exe failure ScheduleAppPushListener reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

(Retries after 5s, 10s, then 30s; resets the failure count after a day of staying up.)

### 9. Open the firewall

```powershell
New-NetFirewallRule -DisplayName "ScheduleApp PushListener" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
```

(Match the port to whatever `Push:ListenUrl` uses.)

### 10. Start it

```powershell
sc.exe start ScheduleAppPushListener
```

### 11. Point the terminal at it

On the terminal itself: Comm -> Cloud Server (ADMS) -> enable, point Server Address at this
machine and Server Port at whatever you set in step 3.

## Verifying it's working

- **Event Viewer** -> Windows Logs -> Application, source `ScheduleAppPushListener` -- look
  for `Connected to ScheduleAppDb -- schema is up to date.` and, once a device is talking to
  it, `Handshake from SN=...` / `ATTLOG SN=...` entries.
- `logs/pushlistener-<date>.json` next to the published exe -- the same log lines as Event
  Viewer, as structured JSON (one object per line), rolled daily and kept for 31 days. Good
  for `grep`/`jq`-ing by `SerialNumber` or event type across more history than Event Viewer
  comfortably holds.
- `data/raw_uploads-<date>.log` next to the published exe -- every raw upload body, unparsed,
  as a second way to confirm what actually arrived. Rolls daily, kept for 30 days.
- Once confirmed, check the Punch Records grid in ScheduleApp.Desktop for rows with
  `Source = Adms`.
- Or open ScheduleApp.Desktop's new **Push Listener** tab (see below) and check its
  Devices/Attendance grids directly against this listener's admin API -- no Event Viewer or
  database access needed.

## Status update (Control Panel merged into ScheduleApp.Desktop)

`ScheduleApp.PushListener.ControlPanel` (the standalone WPF app described above) has been
folded into `ScheduleApp.Desktop` as a new **Push Listener** tab, following the same
"port it in, retire the standalone original" pattern already used for `AdmsServer`. The tab is
built the same way the standalone app was -- a pure `HttpClient` (`PushListenerApiClient`) over
this project's `/admin/*` endpoints, no project reference from `ScheduleApp.Desktop` to
`ScheduleApp.PushListener` itself -- so it can still point at a listener running on a
different machine (`PushListener:BaseUrl` in `ScheduleApp.Desktop/appsettings.json`, editable
in the tab itself too) exactly like the standalone app could.

`ScheduleApp.PushListener.ControlPanel` has since been removed from the solution -- it's no
longer needed now that the Desktop tab covers the same job.

## Status update (structured logging + log rotation)

A comparison against another, unrelated ADMS server implementation (a Go library, not part of
this codebase) surfaced two real gaps in what "Windows Service logging" above left in place:
neither the Windows Event Log sink nor `raw_uploads.log` gave a queryable, structured record,
and `raw_uploads.log` in particular had no rotation -- it would have grown forever on a service
meant to run unattended for months.

Fixed by:

- Replacing the `Microsoft.Extensions.Logging.AddEventLog(...)` setup with Serilog
  (`Serilog.AspNetCore` via `builder.Host.UseSerilog(...)` in `Program.cs`). Every existing
  `ILogger`/`_logger.LogInformation(...)` call site in `IClockController`, `AdminController`,
  and `Program.cs` is unchanged -- only the sinks receiving those calls changed.
- A new `logs/pushlistener-<date>.json` sink (`Serilog.Sinks.File` +
  `Serilog.Formatting.Compact`), rolling daily and by size (50 MB), retaining 31 files. Every
  structured field already passed to a `LogInformation` call (e.g. `SerialNumber`, `Received`,
  `Duplicate`) lands as a real JSON property, not just baked into a rendered message string.
- Event Log stays wired up via `Serilog.Sinks.EventLog` under the same
  `WindowsServiceHelpers.IsWindowsService()` condition as before, with `manageEventSource: false`
  -- the event source still has to exist first via the admin-run `New-EventLog` in **Deployment**
  step 2, same as always.
- `Serilog.MinimumLevel`/`Override` moved from appsettings.json's old `Logging` section into a
  `Serilog` section of the same shape, read via `ReadFrom.Configuration(...)` -- Development vs
  Production verbosity still works the same way, just under a new section name.
- `RawUploadLogger` now rolls to a new `data/raw_uploads-<date>.log` file each UTC day (was a
  single `data/raw_uploads.log`) and deletes files older than 30 days. Kept as its own file
  rather than folded into the Serilog pipeline -- raw device bodies are bulky, unparsed text
  that would clutter the structured JSON log if mixed in.

## Status update (SQL Server connection retries)

Users occasionally hit "Could not connect to SQL Server / apply migrations" on
ScheduleApp.Desktop's startup -- intermittently, not consistently, which points at a race with
SQL Server Express still starting up (and possibly SQL Server Browser, since the connection
string uses the named instance `.\SQLEXPRESS` rather than a fixed port) rather than a real
misconfiguration. Neither this project nor ScheduleApp.Desktop had any retry logic at all -- one
connection attempt, first failure wins.

Fixed by enabling EF Core's `EnableRetryOnFailure` on both projects' `UseSqlServer(...)` call
(5 retries, up to 10s backoff). That alone is not enough, though: `Migrate()` (in
ScheduleApp.Desktop) and `GetPendingMigrationsAsync()` (here) are both APIs that do not
automatically go through the configured execution strategy (a known EF Core limitation --
[dotnet/efcore#27450](https://github.com/dotnet/efcore/issues/27450)), so each call site also
wraps itself explicitly in `db.Database.CreateExecutionStrategy()` to actually get retried.
Confirmed safe to combine with `SqlAttendanceLogRepository.AddLogsAsync`'s existing
duplicate-key handling: neither this nor ScheduleApp.Desktop use an explicit
`BeginTransaction()` anywhere (which the retry strategy does not support without extra wrapping
of its own), and if a retried `SaveChangesAsync()` ever did re-attempt a batch that had actually
already committed, it would hit the same unique-constraint violation `AddLogsAsync` already
catches and treats as a duplicate -- not a new failure mode.

## Updating / redeploying

```powershell
sc.exe stop ScheduleAppPushListener
# replace files in C:\Services\ScheduleApp.PushListener with the new publish output
sc.exe start ScheduleAppPushListener
```

`ScheduleAppDb` and `data\raw_uploads.log` are untouched by this -- only the app binaries are
replaced.

## Uninstalling

```powershell
sc.exe stop ScheduleAppPushListener
sc.exe delete ScheduleAppPushListener
```

This does not remove the SQL Server login, the Event Log source, or the `data` folder --
clean those up separately if you want a full removal.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Service starts then immediately stops | Check Event Viewer, Application log, sources `ScheduleAppPushListener` and `.NET Runtime`, for the actual exception. |
| `Login failed for user ...` | The service's run-as account doesn't have a SQL Server login yet, or wasn't granted `db_datareader`/`db_datawriter` on `ScheduleAppDb` (step 1). |
| Nothing in Event Viewer at all | The event source wasn't created (step 2) -- `AddEventLog` silently no-ops if it can't write. |
| `pending EF Core migration(s)` warning at startup | `ScheduleApp.Desktop` (or `dotnet ef database update`) hasn't been run against this database yet -- this listener never applies migrations itself. |
| Port already in use | Something else is bound to the configured port -- check with `netstat -ano \| findstr :<port>`. |
| Device never shows up as `Source = Adms` rows in Desktop | Confirm the device's configured server IP/port matches this machine, and that firewall/antivirus aren't blocking the inbound port. |
| `[SC] StartService FAILED 1075: The dependency service does not exist...` | Step 6's `depend=` value wasn't single-quoted in PowerShell, so `$SQLEXPRESS` got expanded away to nothing before `sc.exe` saw it -- or the SQL Server service really is named something other than `MSSQL$SQLEXPRESS` on this machine. See step 6. |

## Retiring the old AdmsServer prototype

Done -- the standalone `AdmsServer` project has been removed from the solution now that this
project is confirmed working. Its job is fully absorbed here; keeping a second,
DB-disconnected implementation of the same protocol around afterward would have been a
maintenance trap, not a safety net.