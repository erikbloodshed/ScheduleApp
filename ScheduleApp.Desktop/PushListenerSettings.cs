namespace ScheduleApp.Desktop;

/// <summary>
/// Bound from the "PushListener" section of appsettings.json. Just a starting
/// default for the Push Listener tab's server URL box -- a missing section is
/// fine, not fatal, same as AttendanceSettings.
/// </summary>
public class PushListenerSettings
{
    /// <summary>
    /// Base URL of a running ScheduleApp.PushListener instance (its
    /// Push:ListenUrl from that project's own appsettings.json), e.g.
    /// "http://localhost:8080" if it's on this same machine, or a LAN
    /// hostname/IP if it's on the SQL Server Express box and this is a
    /// different machine. Editable in the tab itself -- this is only ever
    /// the starting value, not a hard requirement.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";
}
