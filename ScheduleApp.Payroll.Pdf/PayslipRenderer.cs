using QuestPDF.Fluent;

namespace ScheduleApp.Payroll.Pdf;

/// <summary>
/// One PDF (or preview image set)'s worth of output from <see
/// cref="PayslipRenderer"/> -- the PDF bytes themselves plus which
/// employees, if any, ended up on the overflow escape hatch's own dedicated
/// page instead of sharing a quarter-page cell with three others (see
/// Print_Feature.md's Layout section: "flagged in the export result").
/// </summary>
public sealed class PayslipRenderResult
{
    public required byte[] PdfBytes { get; init; }

    /// <summary>Employee names whose payslip didn't fit the per-cell line
    /// budget -- see <c>PayslipDocument.MaxLinesPerCell</c>'s own doc
    /// comment for that budget's source. Empty when every slip fit
    /// normally. Surfaced back to the caller so a person printing a large
    /// run can see at a glance which employees, if any, ended up alone on
    /// their own page.</summary>
    public required IReadOnlyList<string> OverflowEmployeeNames { get; init; }
}

/// <summary>
/// One preview pass's worth of output from <see
/// cref="PayslipRenderer.RenderPreview"/> -- rasterized page images for the
/// Prev/Next viewer, plus the overflow employee list, computed together from
/// one document composition. See <see cref="PayslipRenderResult"/> for the
/// equivalent shape around actual PDF bytes.
/// </summary>
public sealed class PayslipPreviewResult
{
    /// <summary>One PNG per physical page, in print order.</summary>
    public required IReadOnlyList<byte[]> PageImages { get; init; }

    public required IReadOnlyList<string> OverflowEmployeeNames { get; init; }
}

/// <summary>
/// Public entry point into ScheduleApp.Payroll.Pdf -- the only class the
/// rest of the app needs to know about. Wraps <see cref="PayslipDocument"/>
/// (single-cell render, 2×2 page packing, overflow escape hatch --
/// Print_Feature.md's build-order step 2) behind two plain methods: one for
/// the actual PDF bytes (Save/export, and the "export + open in default PDF
/// viewer" flow), one for rasterized page images (the in-app Prev/Next
/// preview -- build-order step 3, QuestPDF's own <c>GenerateImages</c>).
///
/// <see cref="PayslipFonts.EnsureConfigured"/> is called from inside both
/// methods here, not left for the caller to remember -- same "safe to call
/// every time, real work happens once" shape as
/// ScheduleApp.Excel.AttendanceExcelExporter calling
/// ExcelLicense.EnsureConfigured itself rather than pushing that onto every
/// caller.
/// </summary>
public static class PayslipRenderer
{
    /// <summary>The actual PDF, as bytes -- what gets written to disk for
    /// Save/export or handed to a PrintDocument/default-viewer flow.
    /// <paramref name="companyName"/> is the header every slip in this run
    /// prints -- already resolved to whatever's effective (Settings' value,
    /// or PayslipLineBuilder.DefaultCompanyName if that's blank/unset); this
    /// project doesn't read configuration itself, so the caller settles
    /// that before calling in, same boundary as every other "pure data in"
    /// class here (see PayslipDocument's own doc comment).</summary>
    public static PayslipRenderResult RenderToPdf(IReadOnlyList<PayrollResult> payrolls, string companyName)
    {
        var document = BuildDocument(payrolls, companyName);

        return new PayslipRenderResult
        {
            PdfBytes = document.GeneratePdf(),
            OverflowEmployeeNames = document.OverflowEmployeeNames,
        };
    }

    /// <summary>One PNG per physical page, in print order, plus the same
    /// overflow-employee list <see cref="RenderToPdf"/> would report -- what
    /// the preview dialog's Prev/Next viewer pages through (see
    /// Print_Feature.md's own Preview section: "QuestPDF rasterizes its own
    /// generated pages (GenerateImages) into a simple Prev/Next image
    /// viewer"). Built from the exact same <see cref="PayslipDocument"/>
    /// shape <see cref="RenderToPdf"/> uses, so what's previewed is
    /// genuinely what would get printed/exported -- not a separate,
    /// possibly-drifted approximation of it -- at the cost of composing the
    /// document a second time if/when the person actually commits to
    /// Save/Print, rather than caching a single QuestPDF <c>Document</c>
    /// across both; composition itself is cheap relative to rasterization or
    /// PDF encoding, so that's not a real concern in practice. Both pieces
    /// (images and overflow names) come from one composition here, not two
    /// separate calls, since the preview dialog always wants both together.
    /// </summary>
    public static PayslipPreviewResult RenderPreview(IReadOnlyList<PayrollResult> payrolls, string companyName)
    {
        var document = BuildDocument(payrolls, companyName);

        return new PayslipPreviewResult
        {
            PageImages = document.GenerateImages().ToList(),
            OverflowEmployeeNames = document.OverflowEmployeeNames,
        };
    }

    private static PayslipDocument BuildDocument(IReadOnlyList<PayrollResult> payrolls, string companyName)
    {
        ArgumentNullException.ThrowIfNull(payrolls);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);

        if (payrolls.Count == 0)
            throw new ArgumentException("At least one employee's PayrollResult is required.", nameof(payrolls));

        PayslipFonts.EnsureConfigured();

        return new PayslipDocument(payrolls, companyName);
    }
}
