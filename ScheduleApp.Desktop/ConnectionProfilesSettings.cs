using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop;

/// <summary>
/// Bound from the "ConnectionProfiles" section of appsettings.json / the shared
/// config file -- same "missing section is fine, not fatal" story as
/// AttendanceSettings/SignInSettings/PushListenerSettings: a machine that hasn't
/// saved any profiles yet just gets an empty list, not an error.
///
/// Desktop-only, unlike ConnectionStrings:ScheduleDb -- ScheduleApp.PushListener
/// never binds this section, only the one resolved connection string a chosen
/// profile feeds into (see ConnectionProfile.ToConnectionString and
/// SharedConfigWriter.Save). SettingsDialog reads Profiles to populate its
/// Database tab's profile list; nothing else in the app touches this class.
/// </summary>
public class ConnectionProfilesSettings
{
    public List<ConnectionProfile> Profiles { get; set; } = new();
}
