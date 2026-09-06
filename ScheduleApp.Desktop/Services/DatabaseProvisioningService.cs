using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ScheduleApp.Desktop.Services;

/// <summary>Which SQL Server database roles the new login is added to on the database
/// this creates/reuses. Mirrors the two roles ScheduleApp.PushListener's own README
/// already documents for the two accounts this app cares about: FullAccess is what
/// ScheduleApp.Desktop itself needs (it calls Database.Migrate() on startup -- see
/// App.xaml.cs -- which needs enough permission to create/alter tables, not just read
/// and write rows in ones that already exist), ReadWrite is what the README's own
/// CREATE LOGIN / ALTER ROLE example grants the Push Listener service account.</summary>
public enum DatabaseAccessLevel
{
    /// <summary>db_owner -- can create/alter the schema (run migrations), not just
    /// read/write existing tables. What ScheduleApp.Desktop itself needs.</summary>
    FullAccess,

    /// <summary>db_datareader + db_datawriter -- can read and write rows in tables that
    /// already exist, but can't create or alter the schema. What
    /// ScheduleApp.PushListener needs (see its README's Deployment step 1); it only
    /// ever reads/writes existing tables and deliberately never migrates.</summary>
    ReadWrite
}

/// <summary>Everything DatabaseProvisioningService.ProvisionAsync needs. The "admin"
/// credentials (UseWindowsAuthForAdmin/AdminUsername/AdminPassword) are only ever used
/// for the handful of connections ProvisionAsync itself opens -- see that method's own
/// remarks for why they're never persisted anywhere, including by the caller (compare
/// DatabaseSetupDialog, which never stores them on itself past the Create button's
/// click handler either).
///
/// Only ever constructed by DatabaseSetupDialog's own two "create a dedicated SQL
/// login" entry points (SettingsDialog's "Create New Database / Login…" button, and
/// the reactive Migrate()-failure path) -- the third, required-first-run entry point
/// never touches DatabaseProvisioningService at all, and so never constructs one of
/// these: it hands back a Windows-Authenticated connection string directly (see
/// DatabaseSetupDialog's own doc comment), letting App.xaml.cs's fall-through
/// Database.Migrate() create the database itself instead of a separate admin-driven
/// CREATE DATABASE/LOGIN/USER sequence here. Nothing about this app's first run needs
/// a SQL Server login at all -- the only username/password it establishes is
/// SetupAdminPanel's own app account, further down in startup.</summary>
public sealed record DatabaseProvisioningRequest(
    string ServerName,
    string DatabaseName,
    bool UseWindowsAuthForAdmin,
    string? AdminUsername,
    string? AdminPassword,
    string NewLoginUsername,
    string NewLoginPassword,
    DatabaseAccessLevel AccessLevel);

/// <summary>
/// Creates (or reuses) a SQL Server database, a SQL Server login, and a matching
/// database user for that login -- what SettingsDialog's "Create New Database /
/// Login…" button and App.xaml.cs's own reactive Migrate()-failure path (see its
/// catch block) both call, so someone setting Schedule Manager up on a fresh machine
/// doesn't need SQL Server Management Studio, sqlcmd, or ScheduleApp.PushListener's
/// README's own hand-typed T-SQL to get a working ConnectionStrings:ScheduleDb. Not
/// used by App.xaml.cs's own required-first-run gate -- see
/// DatabaseProvisioningRequest's own doc comment for why that path never needs a SQL
/// Server login in the first place.
///
/// Deliberately uses a plain ADO.NET connection (Microsoft.Data.SqlClient -- already
/// referenced transitively via Microsoft.EntityFrameworkCore.SqlServer, same as
/// DatabaseBackupService's own import), not EF Core: CREATE DATABASE/CREATE
/// LOGIN/CREATE USER/ALTER ROLE are server- and database-level administrative
/// statements, not something meaningfully expressed as an EF query, and the first two
/// specifically need to run against master with an admin connection that has nothing
/// to do with ScheduleDbContext's own connection string (which, before this runs, may
/// not even point at a database that exists yet).
///
/// Every identifier this touches (database name, login name) is validated against
/// <see cref="IdentifierPattern"/> before anything reaches SQL Server, so building
/// each SQL statement by bracket-quoting the identifier directly in C# (see
/// BracketIdentifier -- the exact same helper DatabaseBackupService already uses for
/// the same reason) is safe: there's no character that pattern allows through which
/// could break out of a bracketed identifier. The one value that legitimately can
/// contain arbitrary characters -- the new login's password -- is never concatenated
/// in unescaped: CREATE LOGIN/ALTER LOGIN's PASSWORD clause doesn't accept a bound
/// SqlParameter at all (see CreateOrUpdateLoginAsync's own remarks for the exact
/// error that produces), so this embeds it as a properly-escaped T-SQL string
/// literal instead (see SqlStringLiteral), the same quote-doubling rule
/// BracketIdentifier already applies to `]` in identifiers, just for `'` in a string.
/// </summary>
public class DatabaseProvisioningService
{
    /// <summary>SQL Server identifier rules are looser than this in practice (they
    /// allow a lot more than this pattern does), but a brand-new database/login name
    /// someone's about to type into a wizard has no reason to need anything outside
    /// plain ASCII letters/digits/underscore -- keeping this strict is what lets
    /// BracketIdentifier below skip any real escaping logic and just wrap the value in
    /// brackets, since nothing this pattern allows can contain a `]` to begin with.</summary>
    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private static readonly string[] ManagedRoles = { "db_owner", "db_datareader", "db_datawriter" };

    /// <summary>
    /// Runs the whole provisioning flow against <paramref name="request"/>.ServerName:
    ///   1. Connects to master with the admin credentials and creates
    ///      <paramref name="request"/>.DatabaseName if it doesn't already exist.
    ///   2. Creates <paramref name="request"/>.NewLoginUsername as a SQL Server login
    ///      there if it doesn't exist yet, or resets its password if it does -- so
    ///      re-running this later against the same login name is how a password gets
    ///      rotated, not an error (see DatabaseSetupDialog, which is reachable both
    ///      from Settings and from the reactive Migrate()-failure path).
    ///   3. Connects to the (now-existing) target database, still with the admin
    ///      credentials, creates a database user for that login if one doesn't exist,
    ///      and reconciles its membership in the three roles this class manages
    ///      (<see cref="ManagedRoles"/>) to match <paramref name="request"/>.AccessLevel
    ///      exactly -- adding whichever of db_owner/db_datareader/db_datawriter the
    ///      chosen level needs and removing the others, so switching a login from Full
    ///      access to Read & write only (or back) on a second run actually changes its
    ///      effective permissions instead of only ever adding more.
    ///
    /// Returns a connection string for the new login against the target database --
    /// always SQL Server authentication (User Id/Password), regardless of whether the
    /// admin credentials used to create it were Windows or SQL auth, since the whole
    /// point of this flow is handing back a login someone can put straight into
    /// ConnectionStrings:ScheduleDb.
    ///
    /// Throws ArgumentException if DatabaseName or NewLoginUsername fails
    /// <see cref="IdentifierPattern"/>, or SqlException for anything SQL Server itself
    /// rejects -- most commonly CHECK_POLICY (SQL Server's own default password
    /// complexity rule, left on for every login this creates) turning down a weak
    /// password, or the admin credentials not actually having admin rights on the
    /// target server. Callers (see DatabaseSetupDialog.CreateButton_Click) are expected
    /// to show SqlException.Message to the user rather than let it propagate further.
    /// </summary>
    public async Task<string> ProvisionAsync(DatabaseProvisioningRequest request, CancellationToken cancellationToken = default)
    {
        if (!IdentifierPattern.IsMatch(request.DatabaseName))
            throw new ArgumentException(
                "Database name must start with a letter or underscore and contain only letters, digits, and underscores.");

        if (!IdentifierPattern.IsMatch(request.NewLoginUsername))
            throw new ArgumentException(
                "Login name must start with a letter or underscore and contain only letters, digits, and underscores.");

        var adminMasterConnectionString = BuildConnectionString(
            request.ServerName, "master", request.UseWindowsAuthForAdmin, request.AdminUsername, request.AdminPassword);

        await using (var connection = new SqlConnection(adminMasterConnectionString))
        {
            await connection.OpenAsync(cancellationToken);

            if (!await DatabaseExistsAsync(connection, request.DatabaseName, cancellationToken))
                await ExecuteNonQueryAsync(connection, $"CREATE DATABASE {BracketIdentifier(request.DatabaseName)};", cancellationToken);

            await CreateOrUpdateLoginAsync(connection, request.NewLoginUsername, request.NewLoginPassword, cancellationToken);
        }

        var adminTargetConnectionString = BuildConnectionString(
            request.ServerName, request.DatabaseName, request.UseWindowsAuthForAdmin, request.AdminUsername, request.AdminPassword);

        await using (var connection = new SqlConnection(adminTargetConnectionString))
        {
            await connection.OpenAsync(cancellationToken);

            if (!await DatabaseUserExistsAsync(connection, request.NewLoginUsername, cancellationToken))
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"CREATE USER {BracketIdentifier(request.NewLoginUsername)} FOR LOGIN {BracketIdentifier(request.NewLoginUsername)};",
                    cancellationToken);
            }

            await ReconcileRoleMembershipAsync(connection, request.NewLoginUsername, request.AccessLevel, cancellationToken);
        }

        return BuildConnectionString(
            request.ServerName, request.DatabaseName, false,
            request.NewLoginUsername, request.NewLoginPassword);
    }

    /// <summary>Creates the login with CHECK_POLICY left on (SQL Server's own default
    /// password-complexity rule -- this never turns it off, so a weak password is
    /// rejected by SQL Server itself with a clear SqlException rather than silently
    /// accepted), or resets its password if a login with that name already exists.
    /// The password is embedded as an escaped T-SQL string literal (see
    /// SqlStringLiteral), NOT passed as a SqlParameter -- CREATE LOGIN/ALTER LOGIN's
    /// PASSWORD clause doesn't accept a bound parameter there at all: Microsoft.Data.
    /// SqlClient sends a parameterized SqlCommand as `exec sp_executesql N'...',
    /// N'@password ...', @password = N'...'`, and CREATE LOGIN's own grammar doesn't
    /// resolve `@password` inside that inner batch the way an ordinary SELECT/INSERT
    /// would -- SQL Server rejects it outright with "Incorrect syntax near
    /// '@password'." SqlStringLiteral is safe here the same way BracketIdentifier is
    /// safe for identifiers above: doubling every single quote is the standard,
    /// complete escaping rule for a T-SQL string literal (unlike identifiers, T-SQL
    /// string literals have no other character that needs escaping), and this is the
    /// one value in this class that's expected to contain arbitrary characters in the
    /// first place, so escaping (rather than IdentifierPattern-style rejection) is
    /// the right tool here.</summary>
    private static async Task CreateOrUpdateLoginAsync(
        SqlConnection connection, string loginName, string password, CancellationToken cancellationToken)
    {
        var exists = await LoginExistsAsync(connection, loginName, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = exists
            ? $"ALTER LOGIN {BracketIdentifier(loginName)} WITH PASSWORD = {SqlStringLiteral(password)};"
            : $"CREATE LOGIN {BracketIdentifier(loginName)} WITH PASSWORD = {SqlStringLiteral(password)}, CHECK_POLICY = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>type = 'S' is a SQL Server login specifically (as opposed to 'U'/'G',
    /// a Windows user or group login) -- this only ever creates SQL logins, so this is
    /// the right filter for "does a login by this name already exist" here, unlike
    /// PushListener's README, which is provisioning a Windows login and would need to
    /// check 'U' instead.</summary>
    private static async Task<bool> LoginExistsAsync(SqlConnection connection, string loginName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sys.server_principals WHERE name = @loginName AND type = 'S'";
        command.Parameters.AddWithValue("@loginName", loginName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private static async Task<bool> DatabaseUserExistsAsync(SqlConnection connection, string loginName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sys.database_principals WHERE name = @loginName";
        command.Parameters.AddWithValue("@loginName", loginName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    /// <summary>Adds/drops membership in each of <see cref="ManagedRoles"/> so the
    /// login ends up a member of exactly the roles <paramref name="accessLevel"/>
    /// implies -- FullAccess means db_owner alone; ReadWrite means db_datareader and
    /// db_datawriter alone, not db_owner too. Doing this as a reconciliation (checking
    /// IS_ROLEMEMBER before each ALTER ROLE, one role at a time) rather than
    /// unconditionally adding the wanted roles is what makes re-running this against
    /// an existing login idempotent and lets switching access levels actually revoke
    /// the level it's switching away from, instead of only ever accumulating more
    /// access on every run. Never touches any role outside this array, so a role
    /// someone granted by hand outside this dialog is left alone either way.</summary>
    private static async Task ReconcileRoleMembershipAsync(
        SqlConnection connection, string loginName, DatabaseAccessLevel accessLevel, CancellationToken cancellationToken)
    {
        var desiredRoles = accessLevel == DatabaseAccessLevel.FullAccess
            ? new[] { "db_owner" }
            : new[] { "db_datareader", "db_datawriter" };

        foreach (var role in ManagedRoles)
        {
            var shouldBeMember = desiredRoles.Contains(role);
            var isMember = await IsRoleMemberAsync(connection, role, loginName, cancellationToken);

            if (shouldBeMember && !isMember)
                await ExecuteNonQueryAsync(connection, $"ALTER ROLE {BracketIdentifier(role)} ADD MEMBER {BracketIdentifier(loginName)};", cancellationToken);
            else if (!shouldBeMember && isMember)
                await ExecuteNonQueryAsync(connection, $"ALTER ROLE {BracketIdentifier(role)} DROP MEMBER {BracketIdentifier(loginName)};", cancellationToken);
        }
    }

    private static async Task<bool> IsRoleMemberAsync(
        SqlConnection connection, string role, string loginName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IS_ROLEMEMBER(@role, @loginName)";
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@loginName", loginName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is 1;
    }

    private static async Task<bool> DatabaseExistsAsync(SqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@databaseName)";
        command.Parameters.AddWithValue("@databaseName", databaseName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildConnectionString(
        string serverName, string databaseName, bool useWindowsAuth, string? username, string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = databaseName,
            TrustServerCertificate = true
        };

        if (useWindowsAuth)
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = username ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    /// <summary>Same helper, for the same reason, as DatabaseBackupService's own
    /// BracketIdentifier -- see this class's own remarks for why bracket-quoting
    /// alone (no further escaping) is safe here: every identifier reaching this has
    /// already passed <see cref="IdentifierPattern"/>, which admits no `]`.</summary>
    private static string BracketIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    /// <summary>Escapes <paramref name="value"/> for use as a single-quoted T-SQL
    /// string literal -- see CreateOrUpdateLoginAsync's own remarks for why the
    /// password has to be embedded as a literal rather than passed as a SqlParameter.
    /// Doubling every single quote is the complete escaping rule for a T-SQL string
    /// literal (there's no backslash-escaping or other special character the way some
    /// other SQL dialects have), so unlike BracketIdentifier above -- which relies on
    /// IdentifierPattern already having rejected anything it can't safely
    /// bracket-quote -- this doesn't need <paramref name="value"/> to be restricted to
    /// any particular character set first; it's correct for arbitrary input.</summary>
    private static string SqlStringLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
