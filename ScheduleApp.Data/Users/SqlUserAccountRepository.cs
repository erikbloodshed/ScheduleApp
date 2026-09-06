using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Users;

namespace ScheduleApp.Data.Users;

/// <summary>
/// Reads and writes ScheduleApp's own UserAccounts table -- see IUserAccountRepository
/// for the contract. Registered in App.xaml.cs alongside the other Sql*Repository
/// implementations.
/// </summary>
public class SqlUserAccountRepository(ScheduleDbContext db) : IUserAccountRepository
{
    public Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        db.UserAccounts.AnyAsync(cancellationToken);

    public Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        db.UserAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);

    public Task<List<UserAccount>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.UserAccounts
            .OrderByDescending(u => u.CreatedAtUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<UserAccount> AddAsync(UserAccount account, CancellationToken cancellationToken = default)
    {
        if (await db.UserAccounts.AnyAsync(u => u.Username == account.Username, cancellationToken))
            throw new DuplicateUsernameException(account.Username);

        var entry = new UserAccount
        {
            Username = account.Username,
            PasswordHash = account.PasswordHash,
            PasswordSalt = account.PasswordSalt,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        };
        db.UserAccounts.Add(entry);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueUsernameViolation(ex))
        {
            // The check above is a check-then-act race -- another writer could have
            // taken this Username between the check and this save. The unique index
            // (see ScheduleDbContext) is the actual guarantee; this just turns that
            // into the same friendly exception the pre-check throws, instead of a raw
            // SQL error. The failed insert must also be detached here: SaveChangesAsync
            // failing does NOT revert the entity's tracked state, so without this, the
            // same broken "Added" account would be resent (and fail again) on every
            // later unrelated save for the rest of the app session -- ScheduleDbContext
            // lives for the whole session (see App.xaml.cs), not just this one call.
            db.Entry(entry).State = EntityState.Detached;
            throw new DuplicateUsernameException(entry.Username);
        }

        return entry;
    }

    public async Task UpdatePasswordAsync(int id, string passwordHash, string passwordSalt, CancellationToken cancellationToken = default)
    {
        await db.UserAccounts
            .Where(u => u.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.PasswordHash, passwordHash)
                .SetProperty(u => u.PasswordSalt, passwordSalt), cancellationToken);
    }

    public async Task SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await db.UserAccounts
            .Where(u => u.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.IsActive, isActive), cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await db.UserAccounts
            .Where(u => u.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // Mirrors ScheduleRepository.IsUniquePinViolation -- true only for a SQL Server
    // unique-constraint violation (error 2601/2627) against specifically the Username
    // index, not just any DbUpdateException, so an unrelated failure isn't misreported
    // as a duplicate account name.
    private static bool IsUniqueUsernameViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql &&
        sql.Message.Contains("IX_UserAccounts_Username", StringComparison.OrdinalIgnoreCase);
}
