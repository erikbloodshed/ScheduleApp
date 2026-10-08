using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace ScheduleApp.Data.Tests.Fixtures;

/// <summary>
/// Every test class that touches the database is in this one collection, so xUnit runs them one
/// at a time against the one shared database instead of resetting it under each other.
/// </summary>
[CollectionDefinition(Name)]
[SuppressMessage("Naming", "CA1711", Justification = "xUnit names a collection definition class after the collection it defines.")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}
