# Schedule App

A .NET 10 WPF desktop app for managing employee schedules, backed by SQL Server
Express, with import/export to an Excel layout compatible with the original
spreadsheet-based workflow this replaced.

> **Pay rules live in [PAYROLL-POLICY.md](PAYROLL-POLICY.md)** -- every rate and
> multiplier the app applies, in one place, alongside where each still differs
> from Philippine labor law. The payroll sections below cover how those rules are
> wired up and configured; that document covers what they actually pay.

## Solution layout

```
ScheduleApp.slnx
ScheduleApp.Core        Plain models: Department, Employee, ScheduleEntry, ScheduleType,
                        plus the Attendance domain models (AttendanceLog, ManualAttendanceLog,
                        AttendancePolicy, AttendanceSummary, PunchStatus) and the Users domain
                        (UserAccount, PasswordHasher) -- see "Signing in" below
ScheduleApp.ZkTeco      Standalone client for the ZKTeco/ZKSoftware device wire protocol (TCP/UDP
                        port 4370) -- connects to a terminal like the MB560-VL directly, with no
                        dependency on anything else in this solution. See Data/Attendance/
                        ZkTecoAttendanceLogReader for the adapter that turns its output into
                        AttendanceLog rows.
ScheduleApp.Data        EF Core DbContext + repository (SQL Server) -- includes an AttendanceLogs
                        table (see Data/Attendance) that punches are imported into from either a
                        .dat file or a direct network fetch from the terminal (see "Attendance"
                        below), so reports run against the database, not either source, every time
ScheduleApp.Excel       Import/export using EPPlus, compatible with the legacy workbook layout,
                        plus the attendance summary/punch-log Excel exporter
ScheduleApp.Attendance  Attendance calculation engine: matches punches against schedule and
                        produces AttendanceSummary rows
ScheduleApp.Desktop     The WPF app -- a left-side navigation drawer, not tabs (see
                        MainWindow.xaml's NavigationView). Attendance is an expandable
                        submenu of its own three pages -- Summary, Punch Records, Manual
                        Entries (AttendanceSummaryPage/PunchRecordsPage/ManualEntriesPage) --
                        sharing one AttendanceViewModel; every other item (Schedule,
                        Employees, Payroll, Push Listener) is a single page
```

## Signing in

Schedule Manager now has its own login -- a `UserAccounts` table
(`ScheduleApp.Core.Users.UserAccount`), separate from `Employee`, which still just
represents staff being scheduled/tracked for attendance, not people who use this app.
There's only one kind of account -- no separate admin/viewer roles -- so anyone who can
sign in can do everything the app could already do.

- **First run** (a freshly migrated database with no accounts yet) shows
  `SetupAdminWindow` instead of the ordinary login screen, since there's nothing to
  sign in to yet -- enter an account name and password (at least 8 characters) and
  that becomes the first account.
- **Every later run** shows `LoginWindow` -- enter the account name and password. A
  wrong password, an account that doesn't exist, and an account that's been
  deactivated (see below) all show the same "Invalid account name or password."
  message, so a login attempt can't be used to discover which of the three it was.
- **Manage Users** (the 👤 button next to Backup in the main window's top-right corner --
  Settings itself now lives at the bottom of the navigation drawer, not that row) is where
  more accounts get added after the first one, along with resetting a password,
  deactivating/reactivating an account, or deleting one outright. Two guard rails
  are enforced there so it can't lock the app out of itself: you can't
  deactivate/delete the account you're currently signed in as, and you can't
  deactivate/delete the last remaining *active* account, full stop -- either would
  leave nothing left that a future login could ever succeed against.

Passwords are hashed with PBKDF2-HMAC-SHA256 (`ScheduleApp.Core.Users.PasswordHasher`,
210,000 iterations, random salt per account) -- nothing in this app ever stores a
plaintext password. **There's no "forgot password" recovery flow inside the app** -- see
"Recovering from a locked-out account" below for what to do if that happens.

## Recovering from a locked-out account

A password can never be *retrieved* -- only PBKDF2 hashes are stored (see "Signing in"
above), and that's one-way by design, so there's nothing to look up even with direct
database access. The only thing that's ever possible is *resetting* it to something new.

### Someone else can still sign in

Easiest case. Have them open **Manage Users** (the 👤 button, next to Backup)
and click **Reset Password** on the locked-out account. No need for the old password at
all -- see "Signing in" above.

### Nobody can sign in at all

This needs going around the app directly, in SQL Server. Two options, depending on
whether you want to start over completely or fix just one account:

**Option A -- reset every account and start over.** Fastest, and fine if there's no
real reason to keep the old account names around:

1. Close Schedule Manager on every machine that has it open (and stop the Push
   Listener service, if one's installed, so nothing else is holding the database open).
2. In SQL Server Management Studio (or `sqlcmd`), connect to the SQL Server Express
   instance hosting `ScheduleAppDb` -- the same one named in `ConnectionStrings:ScheduleDb`
   (`appsettings.json`, or the shared config file if that's set up -- see "Signing in"
   above) -- and run:
   ```sql
   DELETE FROM ScheduleAppDb.dbo.UserAccounts;
   ```
3. Relaunch Schedule Manager. With `UserAccounts` empty, it shows the first-run "create
   the first account" screen (`SetupAdminWindow`) again, exactly like a brand-new
   install -- see "Signing in" above.
4. Once signed in, use **Manage Users** to re-add anyone else who needs an account.

Nothing else is touched -- schedules, employees, and attendance history are in
different tables and don't go anywhere.

**Option B -- reset one specific account's password, leave every other account alone.**
There's no PBKDF2 function built into plain SQL, so a valid hash can't be written with
just a SQL statement -- it has to be generated first with the same code the app itself
uses (`PasswordHasher`), then saved with an `UPDATE`:

1. Close Schedule Manager everywhere, same as step 1 above.
2. On a machine with the .NET SDK and a copy of this repo, create a small throwaway
   console project that references `ScheduleApp.Core`:
   ```bash
   dotnet new console -o ResetPasswordTool
   cd ResetPasswordTool
   dotnet add reference ../ScheduleApp.Core/ScheduleApp.Core.csproj
   ```
3. Replace `Program.cs` with:
   ```csharp
   using ScheduleApp.Core.Users;

   var (hash, salt) = PasswordHasher.Hash("the-new-password-goes-here");
   Console.WriteLine($"PasswordHash: {hash}");
   Console.WriteLine($"PasswordSalt: {salt}");
   ```
4. Run it and copy the two printed values:
   ```bash
   dotnet run
   ```
5. In SQL Server Management Studio (or `sqlcmd`), against `ScheduleAppDb`:
   ```sql
   UPDATE ScheduleAppDb.dbo.UserAccounts
   SET PasswordHash = N'<paste PasswordHash here>',
       PasswordSalt = N'<paste PasswordSalt here>'
   WHERE Username = N'the-account-name';
   ```
6. Delete the `ResetPasswordTool` folder -- it was only ever a one-time script, not
   part of the app, and it briefly held the new password in plain text in `Program.cs`
   and the console output.
7. Relaunch Schedule Manager and sign in with the new password.

Both options need direct SQL Server access -- whoever set up the connection string in
the first place (see "Prerequisites"/"First-time setup" below) is the right person to
ask if that's not you.

## Attendance


Punch logs identify employees by the punch clock's own employee code, which
matches against `Employee.LegacyId` (the "Employee ID" field in the Add/Edit
Employee dialog) -- **not** `Employee.Id`, which is just the SQL
auto-increment primary key and has no relationship to the punch clock.
`AttendanceWorkflowService` matches on `LegacyId` for this reason:

- Every employee you want attendance reports for needs their `LegacyId` set
  to match their punch clock code. Employees without one are skipped
  (reported in the run log), not shown as Absent every day -- being absent
  from a report they were never going to match correctly seemed better than
  a wall of false Absents.
- If your punch clock's codes ever diverge from what's on file for an
  employee (e.g. the clock was reconfigured, or codes were reused), the
  match will be silently wrong rather than erroring -- worth spot-checking a
  report against a couple of employees you know the hours for after your
  first real run.

Punches are persisted to the database instead of being re-read from the .dat
file (or re-fetched from the device) on every report -- **getting punches into
the database** and **Generate Reports** are two separate steps:

- **Import Punch Log** reads a selected `.dat` file and adds any punches not
  already stored (matched by Employee ID + timestamp + punch type, via a
  unique index -- see `ScheduleDbContext`). Safe to run repeatedly on the same
  or an overlapping export; already-known punches are skipped, not duplicated.
- **Fetch from Device** does the same thing, but connects directly to the
  ZKTeco terminal over the network instead of requiring someone to export a
  `.dat` file to USB first -- see "Getting punches straight from the ZKTeco
  terminal" below.
- **Generate Reports** reads only from the database. It does *not* touch the
  `.dat` file or the device, so **you need to import/fetch at least once
  before the first report** -- if `AttendanceLogs` is empty for the period,
  everyone will show as Absent, not because the data doesn't exist but
  because it hasn't been imported yet.

This also means punch history now survives independently of whatever the
ZKTeco device itself retains, and a report for a given period only ever
queries a bounded range of punches *and* schedule entries -- not the entire
history every time. Both are padded around the requested period by the
widest configured buffer (plus a day, for a shift crossing midnight): punches,
so a shift near the edge of the period can still find a punch just outside it;
schedule entries, so a shift *dated* just outside the period (e.g. an
overnight shift scheduled the day before the period starts, whose clock-out
lands the next morning inside it) can still claim/consider that punch, rather
than it falling out as Unscheduled just because the schedule entry that should
have looked at it wasn't loaded. A schedule entry pulled in only by this
padding never surfaces as a report row (or the "no Employee ID" warning) for a
day nobody asked about -- it exists purely so its buffer window can do its
matching job. See `AttendanceWorkflowService` for both.

### Getting punches straight from the ZKTeco terminal

The Attendance section's Punch Records page has a **Fetch from Device** button (next to **Import…**) that talks
to the terminal directly over the network -- no USB export, no `.dat` file --
using the same proprietary wire protocol ZKTeco's own SDKs and well-known
open-source clients (e.g. `pyzk`) use, on TCP or UDP port 4370. This was
built and validated against a real MB560-VL as a standalone connectivity
test first (see `ScheduleApp.ZkTeco`) before being wired into the Attendance
tab; `ZkTecoAttendanceLogReader` (in `ScheduleApp.Data/Attendance`) is the
thin adapter that turns its output into `AttendanceLog` rows and hands them
to the same `IAttendanceLogRepository.AddLogsAsync` that **Import Punch
Log** uses -- so the two are interchangeable and safe to mix: a punch
already on file from one path is just a duplicate of the same punch from
the other, not a conflict.

What you need:

- **Device IP** -- check Menu → Comm → Ethernet on the terminal itself.
- **Port** -- defaults to 4370, the standard ZKTeco port; almost never needs
  to change.
- **Comm key** -- the device's communication password (Menu → Comm →
  Ethernet/Comm Key). `0` (no password) is the out-of-the-box default on
  most units.
- **Transport** -- TCP by default; check **Use UDP** only if the terminal is
  configured for UDP-only communication (some firmware/installs default to
  one or the other).

These can be pre-filled from the `Attendance` section of `appsettings.json`
(`DeviceIp`, `DevicePort`, `DeviceCommKey`, `DeviceTransport`), the same way
`LogDatFile` already is -- still editable in the tab either way, and a
blank/missing section is fine, not fatal.

A few things worth knowing about how this differs from the `.dat` path:

- **The device protocol has no server-side date-range filter.** `Fetch from
  Device` always pulls the terminal's *entire* stored attendance log, every
  time -- there's no way to ask for "only punches after X" over the wire.
  This matches how other clients (`pyzk` included) handle it too: fetch
  everything, then let the de-duplication in `AddLogsAsync` sort out what's
  actually new. `AddLogsAsync` checks what's already stored with one query
  covering the whole batch's timestamp range (not one lookup per punch --
  that was the original approach, fine for a few hundred rows from a manual
  `.dat` import, but a real capture that came back with over 15,000 records
  from a single device is exactly the scale where a per-row round trip
  starts to matter), so the transfer time itself -- not the de-duplication
  -- is what scales with a large on-device log.
- **The stored `Source` column reads `Network`** for punches that came in
  this way (`File` for `.dat` imports, `Adms` reserved for a possible future
  push-listener) -- visible in the Punch Records tab, so you can tell which
  path a given punch arrived through.
- **`DeviceSerialNumber` is now populated** for these rows (read from the
  terminal via `CMD_OPTIONS_RRQ` during the same fetch), which the `.dat`
  path has never set since a plain text export doesn't carry it.
- **A device user id that isn't a plain integer is skipped, not imported as
  garbage** -- same "skip and report, don't guess" stance the app already
  takes for schedule entries with no `LegacyId` (see above). The run log
  reports how many were skipped, if any.
- **This is a live network connection to production hardware**, not a file
  read -- a slow link, a device mid-export to someone else, or a firewall
  blocking port 4370 will surface as an error message rather than a report
  full of Absents. If it fails, double-check the IP with a ping and confirm
  port 4370 isn't blocked between this machine and the terminal before
  assuming the comm key is wrong.

The Attendance section also has a **Punch Records** page (a submenu item next
to **Summary** and **Manual Entries** in the navigation drawer) --
unrelated to Generate Reports, it just shows (and, via its own **Export…**
button, saves to Excel) whatever's currently in `AttendanceLogs` for a plain
date range (no buffer padding, no schedule matching), with an optional
Employee ID filter. Useful for confirming an import or device fetch actually
landed, or handing someone a punch-log export without generating (or
touching the scope of) a full attendance summary.

### The Summary tab's counts strip: clickable tiles, and exporting just one

Above the results grid, the Summary tab shows a row of counts -- Complete,
Partial, Absent, Leave, plus **Orphaned** and **Unscheduled**:

- **Complete/Partial/Absent/Leave** count distinct scheduled *days*, not rows
  (see "One row per day was a real assumption elsewhere" below for why a
  segmented Flexible day needed its own day-level status).
- **Orphaned** is a punch that landed inside some schedule entry's buffer
  window but wasn't the one actually picked as its clock-in/out -- e.g. a
  duplicate device tap a minute after the real clock-in, or a manual entry
  that lost out to a device punch for the same slot. Near a schedule, just not
  the punch that got used.
- **Unscheduled** is a punch no schedule entry this run even considered at
  all -- a punch on a day with no schedule for that employee, or under a
  legacy ID that doesn't match any employee/schedule entry in the system
  whatsoever (a stale device enrollment, for instance). See
  `AttendanceWorkflowService.RunAsync` for exactly how the two are told apart.

Every tile is clickable and opens a small read-only dialog listing just that
subset -- `AttendanceStatusDetailDialog` for the four day-status tiles,
`PunchListDetailDialog` for Orphaned/Unscheduled. Each dialog has its own
**Export…** button that writes only what's showing in that dialog out to its
own workbook (via `AttendanceExcelExporter.ExportSummaryToExcel`/
`ExportLogsToExcel`, the same calls the Summary tab's own **Export Summary…**
and the Punch Records tab's **Export…** use), rather than needing a full
**Export Summary…** and then manually filtering rows afterward. It's disabled
when the tile's own count is zero, and defaults the save-file name to
`Attendance_<Status>_<date>.xlsx` (or `Attendance_Orphaned_<date>.xlsx` /
`Attendance_Unscheduled_<date>.xlsx`).

### Manual entries, for a punch that never happened

Sometimes an employee just forgets to badge in or out, and there's nothing
for **Import Punch Log**/**Fetch from Device** to ever pick up -- no device
record exists to import. The **Add Manual Entry…** button, on the Manual
Entries page, covers that case: pick the employee, date, time, and
Clock In/Clock Out, give a required reason (e.g. "Forgot to badge in"), and
it's saved.

A few things worth knowing about how this differs from a real punch:

- **Manual entries live in their own `ManualAttendanceLogs` table**, not
  `AttendanceLogs` -- see `ManualAttendanceLog`'s doc comment. `AttendanceLogs`
  stays an untouched record of what the punch clock (or a `.dat`/network
  import of it) actually reported; a hand-typed correction living in the same
  table would blur that. This also means a manual entry can be deleted (via
  the Punch Records grid's Delete button, shown only on manual rows) if it
  was entered by mistake -- something you can't do to a real device punch.
- **A manual entry only fills in a clock-in/out the device side has nothing
  for -- it never overrides a device punch that's already there.** If both
  exist for the same slot, the device punch wins, silently. See
  `ScheduleApp.Attendance.PunchMatching` for where that's enforced (for a
  Normal shift or a segmented Flexible day) and
  `FlexibleShiftCalculationStrategy.CalculateUnrestrictedDay` for the
  segment-less Flexible equivalent, which applies the same preference at the
  whole-day level since it has no per-slot window to fall back within.
- **Shows up alongside device punches everywhere a punch list does** -- the
  Punch Records grid and its Export… both merge the two tables together
  (tagged `Source = Manual`), and a Generate Reports run does the same before
  matching (see `AttendanceWorkflowService`). There's no separate "manual
  entries" report to remember to also check.
- **Flagged wherever it ends up in a report**, so a manually-typed time is
  never mistaken for something the clock actually recorded: a trailing `*`
  on the WPF results grid's Clock In/Clock Out (hover the column header), and
  italic text plus a cell comment ("Manually entered -- see Punch Records for
  who and why") in the exported workbook -- see
  `AttendanceSummary.ClockInIsManual`/`ClockOutIsManual`.

### Flexible days with punching windows get their own per-window result

A Flexible day with no `FlexibleSegments` configured (see "The schedule
model" below) still works the way it always has: every punch that day counts,
paired off sequentially, and compared against the day's single required-hours
total.

A Flexible day that *does* have one or more configured segments is different:
each segment is now matched against punches independently -- its own
+/-buffer search window around its own start (`FlexibleSegmentClockInBuffer`)
and end (`FlexibleSegmentClockOutBuffer`), each defaulting to +/-1 hour, tuned
separately from the full-day Normal-shift buffers since a segment is usually
much shorter -- and produces its own `AttendanceSummary`, with its own
Late/Early/Overtime figures computed exactly like a Normal shift's single
window. In other words, a Flexible day with two segments (e.g. a split shift)
now generates two report rows for that day, not one. `AttendanceExcelExporter`
gained a **Type** column (Normal/Leave/Flexible) so a report mixing both kinds
of Flexible row -- segmented and segment-less -- still shows plainly which
Remain/Overtime model applies to each one. See
`ScheduleApp.Attendance/FlexibleShiftCalculationStrategy.cs` for the full
reasoning, particularly why each segment searches two independent windows
instead of one combined one.

**One row per day was a real assumption elsewhere, not just an implementation
detail.** `AttendanceViewModel`'s results panel and `AttendanceExcelExporter`'s
per-employee/grand totals both used to count Status directly across every
`AttendanceSummary`, which quietly meant "count every day" back when that was
the same thing as "count every row." A segmented Flexible day breaks that: two
segments both Complete now used to count as two Complete days for one actual
day of work, and a day with one segment Complete and the other Absent used to
count toward *both* buckets at once. `ScheduleApp.Core.Attendance.AttendanceDayStatus`
fixes this -- it groups by `(EmployeeId, ShiftDate)` first and collapses each
day's segment statuses into one (all-Complete -> Complete, all-Absent ->
Absent, anything mixed -> Partial, the same meaning Partial already has for a
single Normal shift with only one punch found), and both the WPF results panel
and the exported workbook's "XC | YP | ZA" total lines now go through it, so
they can't drift apart from each other. One side effect worth knowing: the
workbook's total line used to be a live Excel formula (`COUNTIFS` over the
Status column), so editing a Status cell by hand and recalculating would
update it; it's now a literal value written at export time, since there's no
practical way to express "count distinct days, not rows" as a formula over
that column. Given the sheet is generated, not meant for manual edits, this
tradeoff is the right one.

## Payroll: Overtime and Night Differential

`PayrollCalculator` (`ScheduleApp.Payroll/PayrollCalculator.cs`) pays Overtime
and Night Differential hours as **premium percentages on top of Hourly Rate**
(`Employee.DailyRate / PayrollPolicy.StandardHoursPerDay`), each gated on an
eligibility flag, with per-day overrides available for both the eligibility
and the percentage itself:

```
overtime payment = 0.00, unless overtime is eligible for the day, in which case:
    overtime payment = overtime hours * hourly rate
    if the overtime rate percentage premium applies (a separate toggle, see below):
        overtime payment *= 1 + overtime rate percentage

night diff payment = 0.00, unless night diff is eligible for the day, in which case:
    night diff payment = night diff hours * hourly rate * night diff rate percentage
```

**Settings hierarchy** -- each of these resolves per-day override, then
per-employee default, then (for the two rate percentages) the global
`PayrollPolicy` default:

```
Overtime Rate %:         ScheduleEntry.OvertimeRatePercentageOverride  ->  PayrollPolicy.OvertimeRatePercentage
Night Diff Rate %:       ScheduleEntry.NightDiffRatePercentageOverride ->  PayrollPolicy.NightDiffRatePercentage

Overtime Eligibility:    ScheduleEntry.OvertimeEligibleOverride    ->  Employee.QualifiesForOvertime
Overtime "apply rate %": ScheduleEntry.ApplyOvertimeRatePercentageOverride -> Employee.ApplyOvertimeRatePercentageByDefault
Night Diff Eligibility:  ScheduleEntry.NightDiffEligibleOverride   ->  Employee.QualifiesForNightDiff
```

`PayrollPolicy.OvertimeRatePercentage` defaults to `0.25` (a 25% premium,
i.e. `hourly rate * 1.25`) and `PayrollPolicy.NightDiffRatePercentage`
defaults to `0.10`. Both are premium-only percentages, not full multipliers --
this used to be asymmetric (`OvertimePayMultiplier` was a full `1.25`
multiplier while `NightDiffPayMultiplier` was already premium-only at `0.10`);
if you're upgrading from a version that still has
`Payroll:Policy:OvertimePayMultiplier` in `appsettings.json`, change it to
`OvertimeRatePercentage: 0.25` -- leaving the old `1.25` value in place under
the new key would 5x every overtime premium.

**Overtime has three real states, not two.** Unlike Night Diff (which is
either eligible, at its full rate percentage, or not eligible at all),
Overtime has an independent second toggle -- `Employee.ApplyOvertimeRatePercentageByDefault`
/ `ScheduleEntry.ApplyOvertimeRatePercentageOverride` -- for whether the rate
percentage premium actually applies once an employee is eligible:

| State | Eligible? | Rate % applies? | Result |
|---|---|---|---|
| Not entitled to overtime pay (e.g. a manager, PH Labor Code Art. 82) | No | -- | ₱0.00 |
| Eligible, paid straight time (e.g. a temporary company policy) | Yes | No | `hours * hourly rate`, no premium |
| Eligible, paid the labor-law-compliant premium | Yes | Yes | `hours * hourly rate * (1 + overtime rate %)` |

This lets an employee stay eligible for overtime throughout a transition
between the second and third rows above -- flipping only the "apply rate %"
toggle, per day or date range, without touching eligibility itself.

**Where these live in the UI.** The per-employee defaults
(`QualifiesForOvertime`/`QualifiesForNightDiff`/`ApplyOvertimeRatePercentageByDefault`)
are set from the Add/Edit Employee dialog. The per-day overrides are set from
the Set/Edit Schedule dialog's "Overtime / Night differential overrides"
section (visible for Normal and Flexible days only -- Leave and Official
Business never generate overtime or night diff hours in the first place, see
`OfficialBusinessShiftCalculationStrategy`'s own doc comment) -- eligibility
and the apply-rate-% toggle are each a tri-state "Use employee default /
On / Off" choice, and the two rate percentages are optional overrides shown
grayed-out at the current policy default, the same pattern already used for
the dialog's clock-in/clock-out buffer overrides. Any field left untouched
falls through to the next tier above.

## Payroll: Rest Day rates

A Rest Day that's actually worked is paid in **two tiers**, following
Philippine labor law -- the first eight hours at a premium, everything past
eight at a further premium on top of *that* rate:

```
rest day payment = 0.00, unless the day is a worked Rest Day, in which case:
    rest day rate = hourly rate * (1 + rest day premium)

    first PayrollPolicy.StandardHoursPerDay hours:
        payment += rest day rate * hours
    hours beyond that:
        payment += rest day rate * (1 + PayrollPolicy.RestDayOvertimeRatePercentage) * hours
```

At the defaults (30% and 30%) that's **130%** for the first eight hours and
**169%** beyond -- note the second tier *compounds*, `1.30 × 1.30`, rather than
adding the two premiums to reach 160%.

**Where the premium comes from.** Two tiers, same shape as the buffer defaults:

| | |
|---|---|
| `Employee.RestDayWorkPremiumPercentage` | This employee's override. Blank (null) means "inherit". |
| `PayrollPolicy.RestDayPremiumPercentage` | The company default, `0.30`. Set in Settings → Payroll. |

`RestDayOvertimeRatePercentage` is global only -- there's no per-employee or
per-day override for it, because a Rest Day never produces
`AttendanceSummary.OvertimeHours` in the first place (see
`RestDayShiftCalculationStrategy`); the split is made in Payroll, not
Attendance, so there's nothing for a per-day overtime override to attach to.

**Night differential on a Rest Day** is taken against the Rest Day rate, not
the base hourly rate -- so 10% of 130%, not 10% of 100%. All night hours use
the *first-tier* rate even on a day that ran past eight hours: attendance
records only a total night-hours figure, with nothing saying which of those
hours fell past the eighth, so there's nothing to allocate against.

The Rest Day Pay line shows the split when there is one -- `Rest Day Pay
(8.00H + 3.00H OT)` -- so both tiers can be multiplied back out by hand from
what's printed.

## Payroll: Holiday premium

A worked Holiday's day component (Holiday Pay's flat "one extra day" -- see
`PayrollCalculator.CalculateHolidayPay`) is a configurable premium rather than
a hardcoded full day:

```
holiday day component = day rate * holiday premium
```

Same two-tier resolution as the Rest Day premium above:

| | |
|---|---|
| `Employee.HolidayPremiumPercentage` | This employee's override. Blank (null) means "inherit". |
| `PayrollPolicy.HolidayPremiumPercentage` | The company default, `1.00`. Set in Settings → Payroll. |

`1.00` reproduces the app's original behavior exactly (`day rate * 1.00 == day
rate`, one full extra day, PH law's 200% worked-regular-holiday rate). The
premium applies equally to the Monthly-rated-only "listed Holiday not worked
at all" case -- it isn't only for the worked-day branch. It does **not**
apply to the day's Overtime/Night Diff "copies" (see that method's own
`KNOWN GAP` comment), which stay a flat doubling of the day's ordinary OT/ND
pay regardless of what this premium is set to.

## Payroll: Undertime exemption and Work Time default

For a daily-rated employee whose day doesn't need to be completed to earn a
full day's pay -- only exceeded to earn Overtime -- Edit Employee's Payroll
tab has an **Exempt from Undertime Deduction** checkbox
(`Employee.ExemptFromUndertimeDeduction`, default off). Basic Pay was already
a flat rate per credited day regardless of hours actually worked (see
`PayrollCalculator.BasicPayForDay`); the only thing that could still cost such
an employee money for running short was the separate Undertime deduction
line. Checking this reuses the same `PayrollLineItem.Waived`/
`PayrollResult.TotalDeductions` machinery a person's manual per-period
"disregard" already goes through (see `IPayrollUndertimeWaiverRepository`) --
the Undertime figure keeps showing on the payslip for reference, it just
never subtracts, regardless of that period's own manual waiver toggle.
`PayrollCalculator.Calculate` ORs the two together
(`undertimeWaived || employee.ExemptFromUndertimeDeduction`) rather than one
replacing the other, so a person can still waive one period's Undertime by
hand for an otherwise-ordinary employee. Overtime is unaffected either way --
hours worked past the day's scheduled Work Time are still Overtime, exactly
like any other employee.

Edit Employee's Attendance tab pairs this with a **Default work time
(hours)** field (`Employee.DefaultWorkTimeHours`, blank = company default).
Unlike the Clock-in/Clock-out buffer defaults just below it, this isn't a
genuine three-tier runtime resolution -- `ScheduleEntry.WorkTimeHours` is a
required, concrete value once a day is saved, so there's no per-day "blank,
inheriting" state for this to sit above. It's a one-time starting suggestion:
Apply Schedule's Work Time field pre-fills from it (in place of
`AttendanceSettings.DefaultWorkTimeHours`) for a brand-new entry, whenever
every selected employee shares the same resolved value, so scheduling someone
whose normal day is genuinely shorter or longer than most doesn't mean
re-typing that number by hand every time.

Migration `AddEmployeeWorkTimeAndUndertimeExemption` adds both columns --
`DefaultWorkTimeHours` nullable `decimal(5,2)`, `ExemptFromUndertimeDeduction`
`bit NOT NULL DEFAULT 0` -- no backfill needed, since false/null reproduce
every existing employee's current behavior exactly.

## Payroll: Rest Day Pay and Premium Pay Eligibility

Rest Day Pay and Premium Pay are opt-in per employee, the same way Overtime
and Night Differential are (see above) -- an employee only earns either one
if their own eligibility flag is on:

```
Rest Day Pay:  Employee.QualifiesForRestDayPay  -> gates the Rest Day Pay line
Premium Pay:   Employee.QualifiesForPremiumPay  -> gates the Premium Pay adjustment
```

Both default to `false` for every employee -- unlike `QualifiesForOvertime`/
`QualifiesForNightDiff` above, which default to `true` -- so neither pay type
is available to anyone until turned on for them individually (see "If you're
upgrading" below).

**What each flag actually gates.** Both pay types already had a numeric
setting behind them: `Employee.RestDayWorkPremiumPercentage` (the premium %
paid when a Rest Day is worked) and `Employee.DefaultPremiumPay` (a
per-period Premium Pay adjustment amount, like Allowance/Cash Advance). The
two eligibility flags don't replace those numbers -- they gate whether the
numbers are ever usable at all:

- **`QualifiesForRestDayPay`** controls whether "Scheduled duty" can be
  checked for a Rest Day in the Set/Edit Schedule dialog
  (`RestDayDutyCheckBox` in `ApplyScheduleDialog`) -- only a scheduled duty
  day can ever earn the Rest Day premium. An ineligible employee can still
  be scheduled an ordinary, unpaid Rest Day (their weekly day off); the
  checkbox is just disabled, with a tooltip explaining why, and
  force-unchecked if it was already checked for a selection that no longer
  qualifies.
- **`QualifiesForPremiumPay`** controls whether a `PremiumHoliday`
  adjustment row is seeded each period
  (`PayrollComputationService.ContributionDefaultsFor`) and whether the
  Premium Pay card appears at all in the Payroll Summary view and on the
  payslip.

**Where it's enforced.** `PayrollCalculator.Calculate` is the single place
both `PayrollSummaryView` and the payslip (`PayslipLineBuilder`) read from,
so gating happens there once: the Rest Day Pay line is omitted from
`PayrollResult.ComputedGrossPay` when `QualifiesForRestDayPay` is false, and
the `PremiumHoliday` group is omitted from
`PayrollResult.GrossPayAdjustmentGroups` when `QualifiesForPremiumPay` is
false -- not shown at ₱0.00, left out entirely. `TotalGrossPay`/`NetPay`
exclude both amounts for an ineligible employee automatically, since they
just sum whatever lines/groups happen to be present.

**Live-read, not a snapshot.** Same as `QualifiesForOvertime`/
`QualifiesForNightDiff` above -- toggling either flag takes effect
immediately for every period, past and present, not just future ones.
Nothing in this app freezes a payroll result at print time.

**Non-destructive.** Turning a flag off never deletes anything already on
file. An existing `PremiumHoliday` adjustment row is just excluded from the
Payroll Summary/payslip/totals while the flag is off, not removed -- it
reappears exactly as it was the moment the flag is turned back on, and the
same goes for a Rest Day already marked as scheduled duty.

**Where these live in the UI.** Both flags are set from the Add/Edit
Employee dialog -- "Rest Day Pay Eligibility" and "Premium Pay Eligibility,"
right after Night Diff Eligibility. Checking one reveals the numeric field
it gates (the Rest Day premium % box, or the Premium Pay amount box);
unchecking it hides that field, though the underlying value is still saved,
not cleared, so it's there again if eligibility is re-enabled later.

## The schedule model: one row per employee per day

`ScheduleEntry` represents a single employee's schedule for a single calendar
day (`Date`), not a date range. There's a unique `(EmployeeId, Date)` index in
the database, so a day either has no schedule or exactly one -- never two
competing ones to reconcile. Setting a schedule for a day always **replaces**
whatever was there before for that day; there's no separate "override" concept.

`ScheduleType` has three values: **Normal**, **Leave**, and **Flexible**. (An
earlier version of this app had a fourth type, "Temporary," meant to override
an ongoing Normal schedule for a few days -- that's no longer needed, since
every day is already independent. See "Importing older workbooks" below for
how existing data with Temporary rows is handled.)

- **Normal** has one continuous scheduled window (`TimeIn` + `WorkTimeHours`).
  Punches are matched against that window with configurable buffers -- see
  `SingleWindowShiftCalculationStrategy`.
- **Leave** has neither `TimeIn` nor `WorkTimeHours` set. Punch matching is
  skipped entirely -- see `LeaveShiftCalculationStrategy`.
- **Flexible** has `WorkTimeHours` (the day's *required total*, not a shift
  length -- still a single number for the whole day even when the punching
  window below is split into several pieces) and, optionally, one or more
  `FlexibleSegments` -- each an independent `(TimeIn, TimeOut)` pair -- bounding
  *when* punches are allowed during the day (e.g. 05:00-09:00 and 13:00-17:00
  for a split shift). Unlike Normal, the combined length of those windows has
  no fixed relationship to `WorkTimeHours` (windows totaling 16 hours with only
  8 required hours inside them is a normal setup, not a mistake), so each
  segment is a real, independently stored row (`FlexibleSegment`, in a child
  table cascade-deleted with its `ScheduleEntry`) rather than derived. Punches
  outside every segment (or outside the whole calendar day, if there are no
  segments at all) don't count. Within whatever punches remain, every pair is
  paired off sequentially (1st=in, 2nd=out, 3rd=in, ...) and the worked total is
  the sum of each pair's duration, so an employee can clock in and out any
  number of times in a day. See `FlexibleShiftCalculationStrategy` for exactly
  how "Late"/"Early"/"Overtime" translate when there's no fixed clock-in/
  clock-out to measure against, and why `AttendanceExcelExporter` always writes
  literal values (not the live formula it uses for Normal) for Flexible rows --
  that formula only has room for one in/out pair per row. Segments must not
  overlap each other, and each one's `TimeOut` must be later than its own
  `TimeIn` -- windows that cross midnight aren't supported. Flexible entries
  with zero segments (including older rows saved before segments existed) keep
  the original "every punch that day counts" behavior rather than being
  retroactively treated as having no punches at all.

- **`TimeOut` is never stored for Normal.** It's a computed property, `TimeIn +
  WorkTimeHours` wrapped past midnight where needed (`ScheduleEntry.TimeOut`),
  so it can't drift out of sync. The exporter writes it back out as a real
  Excel formula rather than a literal value. (For Flexible, the analogous
  concept -- each window's end -- is a `FlexibleSegment.TimeOut` instead, a
  genuinely stored value, since it doesn't derive from anything. `TimeIn` on
  `ScheduleEntry` itself is Normal-only; Flexible doesn't use it at all.)
- Shifts that run past midnight (e.g. `19:00` + 10 hours = `05:00` the next day)
  are handled with `TimeOnly.Add(TimeSpan, out int wrappedDays)`;
  `ScheduleEntry.CrossesMidnight` flags these for the UI. (Normal only --
  Flexible segments can't cross midnight in the first place, see above.)
- Each `Employee`'s `FirstName` column in the export actually holds
  `"LastName; FirstName"`, matching the original workbook's convention --
  `Employee.DisplayName` reproduces that exactly.
- Employees can be unassigned (`Employee.DepartmentId` is nullable) and show up
  under a synthetic **"(Unassigned)"** node in the tree.

## The UI: the calendar *is* the schedule view

There's no separate grid or list of schedule entries -- the calendar itself
shows each day's schedule directly (type, hours, time in/out, color-coded).
Setting a schedule works by selecting days on the calendar, then choosing
what to apply:

- **Click** a day to select just it.
- **Shift+click, or click-and-drag,** for a contiguous range.
- **Ctrl+click** to add/remove individual days, including non-contiguous ones
  (e.g. picking out every other Friday).
- **"Set Schedule for Selected Days"** opens a small dialog (showing what
  you've selected, grouped into readable ranges) where you pick a type
  (Normal/Leave/Flexible) and hours once, plus a time-in (Normal) or any number
  of punching windows (Flexible, added/removed with their own +/✕ buttons --
  zero is fine and means no restriction). It's applied to every selected day
  individually -- each one just becomes its own upserted row (with its own copy
  of whatever segments you configured, for Flexible).

  The button itself, and the dialog it opens, both react to what's already
  there: if every currently-selected day already has the exact same schedule
  (same type, hours, time-in, and for Flexible, the same punching windows),
  the button reads **"Edit Schedule for Selected Days"** and the dialog opens
  pre-filled with those values instead of the Normal/10-hour/05:00 default. If
  the selection has a mix -- some days scheduled and others not, or scheduled
  days that don't all match -- the button still says "Edit" (there's something
  in the selection you're about to overwrite), but the dialog starts blank
  rather than guessing, with a note explaining why.
- **"Remove Schedule for Selected Days"** deletes the schedule for whichever
  days are selected, back to "nothing set."
- **"Clear Selection"** deselects everything without changing any data.

## Views reset on every launch

The Schedule, Attendance, and Payroll tabs all start from their built-in
defaults every time the app launches -- no employee/department selected and
the current month for Schedule; the current cutoff period for Attendance's
Report, Punch Records, and Manual Entries sub-tabs alike, plus Summary as the
open sub-tab; the current cutoff period for Payroll. Nothing about where you
left off survives a close-and-reopen, on any tab, by design.

Switching tabs *within* a single running session is unaffected by this --
that already works on its own (`NavigationView` reuses the same cached
page/ViewModel instance each time you revisit a tab), so a Schedule
employee/month selection or an Attendance period/scope/search you set still
holds for the rest of that session, right up until you close the app.

This is deliberate, not a missing feature: `ViewStateStore` (Schedule and
Attendance's shared, in-memory "what's currently showing" holder) never
touches disk. It used to round-trip to a small JSON file at
`%LocalAppData%\ScheduleApp\viewstate.json` so the last-used view survived a
restart too, the same convenience "Remember me" on the sign-in page still
provides for credentials -- that's been removed specifically because a saved
*date range* surviving a restart is a correctness risk, not just a
convenience, on tabs whose whole job is "what's the current pay/report
period": reopening the app after the real-world date has crossed into the
next cutoff should always show *that* cutoff, never whichever half-month
happened to be on screen when the app last closed. Payroll's period never
had this problem in the first place (see `PayrollScopeState`'s own doc
comment) -- Attendance and Schedule now match it.

A couple of things were already never part of this even before the change:
multi-select mode on the Schedule tab (a transient "about to bulk-assign"
state) and the ZKTeco device connection fields (those come from
`appsettings.json`, not view state).

## Status bar: notifications, and Payroll Group progress

The thin bar along the bottom of the window (`Controls/StatusBarPresenter`,
handed to `IStatusBarService` once from `MainWindow`'s constructor) is the
single place every tab reports what it's doing, split into two independent
halves that can both be visible at once:

- **Right side -- notifications.** `ShowSuccess`/`ShowInfo`/`ShowCaution`/`ShowError`
  (`Services/StatusBarNotificationExtensions`) show an icon, title, and message,
  auto-clearing after a few seconds. Only one shows at a time -- a new call always
  replaces whatever's currently there and resets the clear timer.
- **Left side -- progress.** `IStatusBarService.ShowProgress`/`ClearProgress` drive a
  determinate progress bar (`Controls/PercentProgressBar`) whose "NN%" label rides
  along the top of the fill instead of sitting in a fixed spot. Right now the only
  thing driving it is the Payroll tab's Payroll Group table: loading a saved payroll
  run (`LoadPayrollGroupViewModel`) or adding employees to one recomputes each
  employee's Net Pay one at a time, and `PayrollViewModel`'s
  `PayrollGroupLoadPercent`/`IsPayrollGroupLoading` push each update to this bar for
  as long as that takes. (The Net Pay column header shows the same percentage as
  plain text too -- "Net Pay (Computing 45%)" -- so the number's visible even if
  you're not looking at the status bar. That header binds via
  `ElementName=PayrollGroupGrid` rather than `RelativeSource AncestorType=DataGrid`
  -- the latter is a common WPF pitfall for `DataGridColumn.Header` content, since
  the column itself isn't part of the visual tree; it left the header blank in
  practice rather than erroring, which is easy to miss until you're actually
  looking for the text.) Unlike the notification side, progress has no auto-clear
  timeout -- whoever's driving it is responsible for calling `ClearProgress` once
  its own work is done.

## Prerequisites

- .NET 10 SDK
- SQL Server Express (or LocalDB) reachable with the connection string in
  `ScheduleApp.Desktop/appsettings.json` (defaults to
  `Server=.\SQLEXPRESS;Database=ScheduleAppDb;Trusted_Connection=True;TrustServerCertificate=True;`)
- `dotnet tool install --global dotnet-ef` (one-time, for creating the database schema)

## First-time setup

Migrations are already committed to the repo (see `ScheduleApp.Data/Migrations`),
so a fresh clone just needs the schema applied, not a new `InitialCreate`:

```bash
dotnet restore

dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop

dotnet run --project ScheduleApp.Desktop
```

The app also calls `Database.Migrate()` on startup, so once the schema is
up to date, `dotnet run` alone is enough from then on -- including picking up
any migration added later. If you change the EF Core model yourself, generate
and commit a new migration before running the app:

```bash
dotnet ef migrations add <Name> --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

## If you're upgrading from an earlier version of this app

The schedule model changed shape: `ScheduleEntry.StartDate`/`EndDate` became a
single `Date`, there's a new unique `(EmployeeId, Date)` index, and
`ScheduleType.Temporary` was removed. If you already have a database from
before this change, you need a new migration:

```bash
dotnet ef migrations add PerDaySchedule --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

Old rows with `ScheduleType = Temporary` (value `2`) won't map to anything once
the enum only has `Normal`/`Leave` (`0`/`1`) -- if you have real data like that,
convert those rows to `Normal` before migrating, or just re-import from Excel
afterward (see below).

Separately, if you already have a database from before the unique index on
`Employee.LegacyId` was added (see "No concurrency handling" below), you need
one more migration:

```bash
dotnet ef migrations add UniqueLegacyId --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

If any existing employees already share a `LegacyId` (from before this was
enforced), that migration will fail to apply until you fix the duplicates by
hand -- the error message will tell you which value collided.

Separately, if you already have a database from before punches were persisted
to the database (see "Attendance" above), you need one more migration for the
new `AttendanceLogs` table:

```bash
dotnet ef migrations add AttendanceLogsTable --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

After that, use the Attendance tab's new **Import Punch Log** button at least
once before generating a report -- see "Attendance" above for why.

Separately, if you already have a database from before `ScheduleType.Flexible`
existed at all (i.e. `ScheduleType` only had Normal/Leave), you need one more
migration for the new type plus its `FlexibleSegments` table:

```bash
dotnet ef migrations add FlexibleSegments --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

There's no existing data to reconcile here -- Flexible didn't exist before this
migration, so there are no old Flexible rows of any shape to carry forward.
After applying it, `ScheduleType.Flexible` is simply available as a new option
the next time you use **Set Schedule for Selected Days** or import a workbook
with a `Flexible` row. Note that this migration alone doesn't change how a
Flexible day is *calculated* -- the per-segment behavior described in
"Flexible days with punching windows get their own per-window result" above
came later and needs no additional migration of its own, since
`FlexibleSegmentClockInBuffer`/`FlexibleSegmentClockOutBuffer` are plain
config values (see `appsettings.json`), not database columns.

Separately, if you already have a database from before manual attendance
entries existed (see "Manual entries, for a punch that never happened"
above), you need one more migration for the new `ManualAttendanceLogs` table:

```bash
dotnet ef migrations add ManualAttendanceLogs --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

There's no existing data to reconcile here either -- it's a brand new table,
not a reshaped one, so there are no old rows of any shape to carry forward.
After applying it, the **Add Manual Entry…** button on the Attendance tab is
simply available.

Separately, if you already have a database from before accounts existed (see
"Signing in" above), the migration for the new `UserAccounts` table is already
included in this codebase (`20260806120000_AddUserAccounts`) -- you just need to
apply it, not generate it:

```bash
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

There's no existing data to reconcile here either -- it's a brand new table. The
next time Schedule Manager starts against this database, it'll find `UserAccounts`
empty and show `SetupAdminWindow` to create the first account, exactly like a
brand new install would.

Separately, if you already have a database from before the Overtime/Night Diff
per-day overrides existed (see "Payroll: Overtime and Night Differential"
above), the migration for the new `Employees.ApplyOvertimeRatePercentageByDefault`
column and the five new `ScheduleEntries` override columns is already included
in this codebase (`20260811090000_AddOvertimeNightDiffPerDayOverrides`) -- you
just need to apply it, not generate it:

```bash
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

All six new columns are additive and nullable (or default to `true` for
`ApplyOvertimeRatePercentageByDefault`, matching the pre-migration behavior
exactly) -- every existing employee/schedule-entry row keeps behaving
identically until an override is actually set. Also update
`appsettings.json`'s `Payroll:Policy` section if you're upgrading from before
this revision -- see "Payroll: Overtime and Night Differential" above for the
`OvertimePayMultiplier` -> `OvertimeRatePercentage` rename and why the value
has to change too, not just the key.

Separately, if you already have a database from before the Rest Day Pay/
Premium Pay eligibility flags existed (see "Payroll: Rest Day Pay and
Premium Pay Eligibility" above), the migration for the new
`Employees.QualifiesForRestDayPay`/`QualifiesForPremiumPay` columns is
already included in this codebase
(`20260821090000_AddEmployeePayEligibilityFlags`) -- you just need to apply
it, not generate it:

```bash
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

Both new columns are additive and default to `false` -- a clean break, not
auto-derived from any `RestDayWorkPremiumPercentage`/`DefaultPremiumPay`
values already on file, so every existing employee needs Rest Day Pay and/or
Premium Pay eligibility turned back on by hand (Add/Edit Employee dialog) if
they should keep getting paid either one going forward. Nothing already
stored -- premium percentages, default amounts, existing `PremiumHoliday`
adjustment rows -- is touched by the migration itself; it's only ever
excluded from view while the corresponding flag is off, per "Payroll: Rest
Day Pay and Premium Pay Eligibility" above.

Separately, `AddRestDayPremiumOverride` turns
`Employee.RestDayWorkPremiumPercentage` into a nullable *override* column and
introduces a company-wide default behind it (see "Payroll: Rest Day rates"
above):

```bash
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

**This migration changes stored data, not just the schema**: every employee
whose premium is currently `0` is rewritten to `NULL`, so the new 30% company
default applies to them. That's deliberate -- a stored `0` meant "nobody ever
configured this" (the field was opt-in and started at 0), and leaving those
rows alone would have each of them read as a deliberate "0% premium" override
that suppresses the company default for the entire payroll, making the change
a no-op in production. An employee with a real non-zero premium keeps it
untouched.

The practical effect: **a worked Rest Day now pays 130% by default instead of
straight time**, and hours past the eighth pay 169%. If some employee genuinely
should be paid straight time for rest day work, type an explicit `0` into their
Rest Day Work Premium % box after migrating -- blank and `0` now mean different
things.

Separately again, `AddHolidayPremiumOverride` adds a new nullable
`Employee.HolidayPremiumPercentage` column (see "Payroll: Holiday premium"
above):

```bash
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

**No data changes here**, unlike the two migrations above -- this is a
brand-new column, not a conversion of an existing non-nullable one, so every
row is simply added as `NULL` (inherit the company default) with nothing to
correct. The default itself, `PayrollPolicy.HolidayPremiumPercentage = 1.00`,
reproduces the app's pre-existing behavior exactly, so this migration changes
no one's pay on its own -- it only makes the figure something Settings →
Payroll and Edit Employee can now change.

Separately again, `AddEmployeeWorkTimeAndUndertimeExemption` adds two new
`Employees` columns -- `DefaultWorkTimeHours` and `ExemptFromUndertimeDeduction`
(see "Payroll: Undertime exemption and Work Time default" above):

```bash
dotnet ef database update --project ScheduleApp.Data --startup-project ScheduleApp.Desktop
```

**No data changes here either** -- both are brand-new columns. `DefaultWorkTimeHours`
is added as `NULL` (no employee-level scheduling suggestion) and
`ExemptFromUndertimeDeduction` as `false` (Undertime deducts normally) for every
existing row, reproducing pre-migration behavior exactly. Nobody's pay or
schedule changes until someone deliberately turns either one on via Edit
Employee.

## Importing older workbooks

`ScheduleApp.Excel/ExcelScheduleImporter` still reads the original range-based
layout (`StartDate`/`EndDate` columns, one sheet per department) -- it expands
each row into one `ScheduleEntry` per day in that range. Rows are applied in
sheet order, so if a file has a "Temporary override" row listed *after* the
"Normal" baseline row for the same date (which is how the original spreadsheets
were laid out), the override's hours/time-in still win for that day -- it just
ends up stored as `Normal` with the override's values, since the type
distinction no longer exists. Any unrecognized `ScheduleType` text (including
old "Temporary" values) falls back to `Normal`.

If a baseline and override happen to be listed in the opposite order in some
file, the baseline would incorrectly win -- worth spot-checking override days
after importing an old file you're not sure about.

## Exporting

`ExcelScheduleExporter` writes one worksheet per department (plus an
"Unassigned" sheet if applicable), in the same `Id, FirstName, ScheduleType,
StartDate, EndDate, WorkTime, TimeIn, TimeOut` column layout as the original
workbook. Since storage is per-day now, the exporter collapses consecutive days
with identical type/hours/time-in/segment-set back into a single range row
purely for readability -- this is a display-time convenience only and has no
effect on how the data is stored or edited in the app. A Flexible run with
more than one punching window emits multiple rows sharing that same range (one
per window, reusing the `TimeIn`/`TimeOut` columns), since there's no separate
column layout for a variable number of windows.

## EPPlus license -- read before shipping this anywhere commercial

This project uses **EPPlus** for all Excel import/export. Unlike some
alternatives, **EPPlus is not free for commercial use** -- free use is limited
to non-commercial/personal/small-business scenarios under their Polyform
Noncommercial-based license; a for-profit deployment of this app likely needs a
paid EPPlus license. See https://epplussoftware.com/en/LicenseOverview and
confirm this applies to your situation before distributing the app.

`ScheduleApp.Excel/ExcelLicense.cs` centralizes the one line that configures
this (`ExcelPackage.LicenseContext = LicenseContext.NonCommercial;`). If your
installed EPPlus version is 7+/8+ it may have moved to a newer license API
(e.g. `ExcelPackage.License.SetNonCommercialPersonal(...)`) that no longer
exposes `LicenseContext` at all -- if that file doesn't compile, swap that one
line for whatever your installed version's docs show.

## Known gaps worth knowing about

- **No concurrency handling.** If this ever runs against the same SQL Server
  instance from more than one machine at once, two people setting a schedule
  for the same employee/day simultaneously will silently overwrite each other
  (no row-version/concurrency token in play). Duplicate Employee IDs from a
  similar race are now prevented at the database level (a unique index on
  `Employee.LegacyId` -- see `ScheduleDbContext`), but that's the only one of
  these races with a DB-level guarantee; the schedule-overwrite race above
  still has none.
- **No tests.** The date/priority-adjacent logic (contiguous-range grouping,
  midnight-crossing time math, the TimeOut formula) is exactly the kind of
  thing that's cheap to pin down with unit tests and expensive to debug by hand
  after a refactor.
- **Manual attendance entries have no real audit trail or approval step.**
  `ManualAttendanceLog.EnteredBy` is still a free-text name, not tied to
  `UserAccount` in any way (see "Signing in" above -- the two are deliberately
  unrelated), and anyone signed in can add or delete one with no confirmation
  beyond the delete button's own Yes/No prompt. Signing in now at least limits
  *who* can open the app; it doesn't yet attribute a manual entry to the
  account that made it, or require a second person's approval. Worth wiring
  `CurrentUserContext.Username` into `EnteredBy` (instead of a free-typed name)
  and/or a real audit log before this app is used somewhere that matters more.
- **No "forgot password" recovery inside the app.** See "Recovering from a
  locked-out account" above -- if every account ends up locked out, getting
  back in needs direct SQL Server access. Worth keeping in mind before
  deactivating an account you're not sure you can reactivate.