using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Respawn;
using Respawn.Graph;
using Xunit;

namespace ScheduleApp.Data.Tests.Fixtures;

/// <summary>
/// The tests' own SQL Server database, ScheduleAppDb_Test_Data on the server appsettings.json
/// names -- never the real ScheduleAppDb. Created and migrated once per test run, then reset
/// with Respawn before each test (see PersistenceTestBase). No migration seeds rows, so every
/// table but EF's migration history gets the full reset.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DatabaseName = "ScheduleAppDb_Test_Data";

    private static readonly Table[] TablesExcludedFromReset = ["__EFMigrationsHistory"];

    private Respawner _respawner = null!;

    public string ConnectionString { get; }

    public DatabaseFixture()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        ConnectionString = new SqlConnectionStringBuilder(configuration.GetConnectionString("ScheduleDb"))
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;
    }

    public ScheduleDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ScheduleDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();

            // Express turns AUTO_CLOSE on for a database it creates, which shuts it down when
            // idle and makes the next run's first queries slow. Set here so a test database
            // dropped and recreated gets it too.
            await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET AUTO_CLOSE OFF");
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = TablesExcludedFromReset,
        });
    }

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
