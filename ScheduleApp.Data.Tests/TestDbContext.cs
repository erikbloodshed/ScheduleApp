using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// SQLite-backed ScheduleDbContext for tests, not EF's InMemory provider -- InMemory
/// doesn't enforce unique indexes (Department.Name, Employee.Pin, (EmployeeId, Date),
/// (PayrollRunId, EmployeeId)) or foreign keys at all, which are exactly the invariants
/// the batching rewrite in ScheduleRepository/PayrollRunRepository is most likely to get
/// wrong. SQLite does enforce all of those (including a FK targeting a UNIQUE
/// non-primary column, which is what ScheduleEntry.Employee's HasPrincipalKey(Pin)
/// relationship needs -- see ScheduleDbContext's own remarks on that entity), so it's a
/// meaningfully real test of the same shape without needing a real SQL Server instance.
///
/// One thing it can't prove: SQL Server's default collation is case-insensitive, so
/// "Kitchen" and "kitchen" collide there; SQLite's default string comparison is
/// case-sensitive, so that specific behavior (see ScheduleRepository.
/// ResolveDepartmentsAsync's own doc comment) has to stay a manual check against a real
/// database instead.
/// </summary>
public static class TestDbContext
{
    /// <summary>Opens a fresh, isolated SQLite in-memory database, creates the schema on
    /// it, and hands back both the context and the connection keeping it alive -- an
    /// in-memory SQLite database is torn down the moment its one connection closes,
    /// unlike a file-backed one, so both must live (and be disposed) together. Dispose
    /// the returned <see cref="TestDatabase"/> once, at the end of the test.
    /// EnsureCreated, not Migrate: the project's migrations are SQL Server-specific (see
    /// ScheduleDbContext's own HasColumnType calls, stripped below), so they can't run
    /// against SQLite at all.</summary>
    public static TestDatabase Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var db = CreateContext(connection);
        db.Database.EnsureCreated();

        return new TestDatabase(db, connection);
    }

    private static SqliteFriendlyScheduleDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SqliteFriendlyScheduleDbContext(options);
    }

    /// <summary>Strips the SQL Server-specific column types ScheduleDbContext.
    /// OnModelCreating pins throughout (bit/time/float/decimal(p,s)) -- SQLite's own EF
    /// Core provider has its own (still type-correct) mappings for bool/TimeOnly/double/
    /// decimal, and the SQL Server type names collide with them rather than being
    /// ignored. Everything else from the real model -- indexes, keys, cascades,
    /// HasPrincipalKey -- is left completely alone, since that's exactly what these
    /// tests exist to exercise.</summary>
    private sealed class SqliteFriendlyScheduleDbContext(DbContextOptions<ScheduleDbContext> options)
        : ScheduleDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
                property.SetColumnType(null);
        }
    }

    /// <summary>Bundles a context with the connection keeping its in-memory database
    /// alive, and disposes both together. <see cref="NewContext"/> opens a second,
    /// independent context against the SAME already-created database -- for a test that
    /// wants to simulate "close the context that ran the write, then read back what was
    /// actually committed with a fresh one," the same way the real app's read paths
    /// (AsNoTracking queries in ScheduleRepository) never reuse the context that just
    /// wrote something. Reading back through a second context also proves the write
    /// really reached the database, rather than just looking committed because the
    /// original context's own change tracker still has the entities in memory.</summary>
    public sealed class TestDatabase(ScheduleDbContext db, SqliteConnection connection) : IDisposable
    {
        public ScheduleDbContext Db { get; } = db;

        public ScheduleDbContext NewContext() => CreateContext(connection);

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }
    }
}
