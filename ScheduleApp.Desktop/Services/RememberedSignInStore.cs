using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Optionally persists the username and password from the sign-in page's "Remember me"
/// checkbox so SignInPanel can pre-fill the form on the next launch. Lives in the same
/// %LocalAppData%\ScheduleApp folder as ViewStateStore's viewstate.json (per-Windows-user,
/// always writable) but is its own file/class rather than another PersistedViewState
/// property: unlike view state, this holds a credential, is read once at startup rather
/// than kept as a long-lived in-memory copy, and needs to disappear the moment the box is
/// unchecked rather than just going stale.
///
/// The password is never written to disk in the clear. It's encrypted with the Windows
/// Data Protection API (see ProtectedData.Protect) using DataProtectionScope.CurrentUser,
/// which ties the encryption to the Windows login that saved it -- the OS derives the key
/// from that account's own credentials, so the resulting bytes can't be decrypted by a
/// different Windows user on the same machine, or after copying remembered-signin.json to
/// a different machine entirely. This is the same mechanism Windows itself uses for saved
/// Wi-Fi passwords and Credential Manager entries, and it needs no key of this app's own to
/// generate or protect.
///
/// This is NOT protection against someone with access to the same signed-in Windows
/// session -- anyone who can run code as that Windows user can call
/// ProtectedData.Unprotect the same way this class does. "Remember me" is a convenience
/// for a single trusted person on their own machine, the same trust boundary as a
/// browser's saved-password feature, not a substitute for keeping physical/account access
/// to the machine itself secure.
/// </summary>
public class RememberedSignInStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScheduleApp",
        "remembered-signin.json");

    /// <summary>Username and password from the last successful sign-in that had
    /// "Remember me" checked, or null if nothing's remembered (never checked, checked
    /// then later unchecked, or the saved file is missing/corrupt/unreadable -- e.g. it
    /// was copied over from another machine or another Windows account, see class doc
    /// comment). Best-effort like ViewStateStore's Load: any failure here just falls back
    /// to a blank sign-in form instead of crashing startup over a convenience
    /// feature.</summary>
    public (string Username, string Password)? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var json = File.ReadAllText(FilePath);
            var record = JsonSerializer.Deserialize<StoredRecord>(json);
            if (record is null ||
                string.IsNullOrEmpty(record.Username) ||
                string.IsNullOrEmpty(record.ProtectedPassword))
                return null;

            var protectedBytes = Convert.FromBase64String(record.ProtectedPassword);
            var passwordBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return (record.Username, Encoding.UTF8.GetString(passwordBytes));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Called by SignInPanel right after a successful sign-in with "Remember me"
    /// checked -- overwrites whatever was previously remembered (including for a
    /// different account) with this one. Best-effort: a save failure (a locked file, a
    /// full disk) just means the next launch asks for credentials again, not a crash
    /// during what the person otherwise just saw as a successful sign-in.</summary>
    public void Save(string username, string password)
    {
        try
        {
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var protectedBytes = ProtectedData.Protect(passwordBytes, null, DataProtectionScope.CurrentUser);

            var record = new StoredRecord
            {
                Username = username,
                ProtectedPassword = Convert.ToBase64String(protectedBytes)
            };

            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(record));
        }
        catch
        {
            // Best-effort -- see class doc comment.
        }
    }

    /// <summary>Called by SignInPanel right after a successful sign-in with "Remember me"
    /// left unchecked -- deletes any previously remembered credentials rather than
    /// leaving a stale one on disk from an earlier sign-in that did have the box checked.
    /// Only ever called on a *successful* sign-in (never on a failed attempt), so a typo
    /// in an unrelated field can't wipe out a credential that was still valid.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // Best-effort -- see class doc comment.
        }
    }

    private class StoredRecord
    {
        public string? Username { get; set; }
        public string? ProtectedPassword { get; set; }
    }
}
