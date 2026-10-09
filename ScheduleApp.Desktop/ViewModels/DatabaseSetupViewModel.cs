using System.Reactive.Linq;
using Microsoft.Data.SqlClient;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Accounts;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>Which of Database Setup's two Create behaviors runs -- orthogonal to whether the
/// dialog is the required first-run gate, which only changes its wording.</summary>
public enum DatabaseSetupMode
{
    /// <summary>Creates (or reuses) a database and a SQL Server login, granting the login the
    /// chosen access -- DatabaseProvisioningService's full round trip, admin credentials and
    /// all. For a machine where Windows Authentication isn't practical; Settings tucks it under
    /// Advanced.</summary>
    DedicatedLogin,

    /// <summary>Just a server and database name, turned into a Windows-Authenticated
    /// connection string -- no SQL Server round trip, no credentials, no login. The database
    /// needn't exist: EF Core's Migrate() creates it on first connect, owned by the Windows
    /// account running the app.</summary>
    WindowsAuthOnly,
}

/// <summary>
/// Database Setup: a connection string for this app, either built from a server and database
/// name (<see cref="DatabaseSetupMode.WindowsAuthOnly"/>) or by provisioning a database and a
/// dedicated SQL login (<see cref="DatabaseSetupMode.DedicatedLogin"/>). Opened from
/// Settings' Database tab, from startup when the configured database can't be reached, and
/// -- required, before anything else -- on a machine with no shared configuration yet.
/// Prefilled from the connection string in effect, when it parses. The admin credentials are
/// used once and never saved.
/// </summary>
public partial class DatabaseSetupViewModel : ReactiveViewModel
{
    private readonly IDatabaseProvisioningService _provisioningService;

    public DatabaseSetupViewModel(
        IDatabaseProvisioningService provisioningService,
        string? currentConnectionString,
        DatabaseSetupMode mode = DatabaseSetupMode.DedicatedLogin,
        bool isRequiredFirstRun = false,
        IReadOnlyList<string>? knownServers = null)
    {
        _provisioningService = provisioningService;
        CreatesDedicatedLogin = mode == DatabaseSetupMode.DedicatedLogin;
        KnownServers = knownServers ?? [];

        var intro = CreatesDedicatedLogin
            ? "Creates a database and a SQL Server login on the server below, then grants that login access to it -- an " +
              "alternative to Windows Authentication (see ScheduleApp.PushListener's README) for a machine where that's not " +
              "practical. Running this again with the same database and login names updates the password and permissions " +
              "instead of failing."
            : "Enter the SQL Server instance and a database name below. Schedule Manager will connect to it using your " +
              "current Windows account -- no separate database username or password is needed. If the database doesn't " +
              "exist yet, it's created automatically the moment Schedule Manager first connects to it.";

        // Required: nothing to skip past, so Cancel is Exit.
        Title = isRequiredFirstRun ? "Database Setup (Required)" : "Database Setup";
        IntroText = isRequiredFirstRun
            ? "Schedule Manager needs a database before it can start, and this machine doesn't have one set up yet -- " +
              "this screen is required to continue.\n\n" + intro
            : intro;
        CancelLabel = isRequiredFirstRun ? "Exit" : "Cancel";

        if (!string.IsNullOrWhiteSpace(currentConnectionString))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(currentConnectionString);
                Server = builder.DataSource;
                DatabaseName = builder.InitialCatalog;
            }
            catch (ArgumentException)
            {
                // Not a SQL Server connection string -- the prefill was only a convenience.
            }
        }

        _isBusyHelper = CreateCommand.IsExecuting.ToProperty(this, x => x.IsBusy);
    }

    public string Title { get; }

    public string IntroText { get; }

    public string CancelLabel { get; }

    /// <summary>Whether the admin connection and new-login sections apply.</summary>
    public bool CreatesDedicatedLogin { get; }

    /// <summary>Local SQL Server instances to offer; the box still takes any name.</summary>
    public IReadOnlyList<string> KnownServers { get; }

    [Reactive]
    public partial string Server { get; set; } = string.Empty;

    [Reactive]
    public partial string DatabaseName { get; set; } = string.Empty;

    /// <summary>Connect as the current Windows account to do the provisioning, rather than a
    /// SQL admin login.</summary>
    [Reactive]
    public partial bool UseWindowsAuthForAdmin { get; set; } = true;

    [Reactive]
    public partial string AdminUsername { get; set; } = string.Empty;

    [Reactive]
    public partial string AdminPassword { get; set; } = string.Empty;

    [Reactive]
    public partial string NewLoginUsername { get; set; } = string.Empty;

    [Reactive]
    public partial string NewLoginPassword { get; set; } = string.Empty;

    [Reactive]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>db_owner, rather than just reading and writing data.</summary>
    [Reactive]
    public partial bool GrantsFullAccess { get; set; } = true;

    [Reactive]
    public partial string? ErrorMessage { get; private set; }

    [ObservableAsProperty]
    public partial bool IsBusy { get; }

    /// <summary>The connection string Create settled on -- null until it succeeds.</summary>
    public string? ConnectionString { get; private set; }

    [ReactiveCommand]
    private async Task<bool> CreateAsync()
    {
        var server = Server.Trim();
        var databaseName = DatabaseName.Trim();

        if (server.Length == 0) return Reject("Enter the SQL Server instance name.");
        if (databaseName.Length == 0) return Reject("Enter a database name.");

        if (!CreatesDedicatedLogin)
        {
            ConnectionString = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                TrustServerCertificate = true,
            }.ConnectionString;
            return true;
        }

        var adminUsername = AdminUsername.Trim();
        var newLoginUsername = NewLoginUsername.Trim();

        if (!UseWindowsAuthForAdmin && (adminUsername.Length == 0 || AdminPassword.Length == 0))
            return Reject("Enter the admin username and password, or switch to Windows (integrated).");
        if (newLoginUsername.Length == 0)
            return Reject("Enter a username for the new login.");
        if (NewLoginPassword.Length < PasswordRules.MinimumLength)
            return Reject($"The new login's password must be at least {PasswordRules.MinimumLength} characters.");
        if (NewLoginPassword != ConfirmPassword)
        {
            ConfirmPassword = string.Empty;
            return Reject("Password and confirmation don't match.");
        }

        var request = new DatabaseProvisioningRequest(
            server, databaseName, UseWindowsAuthForAdmin,
            UseWindowsAuthForAdmin ? null : adminUsername,
            UseWindowsAuthForAdmin ? null : AdminPassword,
            newLoginUsername, NewLoginPassword,
            GrantsFullAccess ? DatabaseAccessLevel.FullAccess : DatabaseAccessLevel.ReadWrite);

        ErrorMessage = null;
        try
        {
            ConnectionString = await _provisioningService.ProvisionAsync(request);
            return true;
        }
        catch (SqlException ex)
        {
            return Reject(ex.Message +
                "\n\nCommon causes: the admin credentials don't have rights to create databases/logins on this server " +
                "(sysadmin is the simplest fix), or the new login's password doesn't meet SQL Server's own password policy.");
        }
        catch (Exception ex)
        {
            return Reject(ex.Message);
        }
    }

    private bool Reject(string message)
    {
        ErrorMessage = message;
        return false;
    }
}
