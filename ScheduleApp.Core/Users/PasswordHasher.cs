using System.Security.Cryptography;
using System.Text;

namespace ScheduleApp.Core.Users;

/// <summary>
/// PBKDF2 (HMAC-SHA256) password hashing for UserAccount.PasswordHash/PasswordSalt --
/// no external dependency (BCrypt.Net, ASP.NET Core Identity's PasswordHasher, etc.)
/// since System.Security.Cryptography.Rfc2898DeriveBytes already ships in the BCL and
/// covers this without adding a package for two static methods.
///
/// Salt and hash are stored as separate base64 columns on UserAccount rather than
/// packed into one string the way ASP.NET Core Identity does -- keeps the schema
/// self-explanatory (same "read the column, know what it is" preference the rest of
/// ScheduleDbContext follows) at the cost of two columns instead of one. There's no
/// compatibility reason to prefer either shape here since nothing outside this app
/// ever reads these rows.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    // 210,000 is OWASP's current minimum recommendation for PBKDF2-HMAC-SHA256 -- high
    // enough to make offline brute-forcing of a stolen hash meaningfully slower, still
    // fast enough (well under a second) that a login on ordinary desktop hardware
    // doesn't feel slow. LoginWindow calls this synchronously on its UI thread (see its
    // own doc comment for why) -- if this constant is ever raised significantly, that
    // call may be worth moving off the UI thread.
    private const int Iterations = 210_000;

    /// <summary>Hashes <paramref name="password"/> against a freshly generated random
    /// salt. Call once when creating an account or resetting a password -- never call
    /// this to *check* a password (that's Verify below); a fresh salt here would never
    /// match what's already stored even for the correct password.</summary>
    public static (string Hash, string Salt) Hash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes, saltBytes, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    /// <summary>True if <paramref name="password"/> hashes (with the stored salt) to
    /// the stored hash. Uses a fixed-time comparison so a failed check can't leak how
    /// many leading bytes matched via response-time differences.</summary>
    public static bool Verify(string password, string storedHash, string storedSalt)
    {
        var saltBytes = Convert.FromBase64String(storedSalt);
        var expectedHashBytes = Convert.FromBase64String(storedHash);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var actualHashBytes = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes, saltBytes, Iterations, HashAlgorithmName.SHA256, expectedHashBytes.Length);

        return CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes);
    }
}
