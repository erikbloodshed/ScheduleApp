using System.Windows;
using Microsoft.Data.SqlClient;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.Views;

/// <summary>Which of this dialog's two entirely different Create behaviors runs --
/// see DatabaseSetupDialog's own doc comment. Explicit rather than derived from
/// isRequiredFirstRun (the pre-mode shape this replaced): isRequiredFirstRun is
/// about *framing* (mandatory, no skipping -- see its own doc comment on the
/// constructor), which is an orthogonal question from *which Create path runs* --
/// SettingsDialog's Database tab needs WindowsAuthOnly reachable from an entirely
/// optional context, something isRequiredFirstRun alone could never express.</summary>
public enum DatabaseSetupMode
{
    /// <summary>Creates (or reuses) a database, a SQL Server login on it, and grants
    /// that login the chosen access level -- the full DatabaseProvisioningService
    /// round trip, admin credentials and all. The escape hatch for a machine where
    /// Windows Authentication "isn't practical" (see IntroText's own wording) --
    /// SettingsDialog's Database tab tucks this behind its Advanced section, since
    /// WindowsAuthOnly below is what most machines actually want.</summary>
    DedicatedLogin,

    /// <summary>Just a Server/Database name -> a Windows-Authenticated connection
    /// string, synchronously, no SQL Server round trip and no admin credentials or
    /// login of any kind -- see CreateButton_Click's own comment on this branch.</summary>
    WindowsAuthOnly
}

/// <summary>
/// Runs DatabaseProvisioningService end to end (DatabaseSetupMode.DedicatedLogin) or
/// just synthesizes a Windows-Authenticated connection string
/// (DatabaseSetupMode.WindowsAuthOnly) -- see DatabaseSetupMode's own doc comment for
/// which. Reachable from three places:
///   - SettingsDialog's Database tab, which now opens this in WindowsAuthOnly mode
///     from its primary "Create New Database…" button, and in DedicatedLogin mode
///     from its Advanced section's "Dedicated SQL Login Wizard…" button. Either way,
///     prefilled from whatever connection string is currently in effect there, and
///     on success just copies the result back into ConnectionStringBox (the person
///     still has to hit Settings' own Save to actually apply it -- same as any other
///     Settings field).
///   - App.xaml.cs's own Migrate() failure path on startup -- opened in the default
///     DedicatedLogin mode, offered as a fix when the configured connection string
///     doesn't work at all yet (e.g. a completely fresh machine with SQL Server
///     Express installed but nothing set up in it), so the very first run doesn't
///     require SQL Server Management Studio, sqlcmd, or hand-typing the T-SQL from
///     ScheduleApp.PushListener's own README.
///
/// This dialog itself only collects input, shows progress, and reports the result --
/// see DatabaseProvisioningService's own remarks for what actually happens against
/// SQL Server and why (CREATE DATABASE/LOGIN/USER, role reconciliation, the admin
/// credentials never being saved anywhere).
///
/// A third entry point -- App.xaml.cs's own pre-configuration first-run gate -- opens
/// this in WindowsAuthOnly mode too, but also passes isRequiredFirstRun: true, which
/// swaps in different *framing* (see that parameter's own doc comment below) on top
/// of the same WindowsAuthOnly Create behavior SettingsDialog's primary button now
/// also uses: it collapses both the "Admin connection" and "New login"/"Access level"
/// sections (see AdminConnectionPanel/LoginProvisioningPanel in the XAML) down to
/// just Server/Database name, and Create doesn't touch DatabaseProvisioningService or
/// SQL Server at all -- it just hands back a Windows-Authenticated connection string
/// for whatever was typed, synchronously, with no admin credentials and no SQL Server
/// login of any kind (see DatabaseProvisioningRequest.NewLoginUsername's own doc
/// comment for why that's the whole point of this required-first-run case: the only
/// username/password this app's first run is meant to establish is SetupAdminPanel's
/// own app account, further down in startup).
///
/// The database doesn't need to exist yet for that to work: App.xaml.cs's own
/// fall-through after this gate calls Database.Migrate(), and EF Core's Migrate()
/// creates the database itself (not just the schema) when it doesn't exist yet, using
/// this exact connection string -- so the Windows account running Schedule Manager
/// becomes its owner automatically the same way it would running any other
/// CREATE DATABASE, with nothing extra for this dialog to orchestrate. Only if that
/// Windows account genuinely lacks rights to create a database on the target server
/// does this fail -- and it fails where App.xaml.cs already has a recovery path built
/// in (the reactive Migrate()-failure MessageBox, offering this same dialog again,
/// this time in DedicatedLogin mode with the full Admin connection/New login sections
/// available).
///
/// Shown before appsettings.json's connection string is even attempted, on any
/// machine that doesn't have SharedConfigFile's shared.appsettings.json yet, so a
/// database always gets set up before SetupAdminPanel's own first-account
/// username/password rather than only reactively (or not at all, on a machine where
/// the bundled Trusted_Connection default happens to already work). See that caller's
/// own comment for why Cancel/the window's X button closing this without a successful
/// Create both end the app there the same way SetupAdminPanel's own Exit does --
/// nothing this screen is required for exists yet at that point in startup.
/// </summary>
public partial class DatabaseSetupDialog : Wpf.Ui.Controls.FluentWindow
{
    private const int MinimumPasswordLength = 8;

    private readonly DatabaseProvisioningService _provisioningService;

    /// <summary>True only for DatabaseSetupMode.DedicatedLogin -- see that enum's own
    /// doc comment for why that one, and only that one, touches
    /// DatabaseProvisioningService or SQL Server at all. Set once, in the constructor,
    /// from the mode parameter; read by CreateButton_Click to decide which of its two
    /// entirely different Create behaviors to run. Kept as its own field (rather than
    /// switching on the mode parameter directly everywhere) since it's a simple bool
    /// every downstream visibility/branch check already keyed off before the mode
    /// parameter existed, and there was no reason to touch those once the *only*
    /// thing that changed was how this one field gets set.</summary>
    private readonly bool _createDedicatedLogin;

    /// <summary>Set once Create succeeds -- null until then. MainWindow.xaml.cs's
    /// SettingsDialog caller and App.xaml.cs's startup-failure caller both check this
    /// (alongside DialogResult) after ShowDialog() returns.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>currentConnectionString is whatever connection string was already in
    /// effect when this dialog was opened -- BackupRestoreDialog/SettingsDialog's own
    /// source (see MainWindow.SettingsButton_Click) at the Settings entry point, or
    /// the one App.xaml.cs just failed to connect with at the startup entry point (or,
    /// for the pre-configuration first-run gate, the local appsettings.json's own
    /// bundled default, best-effort read since real configuration loading hasn't
    /// happened yet at that point -- see that caller's own comment). Used only to
    /// prefill Server/Database name (best-effort either way -- a connection string
    /// that doesn't parse, or is null/blank, e.g. nothing configured yet, just leaves
    /// those fields blank rather than failing to open the dialog at all).
    ///
    /// mode selects which of CreateButton_Click's two entirely different Create
    /// behaviors runs -- see DatabaseSetupMode's own doc comment. Defaults to
    /// DedicatedLogin, the pre-mode behavior every existing optional entry point
    /// (SettingsDialog's own Advanced button, the reactive Migrate()-failure path)
    /// still gets unless it opts into WindowsAuthOnly explicitly.
    ///
    /// isRequiredFirstRun is a separate, orthogonal axis -- *framing*, not *which
    /// Create path runs* (mode already decides that on its own): true swaps in
    /// mandatory-first-run wording -- the title, the intro text (prepended, not
    /// replaced, so the paragraph explaining what Create actually does still shows),
    /// and the Cancel button's label (-> "Exit", matching SetupAdminPanel's own
    /// button for the same "nothing to skip past here" situation). ShowDialog() still
    /// just returns true/false and ConnectionString either way, so the caller alone
    /// decides what closing without a successful Create means. Defaults to false so
    /// every entry point except App.xaml.cs's own first-run gate is unaffected.</summary>
    public DatabaseSetupDialog(
        DatabaseProvisioningService provisioningService,
        string? currentConnectionString,
        DatabaseSetupMode mode = DatabaseSetupMode.DedicatedLogin,
        bool isRequiredFirstRun = false)
    {
        InitializeComponent();
        _provisioningService = provisioningService;
        _createDedicatedLogin = mode == DatabaseSetupMode.DedicatedLogin;

        IntroText.Text = _createDedicatedLogin
            ? "Creates a database and a SQL Server login on the server below, then grants that login access to it -- an alternative to Windows Authentication (see ScheduleApp.PushListener's README) for a machine where that's not practical. Running this again with the same database and login names updates the password and permissions instead of failing."
            : "Enter the SQL Server instance and a database name below. Schedule Manager will connect to it using your current Windows account -- no separate database username or password is needed. If the database doesn't exist yet, it's created automatically the moment Schedule Manager first connects to it.";

        AdminConnectionPanel.Visibility = _createDedicatedLogin ? Visibility.Visible : Visibility.Collapsed;
        LoginProvisioningPanel.Visibility = _createDedicatedLogin ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(currentConnectionString))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(currentConnectionString);
                ServerBox.Text = builder.DataSource;
                DatabaseNameBox.Text = builder.InitialCatalog;
            }
            catch (ArgumentException)
            {
                // Not a parseable SQL Server connection string -- leave both fields
                // blank rather than failing to open the dialog over a prefill that
                // was never essential to begin with.
            }
        }

        if (isRequiredFirstRun)
        {
            Title = "Database Setup (Required)";
            IntroText.Text =
                "Schedule Manager needs a database before it can start, and this machine " +
                "doesn't have one set up yet -- this screen is required to continue.\n\n" +
                IntroText.Text;
            CancelButton.Content = "Exit";
        }

        Loaded += (_, _) => ServerBox.Focus();

        // Synchronous -- see SqlServerDiscovery's own doc comment for why (a registry
        // read, never network discovery). ServerBox stays editable regardless of
        // whether this finds anything, so an empty result never blocks typing a
        // server name by hand.
        ServerBox.ItemsSource = SqlServerDiscovery.DiscoverServers();
    }

    /// <summary>Shows/hides AdminUsernameBox/AdminPasswordBox -- only relevant, and
    /// only required, when SQL Server login is the chosen admin auth mode. Fires once
    /// for the radio button being unchecked and once for the other being checked, but
    /// both handlers set the same Visibility from the same IsChecked read, so the
    /// order doesn't matter.
    ///
    /// AdminWindowsAuthRadio's IsChecked="True" in the XAML fires this Checked handler
    /// synchronously during InitializeComponent(), before InitializeComponent() has
    /// reached the later-declared AdminSqlAuthRadio/AdminCredentialsPanel fields --
    /// both are still null at that point, so bail out rather than dereferencing them.
    /// Safe to skip: AdminCredentialsPanel is already Collapsed by default in XAML,
    /// which is the correct state for Windows auth being the one that's checked.</summary>
    private void AdminAuthRadio_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (AdminCredentialsPanel is null || AdminSqlAuthRadio is null)
        {
            return;
        }

        AdminCredentialsPanel.Visibility = AdminSqlAuthRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        var databaseName = DatabaseNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(server))
        {
            ShowError("Enter the SQL Server instance name.");
            return;
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            ShowError("Enter a database name.");
            return;
        }

        // Required-first-run's whole path: no admin connection, no SQL Server login of
        // any kind (see this class's own doc comment) -- just a Windows-Authenticated
        // connection string for whatever Server/DatabaseName was typed, built and
        // returned synchronously with no SQL Server round trip here at all. The
        // database itself doesn't need to exist yet: App.xaml.cs's own fall-through
        // Migrate() call is what actually creates it (EF Core's own Database.Migrate()
        // creates the database, not just the schema, when it doesn't exist), using
        // this exact connection, the moment normal startup resumes.
        if (!_createDedicatedLogin)
        {
            ConnectionString = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;
            DialogResult = true;
            return;
        }

        var useWindowsAuthForAdmin = AdminWindowsAuthRadio.IsChecked == true;
        var adminUsername = AdminUsernameBox.Text.Trim();
        var adminPassword = AdminPasswordBox.Password;
        var newLoginUsername = NewLoginUsernameBox.Text.Trim();
        var newLoginPassword = NewLoginPasswordBox.Password;
        var confirmPassword = ConfirmPasswordBox.Password;
        var accessLevel = FullAccessRadio.IsChecked == true
            ? DatabaseAccessLevel.FullAccess
            : DatabaseAccessLevel.ReadWrite;

        if (!useWindowsAuthForAdmin && (string.IsNullOrWhiteSpace(adminUsername) || adminPassword.Length == 0))
        {
            ShowError("Enter the admin username and password, or switch to Windows (integrated).");
            return;
        }

        if (string.IsNullOrWhiteSpace(newLoginUsername))
        {
            ShowError("Enter a username for the new login.");
            return;
        }

        if (newLoginPassword.Length < MinimumPasswordLength)
        {
            ShowError($"The new login's password must be at least {MinimumPasswordLength} characters.");
            return;
        }

        if (newLoginPassword != confirmPassword)
        {
            ShowError("Password and confirmation don't match.");
            ConfirmPasswordBox.Clear();
            ConfirmPasswordBox.Focus();
            return;
        }

        var request = new DatabaseProvisioningRequest(
            server, databaseName, useWindowsAuthForAdmin,
            useWindowsAuthForAdmin ? null : adminUsername,
            useWindowsAuthForAdmin ? null : adminPassword,
            newLoginUsername, newLoginPassword, accessLevel);

        CreateButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            ConnectionString = await _provisioningService.ProvisionAsync(request);
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
        }
        catch (SqlException ex)
        {
            ShowError(
                ex.Message +
                "\n\nCommon causes: the admin credentials don't have rights to create databases/logins on " +
                "this server (sysadmin is the simplest fix), or the new login's password doesn't meet SQL " +
                "Server's own password policy.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            CreateButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
