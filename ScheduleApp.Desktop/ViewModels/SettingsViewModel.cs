using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using Microsoft.Data.SqlClient;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.Utilities;
using ScheduleApp.Payroll.Pdf;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>The settings in effect when Settings opens -- the shared file's where it has them,
/// appsettings.json's otherwise.</summary>
public sealed record CurrentSettings(
    string ConnectionString,
    IReadOnlyList<ConnectionProfile> ConnectionProfiles,
    string? DeviceIp,
    int DevicePort,
    uint DeviceCommKey,
    string? DeviceTransport,
    double DefaultWorkTimeHours,
    AttendancePolicy Policy,
    PayrollPolicy PayrollPolicy,
    string? LogoPath,
    string? CompanyName,
    string SharedConfigFilePath);

/// <summary>
/// What Settings' Save changed, group by group -- null for a group left as it was, which
/// SharedConfigWriter.Save leaves untouched, so fixing one value doesn't pin every other one
/// into the shared file. <see cref="LogoPath"/> is a picked image still to be copied, or empty
/// for "back to the built-in logo".
/// </summary>
public sealed record SettingsChanges(
    string? ConnectionString,
    IReadOnlyList<ConnectionProfile>? ConnectionProfiles,
    DeviceDefaults? Device,
    double? DefaultWorkTimeHours,
    AttendancePolicy? Policy,
    PayrollPolicy? PayrollPolicy,
    string? LogoPath,
    string? CompanyName)
{
    public bool Any =>
        ConnectionString is not null || ConnectionProfiles is not null || Device is not null || DefaultWorkTimeHours is not null
        || Policy is not null || PayrollPolicy is not null || LogoPath is not null || CompanyName is not null;
}

/// <summary>A Settings field a problem can point at -- the View brings it into view (switching
/// to its tab) and focuses it.</summary>
public enum SettingsField
{
    ConnectionString,
    NewProfileName,
    NewProfileServer,
    NewProfileDatabase,
    Port,
    CommKey,
    DefaultWorkTimeHours,
    ClockInBufferBefore,
    ClockInBufferAfter,
    ClockOutBufferBefore,
    ClockOutBufferAfter,
    FlexInBuffer,
    FlexOutBuffer,
    FlexMinBreakGap,
    GracePeriod,
    LateEarlyGraceMinutes,
    NightDiffStart,
    NightDiffEnd,
    StandardHoursPerDay,
    NetPayRoundingMultiple,
    CompanyName,
}

/// <summary>Something wrong with one field, for <see cref="SettingsViewModel.ShowFieldProblem"/>.</summary>
public sealed record FieldProblem(SettingsField Field, string Message, string Caption);

/// <summary>
/// Settings: the shared, machine-wide configuration (SharedConfigFile) -- the database
/// connection and its saved profiles, the device defaults, the attendance and payroll
/// policies, the payslip's company name and the sign-in logo -- without hand-editing JSON. Only
/// the connection string matters to the Push Listener service too; the rest is just kept in
/// the same file.
///
/// A field sits on the tab of the group SharedConfigWriter writes it with (UseExcelFormula is
/// an attendance-policy value, so it's on Attendance): a group is written whole, so splitting
/// one across tabs would let an edit on one silently rewrite what another showed.
///
/// Save checks every field before settling anything -- a bad edit can't write a zero into the
/// shared file -- and reports the first problem against its field. Nothing is written here:
/// the shell confirms and writes <see cref="AcceptedChanges"/>.
/// </summary>
public partial class SettingsViewModel : ReactiveViewModel
{
    private const string ImageFilter =
        "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*";

    private readonly IDatabaseProvisioningService _provisioningService;
    private readonly CurrentSettings _original;
    private readonly string _originalCompanyName;
    private readonly string _originalDeviceTransport;
    private readonly Func<string?, bool> _isLoadableImage;

    /// <summary>The profile list as first shown -- what an edit is measured against, so an
    /// untouched synthesized "Current" profile isn't a change.</summary>
    private readonly ConnectionProfile[] _profilesAsOpened;

    /// <summary>A newly picked logo, not yet copied anywhere.</summary>
    private string? _pendingLogoPath;

    private bool _resetLogoRequested;

    /// <param name="isLoadableImage">Whether a path loads as an image -- the sign-in screen's
    /// own loader by default.</param>
    public SettingsViewModel(
        IDatabaseProvisioningService provisioningService,
        CurrentSettings current,
        Func<string?, bool>? isLoadableImage = null,
        IReadOnlyList<string>? knownServers = null)
    {
        _provisioningService = provisioningService;
        _original = current;
        _isLoadableImage = isLoadableImage ?? (path => AuthLogoLoader.TryLoad(path) is not null);
        _originalDeviceTransport = current.DeviceTransport ?? "Tcp";

        // A fresh install has no company name yet; the box still shows the one payslips use.
        _originalCompanyName = string.IsNullOrWhiteSpace(current.CompanyName) ? PayslipLineBuilder.DefaultCompanyName : current.CompanyName;

        KnownServers = knownServers ?? [];
        SharedConfigFileText = $"File: {current.SharedConfigFilePath}";

        _advancedLabelHelper = this.WhenAnyValue(x => x.IsAdvancedExpanded)
            .Select(expanded => expanded ? "Advanced ▴" : "Advanced ▾")
            .ToProperty(this, x => x.AdvancedLabel);
        _canRemoveProfile = this.WhenAnyValue(x => x.SelectedProfile).Select(profile => profile is not null);

        // Picking a profile puts its connection string in effect.
        ConnectionString = current.ConnectionString;
        this.WhenAnyValue(x => x.SelectedProfile)
            .Where(profile => profile is not null)
            .Subscribe(profile => ConnectionString = profile!.ToConnectionString());

        // A machine with no saved profiles shows the connection in effect as "Current" --
        // written back only if the list is actually edited.
        Profiles = [.. current.ConnectionProfiles.Count > 0 ? current.ConnectionProfiles : ImplicitProfiles(current.ConnectionString)];
        _profilesAsOpened = [.. Profiles];

        // A connection string no profile produces (a SQL login's, typed by hand) shows
        // expanded, never hidden behind a collapsed section.
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p.ToConnectionString(), current.ConnectionString.Trim(), StringComparison.OrdinalIgnoreCase));
        IsAdvancedExpanded = SelectedProfile is null;

        var culture = CultureInfo.CurrentCulture;
        DeviceIp = current.DeviceIp ?? string.Empty;
        PortText = current.DevicePort.ToString(culture);
        CommKeyText = current.DeviceCommKey.ToString(culture);
        UseUdp = string.Equals(current.DeviceTransport, "Udp", StringComparison.OrdinalIgnoreCase);

        var policy = current.Policy;
        DefaultWorkTimeHoursText = current.DefaultWorkTimeHours.ToString(culture);
        ClockInBufferBeforeText = policy.ClockInBufferBefore.ToString(culture);
        ClockInBufferAfterText = policy.ClockInBufferAfter.ToString(culture);
        ClockOutBufferBeforeText = policy.ClockOutBufferBefore.ToString(culture);
        ClockOutBufferAfterText = policy.ClockOutBufferAfter.ToString(culture);
        FlexInBufferText = policy.FlexibleSegmentClockInBuffer.ToString(culture);
        FlexOutBufferText = policy.FlexibleSegmentClockOutBuffer.ToString(culture);
        FlexMinBreakGapText = policy.FlexibleMinimumBreakGap.ToString(culture);
        GracePeriodText = policy.ClockOutGracePeriod.ToString(culture);
        LateEarlyGraceMinutesText = policy.LateInEarlyOutGraceMinutes.ToString(culture);
        NightDiffStart = policy.NightDiffStart;
        NightDiffEnd = policy.NightDiffEnd;
        CapEarlyClockIn = policy.CapEarlyClockIn;
        StrictOvertimeFromShiftEnd = policy.StrictOvertimeFromShiftEnd;
        UseExcelFormula = policy.UseExcelFormula;

        var payroll = current.PayrollPolicy;
        StandardHoursPerDayText = payroll.StandardHoursPerDay.ToString(culture);
        OvertimeRatePercent = ToPercent(payroll.OvertimeRatePercentage);
        NightDiffRatePercent = ToPercent(payroll.NightDiffRatePercentage);
        RestDayPremiumPercent = ToPercent(payroll.RestDayPremiumPercentage);
        RestDayOvertimeRatePercent = ToPercent(payroll.RestDayOvertimeRatePercentage);
        HolidayPremiumPercent = ToPercent(payroll.HolidayPremiumPercentage);
        NetPayRoundingMultipleText = payroll.NetPayRoundingMultiple.ToString(culture);
        CompanyName = _originalCompanyName;

        if (_isLoadableImage(current.LogoPath))
        {
            LogoPreviewPath = current.LogoPath;
            LogoStatusText = $"Custom logo: {Path.GetFileName(current.LogoPath)}";
        }
        else
        {
            LogoStatusText = "Using the default logo.";
        }
    }

    /// <summary>Points the person at a field that's wrong: the View shows its tab, says what's
    /// wrong, then focuses it.</summary>
    public Interaction<FieldProblem, RxVoid> ShowFieldProblem { get; } = new();

    public string SharedConfigFileText { get; }

    /// <summary>Local SQL Server instances to offer for a new profile; the box takes any name.</summary>
    public IReadOnlyList<string> KnownServers { get; }

    // ---- Database ----

    public ObservableCollection<ConnectionProfile> Profiles { get; }

    [Reactive]
    public partial ConnectionProfile? SelectedProfile { get; set; }

    private readonly IObservable<bool> _canRemoveProfile;

    /// <summary>What both apps connect with -- the selected profile's, or typed under
    /// Advanced.</summary>
    [Reactive]
    public partial string ConnectionString { get; set; } = string.Empty;

    [Reactive]
    public partial bool IsAddingProfile { get; private set; }

    [Reactive]
    public partial string NewProfileName { get; set; } = string.Empty;

    [Reactive]
    public partial string NewProfileServer { get; set; } = string.Empty;

    [Reactive]
    public partial string NewProfileDatabase { get; set; } = string.Empty;

    [Reactive]
    public partial bool IsAdvancedExpanded { get; set; }

    [ObservableAsProperty(InitialValue = "Advanced ▾")]
    public partial string AdvancedLabel { get; }

    // ---- Device ----

    [Reactive]
    public partial string DeviceIp { get; set; } = string.Empty;

    [Reactive]
    public partial string PortText { get; set; } = string.Empty;

    [Reactive]
    public partial string CommKeyText { get; set; } = string.Empty;

    [Reactive]
    public partial bool UseUdp { get; set; }

    // ---- Attendance ----

    [Reactive]
    public partial string DefaultWorkTimeHoursText { get; set; } = string.Empty;

    [Reactive]
    public partial string ClockInBufferBeforeText { get; set; } = string.Empty;

    [Reactive]
    public partial string ClockInBufferAfterText { get; set; } = string.Empty;

    [Reactive]
    public partial string ClockOutBufferBeforeText { get; set; } = string.Empty;

    [Reactive]
    public partial string ClockOutBufferAfterText { get; set; } = string.Empty;

    [Reactive]
    public partial string FlexInBufferText { get; set; } = string.Empty;

    [Reactive]
    public partial string FlexOutBufferText { get; set; } = string.Empty;

    [Reactive]
    public partial string FlexMinBreakGapText { get; set; } = string.Empty;

    [Reactive]
    public partial string GracePeriodText { get; set; } = string.Empty;

    /// <summary>The one policy value in minutes.</summary>
    [Reactive]
    public partial string LateEarlyGraceMinutesText { get; set; } = string.Empty;

    [Reactive]
    public partial TimeOnly? NightDiffStart { get; set; }

    [Reactive]
    public partial TimeOnly? NightDiffEnd { get; set; }

    [Reactive]
    public partial bool CapEarlyClockIn { get; set; }

    [Reactive]
    public partial bool StrictOvertimeFromShiftEnd { get; set; }

    [Reactive]
    public partial bool UseExcelFormula { get; set; }

    // ---- Payroll ----

    [Reactive]
    public partial string CompanyName { get; set; } = string.Empty;

    [Reactive]
    public partial string StandardHoursPerDayText { get; set; } = string.Empty;

    /// <summary>The premiums as percents (30 for 30%); a box cleared to blank keeps the
    /// policy's value -- a company default has nothing above it to inherit.</summary>
    [Reactive]
    public partial double? OvertimeRatePercent { get; set; }

    [Reactive]
    public partial double? NightDiffRatePercent { get; set; }

    [Reactive]
    public partial double? RestDayPremiumPercent { get; set; }

    [Reactive]
    public partial double? RestDayOvertimeRatePercent { get; set; }

    [Reactive]
    public partial double? HolidayPremiumPercent { get; set; }

    [Reactive]
    public partial string NetPayRoundingMultipleText { get; set; } = string.Empty;

    // ---- Sign-in ----

    /// <summary>The logo to preview, or null for the built-in one.</summary>
    [Reactive]
    public partial string? LogoPreviewPath { get; private set; }

    [Reactive]
    public partial string LogoStatusText { get; private set; } = string.Empty;

    /// <summary>What Save settled on -- null until it succeeds.</summary>
    public SettingsChanges? AcceptedChanges { get; private set; }

    [ReactiveCommand]
    private void BeginAddProfile()
    {
        NewProfileName = NewProfileServer = NewProfileDatabase = string.Empty;
        IsAddingProfile = true;
    }

    [ReactiveCommand]
    private void CancelAddProfile() => IsAddingProfile = false;

    /// <summary>Adds the profile and selects it, which puts it in effect.</summary>
    [ReactiveCommand]
    private async Task ConfirmAddProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProfileName))
        {
            await ReportAsync(SettingsField.NewProfileName, "Enter a name for this profile.", "Required");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProfileServer))
        {
            await ReportAsync(SettingsField.NewProfileServer, "Enter the SQL Server instance name.", "Required");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProfileDatabase))
        {
            await ReportAsync(SettingsField.NewProfileDatabase, "Enter a database name.", "Required");
            return;
        }

        var profile = new ConnectionProfile(NewProfileName.Trim(), NewProfileServer.Trim(), NewProfileDatabase.Trim());
        Profiles.Add(profile);
        SelectedProfile = profile;
        IsAddingProfile = false;
    }

    /// <summary>No confirmation: nothing is written until Save, and the shell confirms that.</summary>
    [ReactiveCommand(CanExecute = nameof(_canRemoveProfile))]
    private void RemoveProfile()
    {
        if (SelectedProfile is { } profile)
        {
            Profiles.Remove(profile);
            SelectedProfile = null;
        }
    }

    [ReactiveCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    /// <summary>Database Setup for the connection string in effect, Windows-Authenticated --
    /// the primary way to create one.</summary>
    [ReactiveCommand]
    private Task CreateDatabaseAsync() => RunDatabaseSetupAsync(DatabaseSetupMode.WindowsAuthOnly);

    /// <summary>Advanced's escape hatch, for a machine where Windows Authentication isn't
    /// practical.</summary>
    [ReactiveCommand]
    private Task CreateDedicatedLoginAsync() => RunDatabaseSetupAsync(DatabaseSetupMode.DedicatedLogin);

    /// <summary>A successful setup just fills in the connection string, as typing it would --
    /// Save still applies it.</summary>
    private async Task RunDatabaseSetupAsync(DatabaseSetupMode mode)
    {
        var setup = new DatabaseSetupViewModel(_provisioningService, ConnectionString, mode, knownServers: KnownServers);
        if (await ShowDialogAsync(setup) && setup.ConnectionString is { } connectionString)
            ConnectionString = connectionString;
    }

    [ReactiveCommand]
    private async Task ChooseLogoAsync()
    {
        if (await PickFileToOpenAsync(ImageFilter, title: "Choose a sign-in logo image") is not { } path) return;

        if (!_isLoadableImage(path))
        {
            await NotifyAsync("That file couldn't be loaded as an image.", "Invalid image", NoticeKind.Warning);
            return;
        }

        _pendingLogoPath = path;
        _resetLogoRequested = false;
        LogoPreviewPath = path;
        LogoStatusText = $"New logo selected: {Path.GetFileName(path)} (not saved until you click Save)";
    }

    [ReactiveCommand]
    private void ResetLogo()
    {
        _pendingLogoPath = null;
        _resetLogoRequested = true;
        LogoPreviewPath = null;
        LogoStatusText = "Will reset to the default logo when saved.";
    }

    /// <summary>Checks every field, then settles what changed; true once there's something to
    /// write.</summary>
    [ReactiveCommand]
    private async Task<bool> SaveAsync()
    {
        if (await ValidateAsync() is not { } changes) return false;

        if (!changes.Any)
        {
            await NotifyAsync("Nothing was changed.", "No changes");
            return false;
        }

        AcceptedChanges = changes;
        return true;
    }

    private async Task<SettingsChanges?> ValidateAsync()
    {
        var culture = CultureInfo.CurrentCulture;

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            IsAdvancedExpanded = true;
            return await RejectAsync(SettingsField.ConnectionString,
                "Select or add a connection profile above, or enter a connection string directly here -- both apps need one " +
                "to reach the database.", "Required");
        }

        if (!int.TryParse(PortText, NumberStyles.Integer, culture, out var port) || port is <= 0 or > 65535)
            return await RejectAsync(SettingsField.Port, "Port must be a number between 1 and 65535.", "Invalid port");

        if (!uint.TryParse(CommKeyText, NumberStyles.Integer, culture, out var commKey))
            return await RejectAsync(SettingsField.CommKey, "Comm key must be a whole number (0 for no password).", "Invalid comm key");

        if (!double.TryParse(DefaultWorkTimeHoursText, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var defaultWorkTimeHours)
            || defaultWorkTimeHours <= 0)
            return await RejectAsync(SettingsField.DefaultWorkTimeHours, "Default work time must be a number of hours greater than 0.",
                "Invalid value");

        (SettingsField Field, string Text, string Label, bool Minutes)[] durations =
        [
            (SettingsField.ClockInBufferBefore, ClockInBufferBeforeText, "Clock-in buffer, before scheduled time", false),
            (SettingsField.ClockInBufferAfter, ClockInBufferAfterText, "Clock-in buffer, after scheduled time", false),
            (SettingsField.ClockOutBufferBefore, ClockOutBufferBeforeText, "Clock-out buffer, before scheduled time", false),
            (SettingsField.ClockOutBufferAfter, ClockOutBufferAfterText, "Clock-out buffer, after scheduled time", false),
            (SettingsField.FlexInBuffer, FlexInBufferText, "Split shift segment clock-in buffer", false),
            (SettingsField.FlexOutBuffer, FlexOutBufferText, "Split shift segment clock-out buffer", false),
            (SettingsField.FlexMinBreakGap, FlexMinBreakGapText, "Flexible minimum break gap", false),
            (SettingsField.GracePeriod, GracePeriodText, "Clock-out grace period", false),
            (SettingsField.LateEarlyGraceMinutes, LateEarlyGraceMinutesText, "Late in / early out grace period", true),
        ];
        var parsed = new double[durations.Length];
        for (var i = 0; i < durations.Length; i++)
        {
            var (field, text, label, minutes) = durations[i];
            if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out parsed[i]) || parsed[i] < 0)
                return await RejectAsync(field, $"{label} must be a number of {(minutes ? "minutes" : "hours")}, 0 or greater.",
                    "Invalid value");
        }

        if (NightDiffStart is not { } nightDiffStart)
            return await RejectAsync(SettingsField.NightDiffStart, "Night differential start must be a valid time.", "Invalid value");
        if (NightDiffEnd is not { } nightDiffEnd)
            return await RejectAsync(SettingsField.NightDiffEnd, "Night differential end must be a valid time.", "Invalid value");

        if (!decimal.TryParse(StandardHoursPerDayText, NumberStyles.Number, culture, out var standardHoursPerDay) || standardHoursPerDay <= 0)
            return await RejectAsync(SettingsField.StandardHoursPerDay,
                "Standard hours per day must be a number greater than 0 -- it divides into the daily rate to get the hourly " +
                "rate every premium is computed from.", "Invalid value");

        if (!decimal.TryParse(NetPayRoundingMultipleText, NumberStyles.Number, culture, out var netPayRoundingMultiple)
            || netPayRoundingMultiple <= 0)
            return await RejectAsync(SettingsField.NetPayRoundingMultiple,
                "Net pay rounding must be a number greater than 0 -- e.g. 1 for the nearest whole unit, 0.25 for the nearest " +
                "quarter.", "Invalid value");

        if (string.IsNullOrWhiteSpace(CompanyName))
            return await RejectAsync(SettingsField.CompanyName, "Enter the company name to print on generated payslips.", "Required");

        var connectionString = ConnectionString.Trim();
        var deviceIp = string.IsNullOrWhiteSpace(DeviceIp) ? null : DeviceIp.Trim();
        var deviceTransport = UseUdp ? "Udp" : "Tcp";
        var device = deviceIp != _original.DeviceIp || port != _original.DevicePort || commKey != _original.DeviceCommKey
                     || deviceTransport != _originalDeviceTransport
            ? new DeviceDefaults(deviceIp, port, commKey, deviceTransport)
            : null;

        // Both policies are copies of what Settings opened with, with only the fields edited
        // here assigned -- an object initializer would silently reset any field added to
        // either class later (as once happened to the two rest-day rates).
        var original = _original.Policy;
        var policy = original.Clone();
        policy.ClockInBufferBefore = parsed[0];
        policy.ClockInBufferAfter = parsed[1];
        policy.ClockOutBufferBefore = parsed[2];
        policy.ClockOutBufferAfter = parsed[3];
        policy.FlexibleSegmentClockInBuffer = parsed[4];
        policy.FlexibleSegmentClockOutBuffer = parsed[5];
        policy.FlexibleMinimumBreakGap = parsed[6];
        policy.ClockOutGracePeriod = parsed[7];
        policy.LateInEarlyOutGraceMinutes = parsed[8];
        policy.NightDiffStart = nightDiffStart;
        policy.NightDiffEnd = nightDiffEnd;
        policy.CapEarlyClockIn = CapEarlyClockIn;
        policy.StrictOvertimeFromShiftEnd = StrictOvertimeFromShiftEnd;
        policy.UseExcelFormula = UseExcelFormula;
        var policyChanged =
            policy.ClockInBufferBefore != original.ClockInBufferBefore || policy.ClockInBufferAfter != original.ClockInBufferAfter
            || policy.ClockOutBufferBefore != original.ClockOutBufferBefore || policy.ClockOutBufferAfter != original.ClockOutBufferAfter
            || policy.FlexibleSegmentClockInBuffer != original.FlexibleSegmentClockInBuffer
            || policy.FlexibleSegmentClockOutBuffer != original.FlexibleSegmentClockOutBuffer
            || policy.FlexibleMinimumBreakGap != original.FlexibleMinimumBreakGap
            || policy.ClockOutGracePeriod != original.ClockOutGracePeriod
            || policy.LateInEarlyOutGraceMinutes != original.LateInEarlyOutGraceMinutes
            || policy.NightDiffStart != original.NightDiffStart || policy.NightDiffEnd != original.NightDiffEnd
            || policy.CapEarlyClockIn != original.CapEarlyClockIn
            || policy.StrictOvertimeFromShiftEnd != original.StrictOvertimeFromShiftEnd
            || policy.UseExcelFormula != original.UseExcelFormula;

        var originalPayroll = _original.PayrollPolicy;
        var payroll = originalPayroll.Clone();
        payroll.StandardHoursPerDay = standardHoursPerDay;
        payroll.OvertimeRatePercentage = ToFraction(OvertimeRatePercent) ?? originalPayroll.OvertimeRatePercentage;
        payroll.NightDiffRatePercentage = ToFraction(NightDiffRatePercent) ?? originalPayroll.NightDiffRatePercentage;
        payroll.RestDayPremiumPercentage = ToFraction(RestDayPremiumPercent) ?? originalPayroll.RestDayPremiumPercentage;
        payroll.RestDayOvertimeRatePercentage = ToFraction(RestDayOvertimeRatePercent) ?? originalPayroll.RestDayOvertimeRatePercentage;
        payroll.HolidayPremiumPercentage = ToFraction(HolidayPremiumPercent) ?? originalPayroll.HolidayPremiumPercentage;
        payroll.NetPayRoundingMultiple = netPayRoundingMultiple;
        var payrollChanged =
            payroll.StandardHoursPerDay != originalPayroll.StandardHoursPerDay
            || payroll.OvertimeRatePercentage != originalPayroll.OvertimeRatePercentage
            || payroll.NightDiffRatePercentage != originalPayroll.NightDiffRatePercentage
            || payroll.RestDayPremiumPercentage != originalPayroll.RestDayPremiumPercentage
            || payroll.RestDayOvertimeRatePercentage != originalPayroll.RestDayOvertimeRatePercentage
            || payroll.HolidayPremiumPercentage != originalPayroll.HolidayPremiumPercentage
            || payroll.NetPayRoundingMultiple != originalPayroll.NetPayRoundingMultiple;

        // Resetting only means something if there's a custom logo to reset.
        var logoPath = _pendingLogoPath
            ?? (_resetLogoRequested && !string.IsNullOrWhiteSpace(_original.LogoPath) ? string.Empty : null);

        var companyName = CompanyName.Trim();

        return new SettingsChanges(
            connectionString == _original.ConnectionString ? null : connectionString,
            Profiles.SequenceEqual(_profilesAsOpened) ? null : [.. Profiles],
            device,
            defaultWorkTimeHours != _original.DefaultWorkTimeHours ? defaultWorkTimeHours : null,
            policyChanged ? policy : null,
            payrollChanged ? payroll : null,
            logoPath,
            companyName == _originalCompanyName ? null : companyName);
    }

    private async Task ReportAsync(SettingsField field, string message, string caption) =>
        await ShowFieldProblem.Handle(new FieldProblem(field, message, caption));

    private async Task<SettingsChanges?> RejectAsync(SettingsField field, string message, string caption)
    {
        await ReportAsync(field, message, caption);
        return null;
    }

    /// <summary>The connection in effect as a single "Current" profile, when it parses.</summary>
    private static List<ConnectionProfile> ImplicitProfiles(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return [];

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return [new("Current", builder.DataSource, builder.InitialCatalog)];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <summary>A premium fraction (0.30) as the percent its box shows (30).</summary>
    private static double ToPercent(decimal fraction) => (double)(fraction * 100m);

    /// <summary>A typed percent (30) as the fraction the policy holds (0.30), to four places.</summary>
    private static decimal? ToFraction(double? percent) => percent is double p ? Math.Round((decimal)p / 100m, 4) : null;
}
