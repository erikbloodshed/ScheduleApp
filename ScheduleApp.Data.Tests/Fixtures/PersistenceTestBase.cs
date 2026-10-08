using Xunit;

namespace ScheduleApp.Data.Tests.Fixtures;

/// <summary>
/// Base for every test class that uses the database: resets it before each test, then hands the
/// test <see cref="Db"/>, the context the repository under test writes through, and
/// <see cref="NewContext"/> for reading back what was actually committed -- a second context,
/// so a write can't look saved just because the first one's change tracker still holds it.
///
/// Deliberately does NOT carry [Collection(DatabaseCollection.Name)] itself -- that attribute
/// must stay on each concrete test class. xUnit doesn't promise to honour it from a base class,
/// and if it didn't, the classes would run in parallel against the one database.
/// </summary>
public abstract class PersistenceTestBase(DatabaseFixture fixture) : IAsyncLifetime
{
    protected ScheduleDbContext Db { get; private set; } = null!;

    protected ScheduleDbContext NewContext() => fixture.CreateContext();

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Db = NewContext();
    }

    public async Task DisposeAsync()
    {
        if (Db is not null) await Db.DisposeAsync();
    }
}
