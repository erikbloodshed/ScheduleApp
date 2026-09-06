using OfficeOpenXml;

namespace ScheduleApp.Excel;

/// <summary>
/// EPPlus 5.x-6.x throws at runtime unless a LicenseContext is set once, process-wide,
/// before the first ExcelPackage is created. This centralizes that so every
/// import/export entry point can just call EnsureConfigured().
///
/// IMPORTANT: EPPlus is NOT MIT-licensed like ClosedXML was. Free use is limited to
/// non-commercial / small-business scenarios under their Polyform Noncommercial-based
/// license; a for-profit deployment of this app likely needs a paid EPPlus license.
/// See https://epplussoftware.com/en/LicenseOverview before shipping this to production.
///
/// Also note: if your installed EPPlus is 7+/8+, this project may have moved to a newer
/// license API (e.g. ExcelPackage.License.SetNonCommercialPersonal("Your Name") or
/// SetNonCommercialOrganization(...) / a commercial key setter) and no longer expose
/// ExcelPackage.LicenseContext at all. If this file fails to compile against your
/// installed version, swap the line below for whatever your EPPlus version's docs show.
/// </summary>
public static class ExcelLicense
{
    private static bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;

        ExcelPackage.License.SetNonCommercialOrganization("Tinapayan Festival Bakeshoppe");

        _configured = true;
    }
}
