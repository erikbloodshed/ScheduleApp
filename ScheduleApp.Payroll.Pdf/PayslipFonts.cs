// ScheduleApp.Payroll.Pdf/PayslipFonts.cs
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace ScheduleApp.Payroll.Pdf;

/// <summary>
/// One-time QuestPDF setup: the Community license acknowledgment QuestPDF
/// requires before generating anything (see this project's own .csproj
/// comment on the QuestPDF PackageReference for the license terms), and
/// deciding which monospace family every payslip's <c>FontFamily(PayslipFonts.
/// FamilyName)</c> call actually renders with. No font file is bundled with
/// this project any more -- QuestPDF resolves <see cref="FamilyName"/> against
/// whatever's installed on the machine this runs on, the same way it would
/// resolve any other unregistered <c>FontFamily(...)</c> name. Mirrors
/// ScheduleApp.Excel.ExcelLicense's own "must configure exactly once, before
/// first use, guarded by a bool" shape for a different third-party library.
/// </summary>
internal static class PayslipFonts
{
    /// <summary>Preferred family: used when Iosevka is actually installed on
    /// this machine. Not bundled -- if it's missing, <see cref="FallbackFamilyName"/>
    /// is used instead rather than failing the render.</summary>
    private const string PreferredFamilyName = "Iosevka";

    /// <summary>Fallback family when <see cref="PreferredFamilyName"/> isn't
    /// installed -- Lucida Console, a monospace font that ships with Windows
    /// itself, so it's there even on a machine nobody's ever installed
    /// Iosevka on.</summary>
    private const string FallbackFamilyName = "Lucida Console";

    private static bool _configured;
    private static string? _familyName;

    /// <summary>The family name every payslip's <c>FontFamily(...)</c> call
    /// uses -- <see cref="PreferredFamilyName"/> if it's installed, otherwise
    /// <see cref="FallbackFamilyName"/>. Resolved once, inside <see
    /// cref="EnsureConfigured"/>; reading this before that's run is a bug in
    /// the caller, not something to silently paper over with a default.</summary>
    public static string FamilyName =>
        _familyName ?? throw new InvalidOperationException(
            $"{nameof(PayslipFonts)}.{nameof(EnsureConfigured)} must run before {nameof(FamilyName)} is read.");

    /// <summary>Safe to call every time a document is about to be built --
    /// same "guarded by a bool, real work happens once" shape as
    /// ExcelLicense.EnsureConfigured.</summary>
    public static void EnsureConfigured()
    {
        if (_configured)
            return;

        QuestPDF.Settings.License = LicenseType.Community;

        // SKFontManager.Default is the same system font catalog QuestPDF's
        // own rendering falls back on for any FontFamily(...) name that
        // isn't explicitly registered, so checking it here for Iosevka's
        // presence -- rather than just trying it and catching a failure --
        // tells us up front, before layout even starts, which family this
        // run is actually going to use.
        _familyName = SKFontManager.Default.MatchFamily(PreferredFamilyName) is not null
            ? PreferredFamilyName
            : FallbackFamilyName;

        _configured = true;
    }

    /// <summary>Advance width, in points, of one character in <see
    /// cref="FamilyName"/> at <paramref name="fontSizePt"/> -- both <see
    /// cref="PreferredFamilyName"/> and <see cref="FallbackFamilyName"/> are
    /// monospace, so every glyph advances by the same amount and measuring
    /// one representative character (the digit "0") is exact for any
    /// character <c>PayslipLineBuilder</c> actually emits, not just an
    /// estimate. Lets a caller size a character-based layout (<see
    /// cref="PayslipLineBuilder.LineWidth"/>) to whichever family <see
    /// cref="EnsureConfigured"/> actually resolved, instead of a number
    /// hand-calibrated against one specific font that goes wrong the moment
    /// the fallback kicks in on a machine without <see
    /// cref="PreferredFamilyName"/> installed. Uses SkiaSharp directly --
    /// QuestPDF has no public "how wide will this text render" API of its
    /// own -- but it's the same SkiaSharp text engine QuestPDF's own layout
    /// uses internally, so the measurement stays consistent with what
    /// actually ends up on the page. Must run after <see
    /// cref="EnsureConfigured"/> (reads <see cref="FamilyName"/>).</summary>
    /// <param name="bold">Measures the Bold weight's advance width instead of
    /// Regular's. A "monospace" family isn't guaranteed to advance identically
    /// across weights on every machine/fallback path -- see <see
    /// cref="PayslipDocument.EnsureLayoutConfigured"/>'s own doc comment for
    /// why its caller sizes the amount column's layout box off whichever
    /// weight measures wider, rather than assuming the two match.</param>
    public static float MeasureAdvanceWidthPt(float fontSizePt, bool bold = false)
    {
        using var typeface = SKTypeface.FromFamilyName(
            FamilyName, bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var font = new SKFont(typeface, fontSizePt);
        return font.MeasureText("0");
    }
}