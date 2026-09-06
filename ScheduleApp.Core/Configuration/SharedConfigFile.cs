namespace ScheduleApp.Core.Configuration;

/// <summary>
/// Resolves the one machine-wide config file both ScheduleApp.Desktop and
/// ScheduleApp.PushListener load -- if it exists -- after their own local
/// appsettings.json, so a value like ConnectionStrings:ScheduleDb only has to be set in
/// one place per machine instead of two.
///
/// Before this existed, that connection string was copy-pasted into both projects'
/// appsettings.json files, each with a comment warning to keep them in sync by hand.
/// Moving the database and updating only one of the two -- an easy mistake, since
/// nothing failed loudly when they drifted -- left PushListener quietly pointed at the
/// wrong database (or failing to connect) while Desktop kept working fine against the
/// new one, so pushed punches would just stop showing up with no obvious error pointing
/// at why. Both apps now call <see cref="ResolvePath"/> and add the result as an
/// optional, higher-priority JSON source; setting it up once per machine (see
/// ScheduleApp.PushListener's README, "Deployment" step 4) makes that file the single
/// source of truth for values like the connection string, instead of two copies that can
/// silently disagree.
///
/// Deliberately lives here in ScheduleApp.Core -- a dependency-free project both apps
/// already reference -- rather than duplicated in each project, so the two processes
/// can't drift on *this* the same way they drifted on the connection string itself.
///
/// Deliberately %ProgramData%, not %LocalAppData% (which ScheduleApp.Desktop's own
/// ViewStateStore uses for its per-user view-state file): ScheduleApp.Desktop runs as
/// whichever interactive user is logged in, while ScheduleApp.PushListener normally runs
/// as a Windows Service under a completely different account (LocalSystem or a dedicated
/// service account -- see that project's README). %LocalAppData% is per-user, so the two
/// processes would each resolve it to a different, unrelated folder; %ProgramData% is the
/// one location both can agree on regardless of which account either one runs as.
/// </summary>
public static class SharedConfigFile
{
    /// <summary>
    /// Overrides where the shared file is looked for. Only needed for local testing, or a
    /// deployment where %ProgramData% isn't the right place -- <see cref="DefaultPath"/>
    /// is correct for the normal "both processes on the same machine as SQL Server
    /// Express" deployment described in ScheduleApp.PushListener's README.
    /// </summary>
    public const string PathOverrideEnvironmentVariable = "SCHEDULEAPP_SHARED_CONFIG";

    /// <summary>
    /// %ProgramData%\ScheduleApp\shared.appsettings.json (typically
    /// C:\ProgramData\ScheduleApp\shared.appsettings.json on a real machine) -- outside
    /// both apps' own publish/output folders, so redeploying either one (see each
    /// project's "Updating / redeploying" step) never touches or overwrites it.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ScheduleApp",
        "shared.appsettings.json");

    /// <summary>
    /// The path to actually use: <see cref="PathOverrideEnvironmentVariable"/> if it's
    /// set, otherwise <see cref="DefaultPath"/>. Both callers pass this straight to
    /// AddJsonFile(..., optional: true, ...) -- a machine that hasn't created this file
    /// yet keeps running exactly as before, off each app's own local appsettings.json.
    /// </summary>
    public static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathOverrideEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridePath) ? DefaultPath : overridePath;
    }
}
