using System.IO;
using System.Text.Json;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Remembers, across restarts, whether the person pinned the main window's navigation
/// drawer open (see MainWindow's pin button, in the pane header next to the toggle).
/// Same %LocalAppData%\ScheduleApp folder and best-effort JSON round-trip as
/// <see cref="RememberedSignInStore"/> -- per-Windows-user, always writable -- but its
/// own tiny file rather than another PersistedViewState property: unlike the Schedule/
/// Attendance view state (deliberately session-only, see ViewStateStore's own doc
/// comment), this one preference IS meant to survive a close-and-reopen, so pinning the
/// drawer once actually sticks.
///
/// Best-effort throughout: a missing/corrupt/unreadable file just means "not pinned"
/// (the app's normal compact-on-launch default -- see MainWindow.OnAuthSucceeded), and a
/// failed save just means the next launch forgets the pin. Neither is worth interrupting
/// startup or a single click over.
/// </summary>
public sealed class NavigationDrawerStateStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScheduleApp",
        "navigation-drawer.json");

    /// <summary>True when the drawer was left pinned open on the last run. False on the
    /// first ever run, after an explicit unpin, or whenever the file can't be read.</summary>
    public bool LoadPinned()
    {
        try
        {
            if (!File.Exists(FilePath))
                return false;

            var record = JsonSerializer.Deserialize<StoredRecord>(File.ReadAllText(FilePath));
            return record?.Pinned ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Persists the new pinned state -- called from MainWindow the moment the
    /// pin button is toggled, so the choice is already on disk before the app next
    /// closes.</summary>
    public void SavePinned(bool pinned)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new StoredRecord { Pinned = pinned }));
        }
        catch
        {
            // Best-effort -- see class doc comment.
        }
    }

    private sealed class StoredRecord
    {
        public bool Pinned { get; set; }
    }
}
