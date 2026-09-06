using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.Utilities;
using ScheduleApp.Payroll.Pdf;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Lets someone set up the shared, machine-wide config file (see SharedConfigFile /
/// SharedConfigWriter) without hand-editing JSON or running the PowerShell one-liner
/// from PushListener's README. Opened from MainWindow's gear button, pre-filled with
/// whatever's currently *effective* (the shared file's values if it already exists and
/// has them, otherwise whatever appsettings.json has) -- see MainWindow.SettingsButton_Click
/// for where those come from.
///
/// Covers seven config groups, laid out across five tabs -- Database (the connection
/// string), Device (the Attendance page's device defaults), Attendance (the default
/// work-time-hours seeded into new schedule entries, plus the Attendance Policy
/// buffers/flags), Payroll (the company name every generated payslip prints, plus the Net
/// Pay rounding multiple -- the one PayrollPolicy field exposed here; see
/// PayrollPolicy.NetPayRoundingMultiple's own doc comment for why it alone, and not the
/// rest of PayrollPolicy, has a dialog field), and Sign-in (the sign-in page's logo).
/// None of it except the connection string affects Push Listener (it never reads
/// Attendance:DefaultWorkTimeHours, Attendance:Policy, Payroll:Policy,
/// Payroll:CompanyName, or SignIn:LogoPath) but it all lives in the same dialog/file for
/// one place to manage all of it.
///
/// A field belongs on the tab that owns its *change group*, which isn't always the tab its
/// wording suggests -- UseExcelFormula reads like a reporting setting but is an
/// AttendancePolicy field, so it sits on Attendance. That rule exists because
/// SharedConfigWriter.Save writes whole sections: splitting one group across two tabs
/// would mean an edit on one tab silently rewrites values the other tab was showing.
///
/// Save only exposes the groups that actually changed from what the dialog was opened
/// with (see the Changed* properties) -- so fixing just the connection string doesn't
/// also pin the device defaults and every Policy buffer to their current values in the
/// shared file, permanently shadowing this machine's local appsettings.json for fields
/// nobody meant to touch. A group where nothing changed comes back null, and
/// SharedConfigWriter.Save leaves a null group completely untouched.
///
/// Same self-validating-on-Save shape as ApplyScheduleDialog: Save only commits parsed
/// values once everything on the form has parsed cleanly, so a bad edit can't silently
/// write a zero/garbage value into the shared file. Every failure routes through
/// ShowFieldError, which selects the offending field's tab first -- a bare message box is
/// no help when the field it's complaining about is two tabs away. Cancelling leaves the
/// shared file untouched.
/// </summary>
public partial class SettingsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly DatabaseProvisioningService _provisioningService;
    private readonly string _originalConnectionString;
    private readonly string? _originalDeviceIp;
    private readonly int _originalDevicePort;
    private readonly uint _originalDeviceCommKey;
    private readonly string _originalDeviceTransport;
    private readonly double _originalDefaultWorkTimeHours;
    private readonly AttendancePolicy _originalPolicy;
    private readonly PayrollPolicy _originalPayrollPolicy;
    private readonly string? _originalLogoPath;
    private readonly string _originalCompanyName;

    /// <summary>Set by ChooseLogoButton_Click to the newly picked file's path (not yet
    /// copied anywhere -- see ChangedLogoPath) -- null means no new image was picked
    /// since the dialog opened (or ResetLogoButton_Click cleared it back to null).</summary>
    private string? _pendingLogoSourcePath;

    /// <summary>Set by ResetLogoButton_Click; cleared by ChooseLogoButton_Click. Only
    /// matters if _originalLogoPath was actually set -- see ChangedLogoPath.</summary>
    private bool _resetLogoRequested;

    /// <summary>Null if the connection string field wasn't changed from what the dialog
    /// was opened with.</summary>
    public string? ChangedConnectionString { get; private set; }

    /// <summary>Null if none of the four device fields were changed.</summary>
    public DeviceDefaults? ChangedDevice { get; private set; }

    /// <summary>Null if the default-work-time-hours field wasn't changed from what the
    /// dialog was opened with.</summary>
    public double? ChangedDefaultWorkTimeHours { get; private set; }

    /// <summary>Null if none of the fourteen policy fields were changed.</summary>
    public AttendancePolicy? ChangedPolicy { get; private set; }

    /// <summary>Null if the Net Pay rounding multiple field wasn't changed from what
    /// the dialog was opened with -- the only PayrollPolicy field this dialog edits
    /// (see PayrollPolicy.NetPayRoundingMultiple's own doc comment). Carries every
    /// other PayrollPolicy field through unchanged from _originalPayrollPolicy, the
    /// same "full replace, since there's no other unmanaged key under this section"
    /// reasoning ChangedPolicy/SharedConfigWriter.Save already follow for
    /// Attendance:Policy.</summary>
    public PayrollPolicy? ChangedPayrollPolicy { get; private set; }

    /// <summary>Null if the logo wasn't touched. Empty string means "reset to the
    /// built-in default" (SharedConfigWriter.Save's cue to clear SignIn:LogoPath rather
    /// than copy a file). Otherwise, the newly picked source file's path, still to be
    /// copied into the shared folder by SharedConfigWriter.Save.</summary>
    public string? ChangedLogoPath { get; private set; }

    /// <summary>Null if the company-name field wasn't changed from what the dialog was
    /// opened with. Never empty string on its own -- SaveButton_Click requires the field
    /// to be non-blank, the same "required, not a reset sentinel" treatment
    /// ConnectionStringBox gets, unlike ChangedLogoPath's own empty-string convention (a
    /// picker with a real built-in fallback image to reset to; a plain text field has no
    /// equivalent "clear the box" gesture worth wiring up separately from just typing the
    /// name you actually want).</summary>
    public string? ChangedCompanyName { get; private set; }

    /// <summary>True once Save has validated and populated the Changed* properties above
    /// -- MainWindow only calls SharedConfigWriter.Save (and only offers a restart) when
    /// this is true and at least one Changed* property is non-null.</summary>
    public bool HasChanges =>
        ChangedConnectionString is not null || ChangedDevice is not null ||
        ChangedDefaultWorkTimeHours is not null || ChangedPolicy is not null ||
        ChangedPayrollPolicy is not null || ChangedLogoPath is not null ||
        ChangedCompanyName is not null;

    public SettingsDialog(
        DatabaseProvisioningService provisioningService,
        string connectionString,
        string? deviceIp,
        int devicePort,
        uint deviceCommKey,
        string? deviceTransport,
        double defaultWorkTimeHours,
        AttendancePolicy policy,
        PayrollPolicy payrollPolicy,
        string? signInLogoPath,
        string? companyName,
        string sharedConfigFilePath)
    {
        InitializeComponent();

        _provisioningService = provisioningService;
        _originalConnectionString = connectionString;
        _originalDeviceIp = deviceIp;
        _originalDevicePort = devicePort;
        _originalDeviceCommKey = deviceCommKey;
        _originalDeviceTransport = deviceTransport ?? "Tcp";
        _originalDefaultWorkTimeHours = defaultWorkTimeHours;
        _originalPolicy = policy;
        _originalPayrollPolicy = payrollPolicy;
        _originalLogoPath = signInLogoPath;

        // Same "caller passes the raw effective value, dialog falls back to a sensible
        // default" shape as deviceTransport just above -- PayrollSettings.CompanyName is
        // null/blank on a fresh install (see its own doc comment), and the field should
        // still show *something* editable rather than an empty box.
        _originalCompanyName = string.IsNullOrWhiteSpace(companyName)
            ? PayslipLineBuilder.DefaultCompanyName
            : companyName;

        ConnectionStringBox.Text = connectionString;
        DeviceIpBox.Text = deviceIp ?? string.Empty;
        PortBox.Text = devicePort.ToString(CultureInfo.CurrentCulture);
        CommKeyBox.Text = deviceCommKey.ToString(CultureInfo.CurrentCulture);
        UseUdpCheckBox.IsChecked = string.Equals(deviceTransport, "Udp", StringComparison.OrdinalIgnoreCase);
        FilePathText.Text = $"File: {sharedConfigFilePath}";

        DefaultWorkTimeHoursBox.Text = defaultWorkTimeHours.ToString(CultureInfo.CurrentCulture);

        ClockInBufferBeforeBox.Text = policy.ClockInBufferBefore.ToString(CultureInfo.CurrentCulture);
        ClockInBufferAfterBox.Text = policy.ClockInBufferAfter.ToString(CultureInfo.CurrentCulture);
        ClockOutBufferBeforeBox.Text = policy.ClockOutBufferBefore.ToString(CultureInfo.CurrentCulture);
        ClockOutBufferAfterBox.Text = policy.ClockOutBufferAfter.ToString(CultureInfo.CurrentCulture);
        FlexInBufferBox.Text = policy.FlexibleSegmentClockInBuffer.ToString(CultureInfo.CurrentCulture);
        FlexOutBufferBox.Text = policy.FlexibleSegmentClockOutBuffer.ToString(CultureInfo.CurrentCulture);
        FlexMinBreakGapBox.Text = policy.FlexibleMinimumBreakGap.ToString(CultureInfo.CurrentCulture);
        GracePeriodBox.Text = policy.ClockOutGracePeriod.ToString(CultureInfo.CurrentCulture);
        LateEarlyGraceMinutesBox.Text = policy.LateInEarlyOutGraceMinutes.ToString(CultureInfo.CurrentCulture);
        NightDiffStartBox.Text = TimeDisplayFormat.Format(policy.NightDiffStart);
        NightDiffEndBox.Text = TimeDisplayFormat.Format(policy.NightDiffEnd);
        CapEarlyClockInCheckBox.IsChecked = policy.CapEarlyClockIn;
        StrictOvertimeCheckBox.IsChecked = policy.StrictOvertimeFromShiftEnd;
        UseExcelFormulaCheckBox.IsChecked = policy.UseExcelFormula;
        NetPayRoundingMultipleBox.Text = payrollPolicy.NetPayRoundingMultiple.ToString(CultureInfo.CurrentCulture);

        CompanyNameBox.Text = _originalCompanyName;

        if (AuthLogoLoader.TryLoad(signInLogoPath) is { } logoBitmap)
        {
            LogoPreviewImage.Source = logoBitmap;
            LogoStatusText.Text = $"Custom logo: {Path.GetFileName(signInLogoPath)}";
        }
        else
        {
            LogoStatusText.Text = "Using the default logo.";
        }

        // Database is deliberately the first tab, so this would work on its own -- going
        // through RevealField means reordering the tabs later can't quietly turn the
        // opening focus into a no-op on a tab that hasn't been realized yet.
        Loaded += (_, _) =>
        {
            RevealField(ConnectionStringBox);
            ConnectionStringBox.Focus();
        };
    }

    /// <summary>Selects whichever tab <paramref name="field"/> lives on and scrolls it
    /// into view, so a validation message never points at a field the user can't see.
    /// Walks the *logical* tree rather than the visual one deliberately: an unselected
    /// TabItem has no visual children at all (a TabControl only realizes the tab that's
    /// showing), but TabItem.Content -- and every ScrollViewer/StackPanel/DockPanel
    /// between it and the field -- is a logical child no matter which tab is up. Walking
    /// up from the field rather than being handed a tab also means a field can never be
    /// paired with the wrong one, and moving a field between tabs needs no change here at
    /// all -- which is why none of the TabItems in the XAML has an x:Name.</summary>
    private void RevealField(Control field)
    {
        for (DependencyObject? node = field; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (node is TabItem tab)
                tab.IsSelected = true;
        }

        // The tab just selected only becomes a real visual tree on the next measure pass,
        // and BringIntoView has nothing to scroll until then.
        SettingsTabs.UpdateLayout();
        field.BringIntoView();
    }

    /// <summary>The one way this dialog reports a bad field: switch to its tab, say what's
    /// wrong, then leave the caret in it with the bad text selected so the fix is one
    /// keystroke. Focus deliberately comes *after* the message box rather than before --
    /// dismissing it reactivates this window, and WPF restores focus to whatever held it
    /// when the window was disabled (the Save button), which would undo an earlier
    /// Focus() call.</summary>
    private void ShowFieldError(Control field, string message, string caption)
    {
        RevealField(field);
        MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        field.Focus();
        (field as TextBox)?.SelectAll();
    }

    /// <summary>Backs every "hours" field in the Attendance Policy section -- all eight
    /// share the same rule (a non-negative number), so this avoids repeating the
    /// TryParse/ShowFieldError pair eight times. Not static any more: reporting a bad
    /// value now has to reach the TabControl to bring the field into view.</summary>
    private bool TryParseHours(TextBox box, string fieldLabel, out double value)
    {
        if (double.TryParse(box.Text, out value) && value >= 0)
            return true;

        ShowFieldError(box,
            $"{fieldLabel} must be a number of hours, 0 or greater.",
            "Invalid value");
        return false;
    }

    /// <summary>Same rule as <see cref="TryParseHours"/> (a non-negative number), but for
    /// LateEarlyGraceMinutesBox -- the one Attendance Policy field expressed in minutes
    /// rather than hours (see AttendancePolicy.LateInEarlyOutGraceMinutes's own doc
    /// comment for why), so it needs its own message rather than TryParseHours'
    /// hours-specific wording.</summary>
    private bool TryParseMinutes(TextBox box, string fieldLabel, out double value)
    {
        if (double.TryParse(box.Text, out value) && value >= 0)
            return true;

        ShowFieldError(box,
            $"{fieldLabel} must be a number of minutes, 0 or greater.",
            "Invalid value");
        return false;
    }

    /// <summary>Opens DatabaseSetupDialog (see its own doc comment) to create a new
    /// database and SQL Server login, prefilled from whatever's currently in
    /// ConnectionStringBox. On success just fills ConnectionStringBox with the
    /// resulting connection string -- same as typing/pasting it in by hand -- so
    /// SaveButton_Click's existing change-tracking and Save still needs to be clicked
    /// to actually apply it, the same as every other field in this dialog.</summary>
    private void CreateDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        var setupDialog = new DatabaseSetupDialog(_provisioningService, ConnectionStringBox.Text) { Owner = this };
        if (setupDialog.ShowDialog() == true && setupDialog.ConnectionString is { } newConnectionString)
            ConnectionStringBox.Text = newConnectionString;
    }

    private void ChooseLogoButton_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Choose a sign-in logo image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"
        };

        if (fileDialog.ShowDialog(this) != true)
            return;

        if (AuthLogoLoader.TryLoad(fileDialog.FileName) is not { } bitmap)
        {
            MessageBox.Show(
                "That file couldn't be loaded as an image.",
                "Invalid image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pendingLogoSourcePath = fileDialog.FileName;
        _resetLogoRequested = false;
        LogoPreviewImage.Source = bitmap;
        LogoStatusText.Text = $"New logo selected: {Path.GetFileName(fileDialog.FileName)} (not saved until you click Save)";
    }

    private void ResetLogoButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingLogoSourcePath = null;
        _resetLogoRequested = true;
        LogoPreviewImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Tinapayan_Logo.png"));
        LogoStatusText.Text = "Will reset to the default logo when saved.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringBox.Text))
        {
            ShowFieldError(ConnectionStringBox,
                "Enter a connection string -- both apps need it to reach the database.",
                "Required");
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            ShowFieldError(PortBox,
                "Port must be a number between 1 and 65535.",
                "Invalid port");
            return;
        }

        if (!uint.TryParse(CommKeyBox.Text, out var commKey))
        {
            ShowFieldError(CommKeyBox,
                "Comm key must be a whole number (0 for no password).",
                "Invalid comm key");
            return;
        }

        if (!double.TryParse(DefaultWorkTimeHoursBox.Text, out var defaultWorkTimeHours) || defaultWorkTimeHours <= 0)
        {
            ShowFieldError(DefaultWorkTimeHoursBox,
                "Default work time must be a number of hours greater than 0.",
                "Invalid value");
            return;
        }

        if (!TryParseHours(ClockInBufferBeforeBox, "Clock-in buffer, before scheduled time", out var clockInBefore))
            return;
        if (!TryParseHours(ClockInBufferAfterBox, "Clock-in buffer, after scheduled time", out var clockInAfter))
            return;
        if (!TryParseHours(ClockOutBufferBeforeBox, "Clock-out buffer, before scheduled time", out var clockOutBefore))
            return;
        if (!TryParseHours(ClockOutBufferAfterBox, "Clock-out buffer, after scheduled time", out var clockOutAfter))
            return;
        if (!TryParseHours(FlexInBufferBox, "Split shift segment clock-in buffer", out var flexIn))
            return;
        if (!TryParseHours(FlexOutBufferBox, "Split shift segment clock-out buffer", out var flexOut))
            return;
        if (!TryParseHours(FlexMinBreakGapBox, "Flexible minimum break gap", out var flexMinBreakGap))
            return;
        if (!TryParseHours(GracePeriodBox, "Clock-out grace period", out var grace))
            return;
        if (!TryParseMinutes(LateEarlyGraceMinutesBox, "Late in / early out grace period", out var lateEarlyGrace))
            return;

        if (!TimeDisplayFormat.TryParse(NightDiffStartBox.Text, out var nightDiffStart))
        {
            ShowFieldError(NightDiffStartBox,
                "Night differential start must be a valid time, e.g. 10:00 PM.",
                "Invalid value");
            return;
        }

        if (!TimeDisplayFormat.TryParse(NightDiffEndBox.Text, out var nightDiffEnd))
        {
            ShowFieldError(NightDiffEndBox,
                "Night differential end must be a valid time, e.g. 6:00 AM.",
                "Invalid value");
            return;
        }

        if (!decimal.TryParse(NetPayRoundingMultipleBox.Text, out var netPayRoundingMultiple) ||
            netPayRoundingMultiple <= 0)
        {
            ShowFieldError(NetPayRoundingMultipleBox,
                "Net pay rounding must be a number greater than 0 -- e.g. 1 for the nearest " +
                "whole unit, 0.25 for the nearest quarter.",
                "Invalid value");
            return;
        }

        if (string.IsNullOrWhiteSpace(CompanyNameBox.Text))
        {
            ShowFieldError(CompanyNameBox,
                "Enter the company name to print on generated payslips.",
                "Required");
            return;
        }

        var connectionString = ConnectionStringBox.Text.Trim();
        var deviceIp = string.IsNullOrWhiteSpace(DeviceIpBox.Text) ? null : DeviceIpBox.Text.Trim();
        var deviceTransport = UseUdpCheckBox.IsChecked == true ? "Udp" : "Tcp";
        var capEarlyClockIn = CapEarlyClockInCheckBox.IsChecked == true;
        var strictOvertime = StrictOvertimeCheckBox.IsChecked == true;
        var useExcelFormula = UseExcelFormulaCheckBox.IsChecked == true;

        ChangedConnectionString = connectionString == _originalConnectionString
            ? null
            : connectionString;

        var deviceChanged =
            deviceIp != _originalDeviceIp ||
            port != _originalDevicePort ||
            commKey != _originalDeviceCommKey ||
            deviceTransport != _originalDeviceTransport;
        ChangedDevice = deviceChanged
            ? new DeviceDefaults(deviceIp, port, commKey, deviceTransport)
            : null;

        ChangedDefaultWorkTimeHours = defaultWorkTimeHours != _originalDefaultWorkTimeHours
            ? defaultWorkTimeHours
            : null;

        var policyChanged =
            clockInBefore != _originalPolicy.ClockInBufferBefore ||
            clockInAfter != _originalPolicy.ClockInBufferAfter ||
            clockOutBefore != _originalPolicy.ClockOutBufferBefore ||
            clockOutAfter != _originalPolicy.ClockOutBufferAfter ||
            flexIn != _originalPolicy.FlexibleSegmentClockInBuffer ||
            flexOut != _originalPolicy.FlexibleSegmentClockOutBuffer ||
            flexMinBreakGap != _originalPolicy.FlexibleMinimumBreakGap ||
            grace != _originalPolicy.ClockOutGracePeriod ||
            lateEarlyGrace != _originalPolicy.LateInEarlyOutGraceMinutes ||
            nightDiffStart != _originalPolicy.NightDiffStart ||
            nightDiffEnd != _originalPolicy.NightDiffEnd ||
            capEarlyClockIn != _originalPolicy.CapEarlyClockIn ||
            strictOvertime != _originalPolicy.StrictOvertimeFromShiftEnd ||
            useExcelFormula != _originalPolicy.UseExcelFormula;
        ChangedPolicy = policyChanged
            ? new AttendancePolicy
            {
                ClockInBufferBefore = clockInBefore,
                ClockInBufferAfter = clockInAfter,
                ClockOutBufferBefore = clockOutBefore,
                ClockOutBufferAfter = clockOutAfter,
                FlexibleSegmentClockInBuffer = flexIn,
                FlexibleSegmentClockOutBuffer = flexOut,
                FlexibleMinimumBreakGap = flexMinBreakGap,
                ClockOutGracePeriod = grace,
                LateInEarlyOutGraceMinutes = lateEarlyGrace,
                NightDiffStart = nightDiffStart,
                NightDiffEnd = nightDiffEnd,
                CapEarlyClockIn = capEarlyClockIn,
                StrictOvertimeFromShiftEnd = strictOvertime,
                UseExcelFormula = useExcelFormula
            }
            : null;

        ChangedPayrollPolicy = netPayRoundingMultiple != _originalPayrollPolicy.NetPayRoundingMultiple
            ? new PayrollPolicy
            {
                StandardHoursPerDay = _originalPayrollPolicy.StandardHoursPerDay,
                OvertimeRatePercentage = _originalPayrollPolicy.OvertimeRatePercentage,
                NightDiffRatePercentage = _originalPayrollPolicy.NightDiffRatePercentage,
                NetPayRoundingMultiple = netPayRoundingMultiple
            }
            : null;

        // Empty string is the "reset to default" sentinel SharedConfigWriter.Save reads
        // (see ChangedLogoPath's own doc comment) -- only meaningful if there's actually
        // a custom logo currently set; resetting when there was never one to begin with
        // is a no-op, same as every other field here that didn't change.
        ChangedLogoPath = _pendingLogoSourcePath is not null
            ? _pendingLogoSourcePath
            : _resetLogoRequested && !string.IsNullOrWhiteSpace(_originalLogoPath)
                ? string.Empty
                : null;

        var companyName = CompanyNameBox.Text.Trim();
        ChangedCompanyName = companyName == _originalCompanyName ? null : companyName;

        if (!HasChanges)
        {
            MessageBox.Show(
                "Nothing was changed.",
                "No changes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
