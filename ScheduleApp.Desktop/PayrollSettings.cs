using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Desktop;

/// <summary>
/// Bound from the "Payroll" section of appsettings.json -- the Payroll
/// equivalent of AttendanceSettings' role for AttendancePolicy. A missing or
/// incomplete section is fine, not fatal, same as AttendanceSettings: it just
/// means PayrollPolicy starts at its own built-in defaults.
/// </summary>
public class PayrollSettings
{
    public PayrollPolicy Policy { get; set; } = new();

    /// <summary>The header every generated payslip prints -- both the in-app
    /// preview and the actual Save/Print output (see PayslipRenderer, which both
    /// paths go through). Null/blank (the default, and what a fresh install with
    /// no "Payroll:CompanyName" key has) means "use the built-in
    /// PayslipLineBuilder.DefaultCompanyName" -- same "blank means use the
    /// built-in default" shape SignInSettings.LogoPath already established for
    /// the sign-in logo, rather than duplicating the actual fallback text here
    /// too. Set via the Settings dialog's "Payslip" section, which writes it
    /// back to Payroll:CompanyName in the shared config file -- see
    /// SharedConfigWriter.Save.</summary>
    public string? CompanyName { get; set; }
}