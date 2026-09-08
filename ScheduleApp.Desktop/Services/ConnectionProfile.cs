using Microsoft.Data.SqlClient;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// One saved, named database connection -- e.g. "Production" pointing at
/// ScheduleAppDb, "Testing" pointing at ScheduleAppDb_Testing -- shown in
/// SettingsDialog's Database tab in place of hand-typing a raw connection string.
/// Always Windows-Authenticated: the whole point of this shape is the common case
/// (pick a server and a database, connect as yourself), not every connection string
/// SQL Server can express -- a SQL-auth login or an unusual server/port syntax still
/// goes through that tab's own Advanced section (the raw connection-string box, or
/// the dedicated-login wizard) instead of a profile.
///
/// Desktop-only -- ScheduleApp.PushListener never reads a list of profiles, only the
/// one resolved ConnectionStrings:ScheduleDb value a profile's own
/// <see cref="ToConnectionString"/> feeds into (see SettingsDialog/SharedConfigWriter).
/// That's also why this lives in ScheduleApp.Desktop.Services rather than
/// ScheduleApp.Core, unlike SharedConfigFile's path-resolution logic, which both
/// processes genuinely need.
/// </summary>
public sealed record ConnectionProfile(string Name, string Server, string Database)
{
    /// <summary>Same shape DatabaseSetupDialog's own Windows-Auth-only Create path
    /// builds (see CreateButton_Click) -- TrustServerCertificate=true so a self-signed
    /// or unconfigured server certificate doesn't block every local/LAN SQL Server
    /// instance out of the box, same reasoning that path already documents.</summary>
    public string ToConnectionString() => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = Database,
        IntegratedSecurity = true,
        TrustServerCertificate = true
    }.ConnectionString;
}
