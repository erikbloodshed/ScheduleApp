using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ScheduleApp.Data.Queries;

/// <summary>
/// Reads go through Dapper and writes through EF Core. A read borrows the ScheduleDbContext's own
/// connection, so it uses the same connection string as EF, and Dapper opens and closes the
/// connection around the query when EF hasn't already opened it. On a context holding a
/// transaction, a read joins that transaction (<see cref="Command"/>).
///
/// Outside a transaction a read runs through the context's execution strategy, so it keeps the
/// transient-fault retries App.xaml.cs and PushListener's Program.cs configure with
/// EnableRetryOnFailure -- the same retries the EF queries these replaced got automatically.
/// Inside one it runs once: a retry can't replay the rest of the transaction.
///
/// The context is still one per app session (see App.xaml.cs), so these reads share EF's single
/// connection the same way its own queries did, and must never run concurrently with another
/// read or a save on the same context -- the callers already serialize their work for EF's sake.
/// </summary>
internal static class DapperReads
{
    /// <summary>
    /// The ids in <c>@ids</c> (an <see cref="IdList"/>) as a one-column table, for
    /// <c>WHERE x IN (…)</c>. One parameter however many ids, so it never runs into SQL
    /// Server's limit of 2,100 parameters. The empty-value filter is what makes an empty list
    /// match nothing: STRING_SPLIT('') returns one empty row, which CAST would turn into 0.
    /// </summary>
    public const string IdsTable = "SELECT CAST(value AS int) FROM STRING_SPLIT(@ids, ',') WHERE value <> ''";

    static DapperReads()
    {
        // Dapper sends a DateTime as SQL Server's legacy datetime, rounded to 1/300 s. Every
        // DateTime column here is EF's default datetime2, and a rounded bound can move a range
        // edge (23:59:59.9999999 rounds up to the next midnight), so send datetime2 instead.
        SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);
    }

    public static string IdList(IEnumerable<int> ids) => string.Join(',', ids);

    public static DbConnection Connection(this ScheduleDbContext db) => db.Database.GetDbConnection();

    /// <summary>A command on <paramref name="db"/>'s connection, in its transaction if it has one.</summary>
    public static CommandDefinition Command(
        this ScheduleDbContext db, string sql, object? parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken: cancellationToken);

    /// <summary>Runs <paramref name="read"/>, retried on a transient failure when there's no transaction to replay.</summary>
    public static Task<TResult> ReadAsync<TResult>(
        this ScheduleDbContext db, Func<Task<TResult>> read, CancellationToken cancellationToken) =>
        db.Database.CurrentTransaction is null
            ? db.Database.CreateExecutionStrategy().ExecuteAsync(read, (_, _) => read(), cancellationToken)
            : read();

    public static Task<List<T>> QueryAsync<T>(
        this ScheduleDbContext db, string sql, object? parameters, CancellationToken cancellationToken) =>
        db.ReadAsync(
            async () => (await db.Connection().QueryAsync<T>(db.Command(sql, parameters, cancellationToken))).AsList(),
            cancellationToken);

    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this ScheduleDbContext db, string sql, object? parameters, CancellationToken cancellationToken) =>
        db.ReadAsync(
            () => db.Connection().QueryFirstOrDefaultAsync<T?>(db.Command(sql, parameters, cancellationToken)),
            cancellationToken);

    public static Task<T> QuerySingleAsync<T>(
        this ScheduleDbContext db, string sql, object? parameters, CancellationToken cancellationToken) =>
        db.ReadAsync(
            () => db.Connection().QuerySingleAsync<T>(db.Command(sql, parameters, cancellationToken)),
            cancellationToken);
}
