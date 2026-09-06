using System.IO;
using Microsoft.Data.SqlClient;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Native SQL Server BACKUP DATABASE / RESTORE DATABASE, backing the "Backup / Restore
/// Database" button on MainWindow (see BackupRestoreDialog) -- lets ScheduleAppDb be
/// moved to another computer without SSMS or sqlcmd: Backup writes a single .bak file
/// with everything in it, copy that file to the other machine's SQL Server Express
/// instance however you like (USB, network share, etc.), then Restore there rebuilds
/// the database from it.
///
/// Deliberately uses a plain ADO.NET connection to master (Microsoft.Data.SqlClient --
/// already referenced transitively via Microsoft.EntityFrameworkCore.SqlServer, same as
/// ScheduleRepository's own import), not the app's EF Core ScheduleDbContext: BACKUP/
/// RESTORE DATABASE are server-level administrative statements, not something
/// meaningfully expressed as an EF query, and RESTORE specifically needs a connection
/// that ISN'T to the database being replaced -- SQL Server refuses to restore over a
/// database its own caller is connected to.
///
/// Both operations act on whatever database name is in the connection string's Initial
/// Catalog (ScheduleAppDb by default -- see appsettings.json), not a fixed literal, so
/// this keeps working if that's ever renamed.
///
/// Permissions this needs on the target SQL Server (see ScheduleApp.PushListener's
/// README for the equivalent db_datareader/db_datawriter grant for that project's
/// service account -- this is the same idea, for a different pair of permissions):
///   - Backup: db_backupoperator on ScheduleAppDb (or higher).
///   - Restore: dbcreator server role or sysadmin -- restoring can create or fully
///     replace a database, which db_backupoperator alone doesn't cover.
/// On a typical single-machine deployment where Schedule Manager runs as the same
/// Windows account that installed SQL Server Express, that account is already a
/// sysadmin and both just work with no extra setup.
/// </summary>
public class DatabaseBackupService
{
    private sealed record MoveTarget(string LogicalName, string PhysicalPath);

    /// <summary>
    /// Runs BACKUP DATABASE against the database named in <paramref name="connectionString"/>,
    /// writing to <paramref name="destinationPath"/>.
    ///
    /// <paramref name="destinationPath"/> is a path on whatever machine SQL Server
    /// itself is running on -- the same machine as this app, in every deployment this
    /// project's READMEs describe (see ScheduleApp.PushListener's README, "Deployment").
    /// BACKUP DATABASE writes the file from the server process, not this one, so the SQL
    /// Server service account (not the interactive Windows user running this app) needs
    /// write access to the destination folder. Its own default backup folder always has
    /// that access -- see TryGetDefaultBackupFolderAsync, used to pre-fill the picker for
    /// exactly this reason -- a different folder (Desktop, Documents, a USB drive)
    /// usually needs access granted first, or this fails with an "Operating system error
    /// 5(Access is denied)" reported by SQL Server, not by this app.
    /// </summary>
    public async Task BackupAsync(string connectionString, string destinationPath, CancellationToken cancellationToken = default)
    {
        var (databaseName, masterConnectionString) = ToMasterConnection(connectionString);

        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync(cancellationToken);

        // BACKUP COMPRESSION is an Enterprise/Standard feature -- Express (and the old
        // Personal edition) reject the WITH COMPRESSION option entirely with "Backup
        // compression is not supported on Standard Edition, Web Edition, Express
        // Edition, ...", rather than silently ignoring it. Since this app targets
        // Express deployments (see this class's own remarks), compression can't be
        // hardcoded on -- but it's still worth using when it happens to be available,
        // so this checks the server rather than just deleting the option outright.
        var supportsCompression = await SupportsBackupCompressionAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        // No arbitrary cutoff -- a real database's backup can legitimately take minutes,
        // and the default 30s ADO.NET command timeout would otherwise abort a slow-but-
        // healthy backup partway through and report it as a failure.
        command.CommandTimeout = 0;
        command.CommandText =
            $"BACKUP DATABASE {BracketIdentifier(databaseName)} " +
            $"TO DISK = {QuoteLiteral(destinationPath)} " +
            $"WITH FORMAT, INIT{(supportsCompression ? ", COMPRESSION" : string.Empty)}, STATS = 10";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the connected SQL Server instance supports BACKUP DATABASE ... WITH
    /// COMPRESSION. Express and the legacy Personal edition never support it
    /// (EngineEdition 4); every other edition (Standard, Enterprise, Azure variants)
    /// does. Falls back to false on any failure reading the property, so an unexpected
    /// error here just means the backup proceeds uncompressed instead of failing the
    /// whole operation over a capability check.
    /// </summary>
    private static async Task<bool> SupportsBackupCompressionAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is int engineEdition && engineEdition != 4;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs RESTORE DATABASE against the database named in <paramref name="connectionString"/>,
    /// reading from <paramref name="sourcePath"/> (again, a path on the SQL Server
    /// machine itself -- see BackupAsync's remarks, same reasoning applies here for
    /// read access).
    ///
    /// If that database already exists (e.g. ScheduleApp.Desktop already created an
    /// empty one via its own Database.Migrate() on this machine's first launch -- see
    /// App.xaml.cs's OnStartup), it's replaced in place: other connections (including
    /// this app's own EF Core connection pool -- see SqlConnection.ClearAllPools below)
    /// are forced off first via SINGLE_USER WITH ROLLBACK IMMEDIATE, and the restore
    /// reuses that database's exact existing physical .mdf/.ldf paths, so this never
    /// needs to guess where SQL Server put them.
    ///
    /// If it doesn't exist yet, the restore creates it fresh, in SQL Server's own
    /// default data/log folder (queried from the server itself -- see
    /// ResolveMoveTargetsAsync) -- again, nothing here needs to know or guess that path.
    /// </summary>
    public async Task RestoreAsync(string connectionString, string sourcePath, CancellationToken cancellationToken = default)
    {
        var (databaseName, masterConnectionString) = ToMasterConnection(connectionString);

        // Drops every pooled connection this process has open for every connection
        // string it's used -- not just this one -- so nothing (including this app's own
        // ScheduleDbContext pool from earlier in the same session) is still holding a
        // connection to the database SINGLE_USER is about to force everyone else off of.
        // A one-time, whole-process reset is the safe choice for an operation this rare
        // and this disruptive by nature.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync(cancellationToken);

        var databaseExists = await DatabaseExistsAsync(connection, databaseName, cancellationToken);
        var moveTargets = await ResolveMoveTargetsAsync(connection, databaseName, sourcePath, databaseExists, cancellationToken);

        if (databaseExists)
        {
            await ExecuteNonQueryAsync(
                connection,
                $"ALTER DATABASE {BracketIdentifier(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                cancellationToken);
        }

        try
        {
            var moveClauses = string.Join(
                ", ",
                moveTargets.Select(t => $"MOVE {QuoteLiteral(t.LogicalName)} TO {QuoteLiteral(t.PhysicalPath)}"));

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0; // Same reasoning as BackupAsync.
            command.CommandText =
                $"RESTORE DATABASE {BracketIdentifier(databaseName)} " +
                $"FROM DISK = {QuoteLiteral(sourcePath)} " +
                $"WITH REPLACE, {moveClauses}, STATS = 10";

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            // Always put the database back in normal, shared use -- even if RESTORE
            // itself failed after SINGLE_USER already took effect above, so a failed
            // restore attempt doesn't also leave a previously-working database locked to
            // one connection until someone notices and fixes it by hand.
            await ExecuteNonQueryAsync(
                connection,
                $"IF DB_ID({QuoteLiteral(databaseName)}) IS NOT NULL " +
                $"ALTER DATABASE {BracketIdentifier(databaseName)} SET MULTI_USER",
                CancellationToken.None);
        }
    }

    /// <summary>
    /// The SQL Server instance's own default backup folder -- used to pre-fill the
    /// Backup/Restore dialog's file picker, since the SQL Server service account is
    /// guaranteed access there already (see BackupAsync's remarks), unlike an arbitrary
    /// user folder which usually needs access granted first. Still just a starting
    /// point -- the picker lets you navigate anywhere. Returns null (rather than
    /// throwing) if this can't be determined, so a picker that can't pre-fill a folder
    /// still opens with no starting folder instead of blocking Backup/Restore on
    /// something neither actually requires to work.
    /// </summary>
    public async Task<string?> TryGetDefaultBackupFolderAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, masterConnectionString) = ToMasterConnection(connectionString);
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(500))";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> DatabaseExistsAsync(SqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@databaseName)";
        command.Parameters.AddWithValue("@databaseName", databaseName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    /// <summary>
    /// Works out where RESTORE's MOVE clause should point each file in the backup.
    /// ScheduleAppDb has always been a single data file + single log file (see the
    /// migrations this schema came from), so matching backup files to targets by type
    /// (D = data, L = log) alone is unambiguous -- this doesn't attempt to support a
    /// database that's since grown extra files by hand outside of EF Core.
    /// </summary>
    private static async Task<List<MoveTarget>> ResolveMoveTargetsAsync(
        SqlConnection connection,
        string databaseName,
        string sourcePath,
        bool databaseExists,
        CancellationToken cancellationToken)
    {
        // RESTORE FILELISTONLY reads the backup file's own manifest -- the logical file
        // names and types baked in when ScheduleAppDb.bak was created by BackupAsync.
        // These identify which stream is which; only the *physical* target path (below)
        // changes from one restore to the next.
        var backupFiles = new List<(string LogicalName, string Type)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"RESTORE FILELISTONLY FROM DISK = {QuoteLiteral(sourcePath)}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var logicalNameOrdinal = reader.GetOrdinal("LogicalName");
            var typeOrdinal = reader.GetOrdinal("Type");
            while (await reader.ReadAsync(cancellationToken))
            {
                backupFiles.Add((reader.GetString(logicalNameOrdinal), reader.GetString(typeOrdinal)));
            }
        }

        if (databaseExists)
        {
            // Reuse the exact physical paths the existing database already has.
            var existingPathByType = new Dictionary<string, string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT type_desc, physical_name FROM sys.master_files WHERE database_id = DB_ID(@databaseName)";
                command.Parameters.AddWithValue("@databaseName", databaseName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingPathByType[reader.GetString(0)] = reader.GetString(1); // "ROWS" or "LOG"
                }
            }

            return backupFiles
                .Select(f => new MoveTarget(f.LogicalName, existingPathByType[f.Type == "D" ? "ROWS" : "LOG"]))
                .ToList();
        }

        // Fresh database -- put the files exactly where SQL Server itself would put them
        // for a brand-new one, so this doesn't need to know or guess the instance's data
        // folder (which varies by SQL Server version and instance name).
        string defaultDataPath, defaultLogPath;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(500)), " +
                "CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(500))";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            defaultDataPath = reader.GetString(0);
            defaultLogPath = reader.GetString(1);
        }

        return backupFiles
            .Select(f => new MoveTarget(
                f.LogicalName,
                Path.Combine(
                    f.Type == "D" ? defaultDataPath : defaultLogPath,
                    databaseName + (f.Type == "D" ? ".mdf" : "_log.ldf"))))
            .ToList();
    }

    /// <summary>Splits a ScheduleDb connection string into the database name it points
    /// at and an equivalent connection string pointed at master instead -- see this
    /// class's own remarks for why BACKUP/RESTORE always run against master.</summary>
    private static (string DatabaseName, string MasterConnectionString) ToMasterConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "The connection string has no Initial Catalog/Database -- can't tell which database to back up or restore.");
        }

        builder.InitialCatalog = "master";
        return (databaseName, builder.ConnectionString);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BracketIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}