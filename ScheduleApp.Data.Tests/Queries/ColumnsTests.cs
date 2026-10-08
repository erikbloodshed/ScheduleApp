using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ScheduleApp.Data.Queries;
using Xunit;

namespace ScheduleApp.Data.Tests.Queries;

/// <summary>
/// A Dapper read fills only the columns it selects, so a property missing from Columns would
/// read back as its default without any error. These keep the lists in step with EF's model. No
/// database: the model is built from ScheduleDbContext's configuration alone.
/// </summary>
public class ColumnsTests
{
    private static readonly IModel Model = CreateModel();

    private static IModel CreateModel()
    {
        using var db = new ScheduleDbContext(
            new DbContextOptionsBuilder<ScheduleDbContext>().UseSqlServer("Server=unused").Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Every_entity_has_a_column_list()
    {
        var entities = Model.GetEntityTypes().Select(e => e.ClrType.Name).Order();

        Assert.Equal(entities, Columns.ByEntity.Keys.Select(t => t.Name).Order());
    }

    [Theory]
    [MemberData(nameof(EntityNames))]
    public void Column_list_is_exactly_the_entitys_columns_with_Id_first(string entityName)
    {
        var entityType = Model.GetEntityTypes().Single(e => e.ClrType.Name == entityName);
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var columns = Columns.ByEntity[entityType.ClrType];

        Assert.Equal(
            entityType.GetProperties().Select(p => p.GetColumnName(table)).Order(),
            columns.Order());
        Assert.Equal("Id", columns[0]);
    }

    public static TheoryData<string> EntityNames() => [.. Model.GetEntityTypes().Select(e => e.ClrType.Name)];
}
