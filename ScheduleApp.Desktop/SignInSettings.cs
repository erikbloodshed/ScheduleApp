namespace ScheduleApp.Desktop;

/// <summary>
/// Bound from the "SignIn" section of appsettings.json / the shared config file --
/// same "missing section is fine, not fatal" story as AttendanceSettings and
/// PushListenerSettings.
/// </summary>
public class SignInSettings
{
    /// <summary>
    /// Absolute path to a custom logo image shown on the sign-in and first-run setup
    /// panels in place of the built-in /Assets/Tinapayan_Logo.png resource. Null/blank
    /// (the default) means "use the built-in logo". Set via the Settings dialog's
    /// "Sign-in page" section, which copies the chosen image into the same
    /// %ProgramData%\ScheduleApp folder as the shared config file and writes its path
    /// back here -- see SharedConfigWriter.Save.
    /// </summary>
    public string? LogoPath { get; set; }
}
