using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Desktop.Services;

/// <summary>Device fields as one group, since the Settings dialog presents and saves
/// them together -- see SharedConfigWriter.Save.</summary>
public record DeviceDefaults(string? DeviceIp, int DevicePort, uint DeviceCommKey, string DeviceTransport);

/// <summary>
/// Writes to the shared, machine-wide config file both this app and
/// ScheduleApp.PushListener read at startup (see SharedConfigFile) -- backs the
/// Settings dialog opened from MainWindow's gear button, so setting the connection
/// string, attendance device defaults, attendance policy, the Net Pay rounding
/// multiple, the sign-in logo, and the payslip company name up for a machine no
/// longer means hand-editing JSON or running the PowerShell one-liner from
/// PushListener's README. Attendance:Policy, Payroll:Policy, Payroll:CompanyName, and
/// SignIn:LogoPath only affect this app -- PushListener never reads any of them -- but
/// are written to the same file as everything else the dialog manages, for one place
/// to configure all of it.
///
/// Only ever touches the keys the dialog edits (ConnectionStrings:ScheduleDb, the four
/// Attendance:Device* keys, Attendance:DefaultWorkTimeHours, all of Attendance:Policy,
/// all of Payroll:Policy, Payroll:CompanyName, and SignIn:LogoPath). Anything else
/// already in the file -- Attendance:LogDatFile, a future PushListener:BaseUrl entry,
/// or anything added by hand -- is read back and re-written untouched, so this can
/// never silently clobber a setting it doesn't know about. Payroll:Policy is fully
/// replaced, not merged, the same as Attendance:Policy just above (see payrollPolicy's
/// own parameter doc on Save for why that's still safe even though only one of its
/// four fields is actually dialog-editable today) -- Payroll:CompanyName is a sibling
/// key under the same Payroll object, not part of that replace, so touching one never
/// clobbers the other.
///
/// Save's parameters are independently optional (null = "leave this alone"),
/// specifically so that saving one change -- e.g. a new connection string after the
/// database moved -- doesn't also pin the Attendance device defaults, every Policy
/// buffer, and the sign-in logo to whatever happened to be showing in the dialog at the
/// time. Without that, the very first Save on a machine would silently freeze those
/// fields to their current values in the shared file, permanently shadowing that
/// machine's local appsettings.json for them even though nobody meant to change them.
/// See SettingsDialog's change-tracking (comparing against what the dialog was opened
/// with) for how the null-vs-non-null decision gets made.
/// </summary>
public class SharedConfigWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Reads the shared file's raw JSON, if it exists. Used both by Save
    /// (to merge into) and by the Settings dialog if it ever needs to distinguish
    /// "explicitly set in the shared file" from "just inherited from appsettings.json"
    /// -- today the dialog prefills from the app's already-merged IConfiguration
    /// instead, which is simpler and shows the same values either way.</summary>
    public JsonObject ReadExisting()
    {
        var path = SharedConfigFile.ResolvePath();
        if (!File.Exists(path))
            return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) as JsonObject ?? new JsonObject();
    }

    /// <summary>Merges whichever of these groups are non-null into whatever's already in
    /// the shared file and writes it back, creating the file and its
    /// %ProgramData%\ScheduleApp folder if none exist yet. A null group is left
    /// completely untouched -- not written, not defaulted -- so calling this with only
    /// connectionString set, for instance, can never change device or policy settings.
    /// Returns the path written to, so the caller can tell the user where it went, or
    /// null if every argument was null (nothing to do; callers should avoid calling this
    /// in the first place when nothing changed, but this keeps it a safe no-op either
    /// way rather than rewriting the file with no actual changes).
    ///
    /// signInLogoSourcePath is a bit different from the others: it isn't the value to
    /// write, it's a source file to copy into the same %ProgramData%\ScheduleApp folder
    /// as the shared config file itself (as "sign-in-logo" + that file's extension) --
    /// SignIn:LogoPath is then set to *that* copied path, not the original source, so
    /// the shared file keeps working even if the original picked file later moves or is
    /// deleted. Empty string (as opposed to null) means "reset to the built-in default"
    /// -- see SettingsDialog.ChangedLogoPath -- which clears SignIn:LogoPath instead of
    /// copying anything.
    ///
    /// Throws on any I/O failure (e.g. permissions) -- the caller (see
    /// MainWindow.SettingsButton_Click) is expected to show that message to the user
    /// rather than let it crash the app. UnauthorizedAccessException is the one most
    /// worth a caller catching specifically: %ProgramData% is often not writable by a
    /// standard (non-admin) account unless whoever set the folder up granted that, so
    /// this is a realistic, not just theoretical, failure mode for whoever's logged
    /// into Desktop.</summary>
    /// <param name="payrollPolicy">Only ChangedPayrollPolicy from the Settings
    /// dialog -- i.e. non-null only when its one dialog-editable field
    /// (NetPayRoundingMultiple) actually changed. Written as a full replace of
    /// Payroll:Policy, the same "exactly N properties, so no other unmanaged key to
    /// preserve" reasoning as policy/Attendance:Policy above -- safe here because
    /// ChangedPayrollPolicy always carries the other three fields through unchanged
    /// from whatever was already effective (see SettingsDialog.ChangedPayrollPolicy's
    /// own doc comment), never a blank/default PayrollPolicy.</param>
    /// <param name="companyName">Only ChangedCompanyName from the Settings dialog.
    /// Empty string is the same "reset to the built-in default" sentinel
    /// signInLogoSourcePath uses -- removes Payroll:CompanyName entirely rather than
    /// writing an empty value, so PayrollSettings.CompanyName reads back null (and
    /// PayslipRenderer's caller falls back to PayslipLineBuilder.DefaultCompanyName)
    /// on next load, the same as if the key had never been set.</param>
    public string? Save(
        string? connectionString = null,
        DeviceDefaults? device = null,
        double? defaultWorkTimeHours = null,
        AttendancePolicy? policy = null,
        PayrollPolicy? payrollPolicy = null,
        string? signInLogoSourcePath = null,
        string? companyName = null)
    {
        if (connectionString is null && device is null && defaultWorkTimeHours is null &&
            policy is null && payrollPolicy is null && signInLogoSourcePath is null && companyName is null)
            return null;

        var path = SharedConfigFile.ResolvePath();
        var root = ReadExisting();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (connectionString is not null)
        {
            var connectionStrings = root["ConnectionStrings"] as JsonObject ?? new JsonObject();
            connectionStrings["ScheduleDb"] = connectionString;
            root["ConnectionStrings"] = connectionStrings;
        }

        if (device is not null)
        {
            var attendance = root["Attendance"] as JsonObject ?? new JsonObject();
            attendance["DeviceIp"] = device.DeviceIp;
            attendance["DevicePort"] = device.DevicePort;
            attendance["DeviceCommKey"] = device.DeviceCommKey;
            attendance["DeviceTransport"] = device.DeviceTransport;
            root["Attendance"] = attendance;
        }

        if (defaultWorkTimeHours is not null)
        {
            var attendance = root["Attendance"] as JsonObject ?? new JsonObject();
            attendance["DefaultWorkTimeHours"] = defaultWorkTimeHours;
            root["Attendance"] = attendance;
        }

        if (policy is not null)
        {
            var attendance = root["Attendance"] as JsonObject ?? new JsonObject();

            // AttendancePolicy has exactly these 14 properties, so -- unlike the
            // Attendance/ConnectionStrings objects above -- this one's fully replaced
            // rather than merged into whatever was already under "Policy": there's no
            // "other, unmanaged key" this dialog could clobber here.
            attendance["Policy"] = new JsonObject
            {
                ["ClockInBufferBefore"] = policy.ClockInBufferBefore,
                ["ClockInBufferAfter"] = policy.ClockInBufferAfter,
                ["ClockOutBufferBefore"] = policy.ClockOutBufferBefore,
                ["ClockOutBufferAfter"] = policy.ClockOutBufferAfter,
                ["FlexibleSegmentClockInBuffer"] = policy.FlexibleSegmentClockInBuffer,
                ["FlexibleSegmentClockOutBuffer"] = policy.FlexibleSegmentClockOutBuffer,
                ["FlexibleMinimumBreakGap"] = policy.FlexibleMinimumBreakGap,
                ["ClockOutGracePeriod"] = policy.ClockOutGracePeriod,
                ["LateInEarlyOutGraceMinutes"] = policy.LateInEarlyOutGraceMinutes,
                // "HH:mm:ss" -- plain 24-hour, culture-invariant -- rather than
                // TimeOnly's default ToString(), so this round-trips cleanly through
                // the config binder's TimeOnly.Parse(string, IFormatProvider) on the
                // way back in (see AttendancePolicy.NightDiffStart/NightDiffEnd).
                ["NightDiffStart"] = policy.NightDiffStart.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ["NightDiffEnd"] = policy.NightDiffEnd.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ["CapEarlyClockIn"] = policy.CapEarlyClockIn,
                ["StrictOvertimeFromShiftEnd"] = policy.StrictOvertimeFromShiftEnd,
                ["UseExcelFormula"] = policy.UseExcelFormula
            };

            root["Attendance"] = attendance;
        }

        if (payrollPolicy is not null)
        {
            var payroll = root["Payroll"] as JsonObject ?? new JsonObject();

            // PayrollPolicy has exactly these 4 properties, so -- same as
            // Attendance:Policy above -- this replaces Payroll:Policy wholesale
            // rather than merging into whatever was already under "Policy".
            payroll["Policy"] = new JsonObject
            {
                ["StandardHoursPerDay"] = payrollPolicy.StandardHoursPerDay,
                ["OvertimeRatePercentage"] = payrollPolicy.OvertimeRatePercentage,
                ["NightDiffRatePercentage"] = payrollPolicy.NightDiffRatePercentage,
                ["NetPayRoundingMultiple"] = payrollPolicy.NetPayRoundingMultiple
            };

            root["Payroll"] = payroll;
        }

        if (companyName is not null)
        {
            var payroll = root["Payroll"] as JsonObject ?? new JsonObject();

            // Empty string ("reset to default") removes the key instead of writing
            // an empty value -- see this parameter's own doc comment on Save.
            if (companyName.Length == 0)
                payroll.Remove("CompanyName");
            else
                payroll["CompanyName"] = companyName;

            root["Payroll"] = payroll;
        }

        if (signInLogoSourcePath is not null && !string.IsNullOrEmpty(directory))
        {
            var signIn = root["SignIn"] as JsonObject ?? new JsonObject();

            // Resolve the copy target before cleaning up old files, so the loop below
            // can skip it if the newly picked file happens to already be that exact
            // path (re-picking the already-copied logo from its own folder).
            var newCopiedPath = signInLogoSourcePath.Length > 0
                ? Path.Combine(directory, "sign-in-logo" + Path.GetExtension(signInLogoSourcePath))
                : null;
            var sourceFullPath = signInLogoSourcePath.Length > 0
                ? Path.GetFullPath(signInLogoSourcePath)
                : null;

            // Clear out any previously copied logo file (whatever its extension) before
            // copying a new one or resetting to default, so switching logos/extensions
            // doesn't leave stale files behind in the shared folder.
            foreach (var staleFile in Directory.EnumerateFiles(directory, "sign-in-logo.*"))
            {
                if (sourceFullPath is not null &&
                    string.Equals(Path.GetFullPath(staleFile), sourceFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try { File.Delete(staleFile); }
                catch { /* best-effort cleanup only -- a leftover file isn't worth failing Save over */ }
            }

            if (newCopiedPath is not null)
            {
                if (!string.Equals(sourceFullPath, Path.GetFullPath(newCopiedPath), StringComparison.OrdinalIgnoreCase))
                    File.Copy(signInLogoSourcePath, newCopiedPath, overwrite: true);

                signIn["LogoPath"] = newCopiedPath;
            }
            else
            {
                // Empty string -- explicit reset to the built-in default.
                signIn.Remove("LogoPath");
            }

            root["SignIn"] = signIn;
        }

        File.WriteAllText(path, root.ToJsonString(WriteOptions));
        return path;
    }
}
