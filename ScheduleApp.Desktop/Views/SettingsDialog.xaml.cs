using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Controls;
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
/// buffers/flags), Payroll (the company name every generated payslip prints, plus every
/// PayrollPolicy value -- the premium rate table and the Net Pay rounding multiple), and
/// Sign-in (the sign-in page's logo).
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

    /// <summary>Exactly what the constructor was given -- compared against _profiles in
    /// SaveButton_Click to decide ChangedConnectionProfiles. Never mutated; _profiles
    /// below is the working copy Add/Remove actually touch.</summary>
    private readonly IReadOnlyList<ConnectionProfile> _originalConnectionProfiles;

    /// <summary>Working copy of the saved-profile list shown in ConnectionProfilesCombo
    /// -- starts as a clone of _originalConnectionProfiles (or, on a machine with no
    /// saved profiles yet, a single synthesized "Current" entry -- see
    /// BuildImplicitProfileList) and is mutated directly by
    /// AddProfileButton_Click/RemoveProfileButton_Click. Never null after the
    /// constructor runs.</summary>
    private List<ConnectionProfile> _profiles = new();
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

    /// <summary>Null if the saved-profile list wasn't changed (by Add/Remove) from what
    /// the dialog was opened with. Independent of ChangedConnectionString above -- adding
    /// a profile without also selecting/Saving it as the active connection string still
    /// counts as a change here, and vice versa.</summary>
    public IReadOnlyList<ConnectionProfile>? ChangedConnectionProfiles { get; private set; }

    /// <summary>Null if none of the four device fields were changed.</summary>
    public DeviceDefaults? ChangedDevice { get; private set; }

    /// <summary>Null if the default-work-time-hours field wasn't changed from what the
    /// dialog was opened with.</summary>
    public double? ChangedDefaultWorkTimeHours { get; private set; }

    /// <summary>Null if none of the fourteen policy fields were changed.</summary>
    public AttendancePolicy? ChangedPolicy { get; private set; }

    /// <summary>Null if the Net Pay rounding multiple field wasn't changed from what
    /// the dialog was opened with. This dialog now edits every PayrollPolicy field, so
    /// there's nothing left to carry through -- but it's still built by cloning
    /// _originalPayrollPolicy and assigning over it rather than by listing fields in
    /// an object initializer, so that a field added to PayrollPolicy later can't
    /// silently revert to its C# default here (which is exactly what happened to the
    /// two rest-day rates before they got fields of their own). Same "full replace,
    /// since there's no other unmanaged key under this section" reasoning
    /// ChangedPolicy/SharedConfigWriter.Save already follow for
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
        ChangedConnectionString is not null || ChangedConnectionProfiles is not null ||
        ChangedDevice is not null ||
        ChangedDefaultWorkTimeHours is not null || ChangedPolicy is not null ||
        ChangedPayrollPolicy is not null || ChangedLogoPath is not null ||
        ChangedCompanyName is not null;

    public SettingsDialog(
        DatabaseProvisioningService provisioningService,
        string connectionString,
        IReadOnlyList<ConnectionProfile> connectionProfiles,
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
        _originalConnectionProfiles = connectionProfiles;
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

        // A machine with no saved profiles yet (pre-upgrade, or nothing set up)
        // synthesizes a single "Current" entry from whatever's already effective,
        // purely for display -- never written back unless Add/Remove actually
        // touches the list (see ChangedConnectionProfiles/SaveButton_Click).
        _profiles = connectionProfiles.Count > 0
            ? new List<ConnectionProfile>(connectionProfiles)
            : BuildImplicitProfileList(connectionString);

        // Select whichever profile's own connection string matches what's already
        // effective, if any -- an Advanced/raw string or a dedicated-login SQL-auth
        // string won't match anything here, which is exactly the signal to leave the
        // combo unselected and expand Advanced below instead, so what's actually in
        // effect is never hidden behind a collapsed section.
        var activeProfile = _profiles.FirstOrDefault(p =>
            string.Equals(p.ToConnectionString(), connectionString.Trim(), StringComparison.OrdinalIgnoreCase));
        RefreshProfilesCombo(activeProfile);
        if (activeProfile is null) ExpandAdvanced();

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
        NightDiffStartBox.SelectedTime = policy.NightDiffStart;
        NightDiffEndBox.SelectedTime = policy.NightDiffEnd;
        CapEarlyClockInCheckBox.IsChecked = policy.CapEarlyClockIn;
        StrictOvertimeCheckBox.IsChecked = policy.StrictOvertimeFromShiftEnd;
        UseExcelFormulaCheckBox.IsChecked = policy.UseExcelFormula;
        StandardHoursPerDayBox.Text = payrollPolicy.StandardHoursPerDay.ToString(CultureInfo.CurrentCulture);
        // .Value, not .Text -- these five are PercentTextBox now, not TextBox. No
        // ToString() needed either: Value takes the raw decimal fraction directly and
        // the control does its own x100-plus-"%" formatting for display.
        OvertimeRatePercentageBox.Value = payrollPolicy.OvertimeRatePercentage;
        NightDiffRatePercentageBox.Value = payrollPolicy.NightDiffRatePercentage;
        RestDayPremiumPercentageBox.Value = payrollPolicy.RestDayPremiumPercentage;
        RestDayOvertimeRatePercentageBox.Value = payrollPolicy.RestDayOvertimeRatePercentage;
        HolidayPremiumPercentageBox.Value = payrollPolicy.HolidayPremiumPercentage;
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
        //
        // Focuses ConnectionProfilesCombo, not ConnectionStringBox -- the latter now
        // lives inside AdvancedPanel, which starts Collapsed whenever a profile matched
        // above (see the constructor), and Control.Focus() on an element inside a
        // Visibility.Collapsed container silently no-ops in WPF. The primary control is
        // the right opening focus target anyway now that it's the primary way to work
        // with this tab.
        Loaded += (_, _) =>
        {
            RevealField(ConnectionProfilesCombo);
            ConnectionProfilesCombo.Focus();
        };

        // Synchronous -- see SqlServerDiscovery's own doc comment for why (a registry
        // read, never network discovery). NewProfileServerBox stays editable
        // regardless of whether this finds anything, so an empty result never blocks
        // typing a server name by hand.
        NewProfileServerBox.ItemsSource = SqlServerDiscovery.DiscoverServers();
    }

    /// <summary>Best-effort synthesizes a single "Current" profile from whatever
    /// connection string is already effective, for a machine that hasn't saved any
    /// profiles yet -- so the combo isn't empty the first time this dialog opens after
    /// upgrading. Never written back on its own; only an actual Add/Remove marks
    /// ChangedConnectionProfiles non-null (see SaveButton_Click). A blank or
    /// unparseable string (nothing configured, or an unusual Advanced-only value) just
    /// returns an empty list rather than failing to open the dialog over it -- same
    /// "best-effort prefill, never essential" treatment DatabaseSetupDialog's own
    /// Server/Database prefill already gives an unparseable connection string.</summary>
    private static List<ConnectionProfile> BuildImplicitProfileList(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new List<ConnectionProfile>();

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return new List<ConnectionProfile> { new("Current", builder.DataSource, builder.InitialCatalog) };
        }
        catch (ArgumentException)
        {
            return new List<ConnectionProfile>();
        }
    }

    /// <summary>Re-binds ConnectionProfilesCombo to the current _profiles list --
    /// re-assigning ItemsSource rather than mutating in place, since _profiles is a
    /// plain List (not an ObservableCollection), matching this file's existing
    /// "no bindings/MVVM, just re-set what changed" idiom elsewhere. Called after every
    /// Add/Remove, and once from the constructor.</summary>
    private void RefreshProfilesCombo(ConnectionProfile? selectProfile = null)
    {
        ConnectionProfilesCombo.ItemsSource = null;
        ConnectionProfilesCombo.ItemsSource = _profiles;
        ConnectionProfilesCombo.SelectedItem = selectProfile;
    }

    /// <summary>Expands AdvancedPanel and flips AdvancedToggleButton's own glyph to
    /// match -- the manual equivalent of RevealField's TabItem.IsSelected flip, for the
    /// one collapsible section on this tab that isn't a TabItem. Needed wherever code
    /// has to guarantee ConnectionStringBox is actually visible/focusable, since
    /// Control.Focus() on an element inside a Visibility.Collapsed container silently
    /// no-ops in WPF.</summary>
    private void ExpandAdvanced()
    {
        AdvancedPanel.Visibility = Visibility.Visible;
        AdvancedToggleButton.Content = "Advanced ▴";
    }

    private void ConnectionProfilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionProfilesCombo.SelectedItem is ConnectionProfile profile)
            ConnectionStringBox.Text = profile.ToConnectionString();
    }

    private void AddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        NewProfileNameBox.Text = string.Empty;
        NewProfileServerBox.Text = string.Empty;
        NewProfileDatabaseBox.Text = string.Empty;
        AddProfilePanel.Visibility = Visibility.Visible;
        NewProfileNameBox.Focus();
    }

    private void CancelAddProfileButton_Click(object sender, RoutedEventArgs e)
        => AddProfilePanel.Visibility = Visibility.Collapsed;

    /// <summary>Validates the three new-profile fields (same "blank blocks, nothing else
    /// does" shape ShowFieldError's other callers use), appends to _profiles, and
    /// selects the new entry -- which flows into ConnectionStringBox automatically via
    /// ConnectionProfilesCombo_SelectionChanged above, the same way picking any other
    /// profile does.</summary>
    private void ConfirmAddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewProfileNameBox.Text))
        {
            ShowFieldError(NewProfileNameBox, "Enter a name for this profile.", "Required");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProfileServerBox.Text))
        {
            ShowFieldError(NewProfileServerBox, "Enter the SQL Server instance name.", "Required");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProfileDatabaseBox.Text))
        {
            ShowFieldError(NewProfileDatabaseBox, "Enter a database name.", "Required");
            return;
        }

        var profile = new ConnectionProfile(
            NewProfileNameBox.Text.Trim(), NewProfileServerBox.Text.Trim(), NewProfileDatabaseBox.Text.Trim());
        _profiles.Add(profile);
        RefreshProfilesCombo(profile);
        AddProfilePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>No confirmation dialog -- same "nothing commits until the outer Save,
    /// plus MainWindow's own shared-file confirmation" model ChooseLogoButton_Click/
    /// ResetLogoButton_Click already use for this dialog's other reversible edits.</summary>
    private void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionProfilesCombo.SelectedItem is not ConnectionProfile profile) return;
        _profiles.Remove(profile);
        RefreshProfilesCombo();
    }

    private void AdvancedToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var expanding = AdvancedPanel.Visibility != Visibility.Visible;
        AdvancedPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
        AdvancedToggleButton.Content = expanding ? "Advanced ▴" : "Advanced ▾";
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

        // TimeInput is a UserControl -- focusing it directly would just put keyboard
        // focus on the container itself rather than anywhere a person could type, so
        // it gets its own entry point (FocusHour) into the hour field instead.
        if (field is TimeInput timeInput)
            timeInput.FocusHour();
        else
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

    /// <summary>The primary button: creates the database named in whatever's currently
    /// in ConnectionStringBox (kept in sync with the selected/newly-added profile via
    /// ConnectionProfilesCombo_SelectionChanged/ConfirmAddProfileButton_Click) using
    /// the signed-in Windows account -- no SQL Server login of any kind. See
    /// RunDatabaseSetup below for the shared plumbing with
    /// CreateDedicatedLoginButton_Click.</summary>
    private void CreateDatabaseButton_Click(object sender, RoutedEventArgs e)
        => RunDatabaseSetup(DatabaseSetupMode.WindowsAuthOnly);

    /// <summary>The Advanced section's escape hatch, for a machine where Windows
    /// Authentication "isn't practical" (see DatabaseSetupDialog's own IntroText
    /// wording) -- the pre-redesign default behavior of what used to be this tab's only
    /// Create button. See RunDatabaseSetup below.</summary>
    private void CreateDedicatedLoginButton_Click(object sender, RoutedEventArgs e)
        => RunDatabaseSetup(DatabaseSetupMode.DedicatedLogin);

    /// <summary>Opens DatabaseSetupDialog (see its own doc comment) in the given mode,
    /// prefilled from whatever's currently in ConnectionStringBox. On success just
    /// fills ConnectionStringBox with the resulting connection string -- same as
    /// typing/pasting it in by hand -- so SaveButton_Click's existing change-tracking
    /// and Save still needs to be clicked to actually apply it, the same as every other
    /// field in this dialog. Deliberately doesn't also touch ConnectionProfilesCombo's
    /// selection or _profiles -- a successful Create for WindowsAuthOnly mode just
    /// re-derives the same connection string the selected/newly-added profile already
    /// produced, and DedicatedLogin mode's SQL-auth result was never going to match any
    /// profile anyway (see the constructor's own active-profile matching).</summary>
    private void RunDatabaseSetup(DatabaseSetupMode mode)
    {
        var setupDialog = new DatabaseSetupDialog(_provisioningService, ConnectionStringBox.Text, mode) { Owner = this };
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
            // Expand Advanced first -- ShowFieldError's own Focus() call would
            // otherwise silently no-op on a field inside a Collapsed container.
            ExpandAdvanced();
            ShowFieldError(ConnectionStringBox,
                "Select or add a connection profile above, or enter a connection string directly here -- both apps need one to reach the database.",
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

        if (NightDiffStartBox.SelectedTime is not { } nightDiffStart)
        {
            ShowFieldError(NightDiffStartBox, "Night differential start must be a valid time.", "Invalid value");
            return;
        }

        if (NightDiffEndBox.SelectedTime is not { } nightDiffEnd)
        {
            ShowFieldError(NightDiffEndBox, "Night differential end must be a valid time.", "Invalid value");
            return;
        }

        if (!decimal.TryParse(StandardHoursPerDayBox.Text, out var standardHoursPerDay) ||
            standardHoursPerDay <= 0)
        {
            ShowFieldError(StandardHoursPerDayBox,
                "Standard hours per day must be a number greater than 0 -- it divides into the " +
                "daily rate to get the hourly rate every premium is computed from.",
                "Invalid value");
            return;
        }

        // No TryParse/range check needed for these five -- unlike StandardHoursPerDayBox/
        // NetPayRoundingMultipleBox above and below, they're PercentTextBox now, which
        // wraps a NumericTextBox that already guarantees a valid, in-range Value on its
        // own (keystrokes that wouldn't leave a number are rejected outright; an
        // out-of-range commit is clamped to Minimum/Maximum -- see each field's own
        // Maximum="5" in the XAML, the same 500% ceiling TryParsePremiumPercentage used to
        // enforce by hand). The only state left to handle here is Value itself being null
        // -- the box was cleared to blank and has no PlaceholderValue to fall back to,
        // since a company-wide policy default has nothing above it to inherit from -- so
        // that falls back to whatever this policy already held rather than silently
        // adopting 0%.
        var overtimeRatePercentage = OvertimeRatePercentageBox.Value ?? _originalPayrollPolicy.OvertimeRatePercentage;
        var nightDiffRatePercentage = NightDiffRatePercentageBox.Value ?? _originalPayrollPolicy.NightDiffRatePercentage;
        var restDayPremiumPercentage = RestDayPremiumPercentageBox.Value ?? _originalPayrollPolicy.RestDayPremiumPercentage;
        var restDayOvertimeRatePercentage = RestDayOvertimeRatePercentageBox.Value ?? _originalPayrollPolicy.RestDayOvertimeRatePercentage;
        var holidayPremiumPercentage = HolidayPremiumPercentageBox.Value ?? _originalPayrollPolicy.HolidayPremiumPercentage;

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

        ChangedConnectionProfiles = _profiles.SequenceEqual(_originalConnectionProfiles)
            ? null
            : _profiles;

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
        // Both policies are built by cloning what the dialog was opened with and
        // assigning only the fields this dialog actually edits -- never by listing
        // fields in an object initializer. An initializer silently resets whatever
        // it forgets, and it forgets by default: a field added to either policy
        // class later isn't a compile error here, just a value that quietly reverts
        // to its C# default the next time someone saves Settings. That already
        // happened once, to PayrollPolicy's two rest-day rates. See
        // AttendancePolicy.Clone/PayrollPolicy.Clone.
        AttendancePolicy? changedPolicy = null;
        if (policyChanged)
        {
            changedPolicy = _originalPolicy.Clone();
            changedPolicy.ClockInBufferBefore = clockInBefore;
            changedPolicy.ClockInBufferAfter = clockInAfter;
            changedPolicy.ClockOutBufferBefore = clockOutBefore;
            changedPolicy.ClockOutBufferAfter = clockOutAfter;
            changedPolicy.FlexibleSegmentClockInBuffer = flexIn;
            changedPolicy.FlexibleSegmentClockOutBuffer = flexOut;
            changedPolicy.FlexibleMinimumBreakGap = flexMinBreakGap;
            changedPolicy.ClockOutGracePeriod = grace;
            changedPolicy.LateInEarlyOutGraceMinutes = lateEarlyGrace;
            changedPolicy.NightDiffStart = nightDiffStart;
            changedPolicy.NightDiffEnd = nightDiffEnd;
            changedPolicy.CapEarlyClockIn = capEarlyClockIn;
            changedPolicy.StrictOvertimeFromShiftEnd = strictOvertime;
            changedPolicy.UseExcelFormula = useExcelFormula;
        }
        ChangedPolicy = changedPolicy;

        var payrollPolicyChanged =
            standardHoursPerDay != _originalPayrollPolicy.StandardHoursPerDay ||
            overtimeRatePercentage != _originalPayrollPolicy.OvertimeRatePercentage ||
            nightDiffRatePercentage != _originalPayrollPolicy.NightDiffRatePercentage ||
            restDayPremiumPercentage != _originalPayrollPolicy.RestDayPremiumPercentage ||
            restDayOvertimeRatePercentage != _originalPayrollPolicy.RestDayOvertimeRatePercentage ||
            holidayPremiumPercentage != _originalPayrollPolicy.HolidayPremiumPercentage ||
            netPayRoundingMultiple != _originalPayrollPolicy.NetPayRoundingMultiple;

        PayrollPolicy? changedPayrollPolicy = null;
        if (payrollPolicyChanged)
        {
            changedPayrollPolicy = _originalPayrollPolicy.Clone();
            changedPayrollPolicy.StandardHoursPerDay = standardHoursPerDay;
            changedPayrollPolicy.OvertimeRatePercentage = overtimeRatePercentage;
            changedPayrollPolicy.NightDiffRatePercentage = nightDiffRatePercentage;
            changedPayrollPolicy.RestDayPremiumPercentage = restDayPremiumPercentage;
            changedPayrollPolicy.RestDayOvertimeRatePercentage = restDayOvertimeRatePercentage;
            changedPayrollPolicy.HolidayPremiumPercentage = holidayPremiumPercentage;
            changedPayrollPolicy.NetPayRoundingMultiple = netPayRoundingMultiple;
        }
        ChangedPayrollPolicy = changedPayrollPolicy;

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
