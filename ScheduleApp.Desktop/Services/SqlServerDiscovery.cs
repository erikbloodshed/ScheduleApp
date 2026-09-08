using Microsoft.Win32;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Best-effort discovery of SQL Server instances installed on *this* machine, for the
/// Server field in DatabaseSetupDialog and SettingsDialog's Add-profile form -- both
/// now editable ComboBoxes (IsEditable="True") populated from this on open, rather
/// than plain TextBoxes someone has to already know the instance name to fill in.
/// "Editable" is still load-bearing, not just a UI nicety: this only ever finds local
/// instances (see DiscoverServersAsync's own doc comment for why network discovery
/// was deliberately left out), so a remote server's name always has to be typed by
/// hand regardless of what shows in the dropdown.
///
/// Stateless -- no background service, no caching across calls; each dialog just
/// calls DiscoverServers once, right after opening.
///
/// Deliberately synchronous, not async: a registry read never blocks meaningfully,
/// and this used to also broadcast on the network via
/// SqlDataSourceEnumerator.GetDataSources() (a synchronous, pre-async-era API with no
/// CancellationToken of its own) before that was removed -- on a network that
/// silently drops the UDP broadcast instead of refusing it (no ICMP unreachable,
/// nothing to make the socket give up on its own), that call was observed to hang for
/// minutes rather than the few seconds it takes on a healthy network. A real failure
/// mode, not a hypothetical, and not worth the complexity (Task.Run, a timeout, an
/// abandoned background thread) of guarding against for a convenience feature. Local
/// instances -- SQL Server Express installed on the same machine -- are also the more
/// directly useful result anyway, which is the common case this field exists to save
/// someone from typing by hand; a remote server's name still always has to be typed,
/// same as before this class existed.
/// </summary>
public static class SqlServerDiscovery
{
    /// <summary>Reads HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL
    /// (see DiscoverLocalInstances below) and returns the result as ready-to-use
    /// server address strings (e.g. ".\SQLEXPRESS"), sorted. Never throws -- a
    /// missing key or restricted registry access just means an empty list, not a
    /// failure the caller needs to guard against.</summary>
    public static IReadOnlyList<string> DiscoverServers() => DiscoverLocalInstances();

    /// <summary>Reads HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL,
    /// whose value names are every SQL Server instance actually installed on this
    /// machine (Express, Developer, or full editions alike) -- present regardless of
    /// whether the instance's own service is currently running. MSSQLSERVER is the
    /// special "default instance" name and is addressed as just "." (or the machine
    /// name), never ".\MSSQLSERVER" -- every other instance name becomes
    /// ".\InstanceName".</summary>
    private static List<string> DiscoverLocalInstances()
    {
        var results = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            if (key is null) return results;

            foreach (var instanceName in key.GetValueNames())
            {
                results.Add(string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                    ? "."
                    : $@".\{instanceName}");
            }
        }
        catch
        {
            // Best-effort only -- a machine without this key (SQL Server tooling
            // installed but no local instance), or one where registry access is
            // restricted, just contributes nothing here rather than failing to open
            // the dialog over it.
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }
}
