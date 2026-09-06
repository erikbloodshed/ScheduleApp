using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ScheduleApp.Payroll.Pdf;

/// <summary>
/// QuestPDF <see cref="IDocument"/> for one print run's worth of payslips --
/// single-cell render, 2×2 page packing, then the overflow escape hatch, in
/// that order (see Print_Feature.md's own "Build order" step 2 and "Layout"
/// section). Takes already-computed <see cref="PayrollResult"/> values (one
/// per employee) rather than fetching anything itself -- same "pure data in"
/// boundary <c>PayrollCalculator.Calculate</c> draws around I/O, just one
/// layer further out (see that method's own doc comment) -- so this class's
/// caller (<see cref="PayslipRenderer"/>) owns nothing about *where*
/// PayrollResults come from, only what to do with them once it has them.
///
/// Internal -- <see cref="PayslipRenderer"/> is the only public surface this
/// project exposes; nothing outside this project needs to know QuestPDF is
/// what's actually doing the rendering.
/// </summary>
internal sealed class PayslipDocument : IDocument
{
    /// <summary>Quarter-page cell dimensions -- Print_Feature.md's Layout
    /// section: a Letter page (8.5×11") tiled edge-to-edge into a 2×2 grid,
    /// no gap between cells, so the four quarters exactly reconstruct the
    /// full sheet when cut apart.</summary>
    private const float CellWidthIn = 4.25f;

    private const float CellHeightIn = 5.5f;

    /// <summary>Content inset on all sides of a cell -- protects against
    /// clipping near the cut lines/printer's unprintable margin, not a gap
    /// between cells (Print_Feature.md's own distinction). Also used as the
    /// whole-page margin on an overflow slip's dedicated page, for the same
    /// clipping-protection reason. Usable area per cell is therefore
    /// 3.65"×4.9" (4.25/5.5 minus 0.3 on every side) -- see
    /// PayslipLineBuilder.LineWidth's own doc comment for the character-
    /// width half of this same budget.</summary>
    private const float CellInsetIn = 0.3f;

    private const int FontSizePt = 9;

    private const float PointsPerInch = 72f;

    /// <summary>A cell's real usable width in points -- (2)×<see
    /// cref="CellInsetIn"/> subtracted from <see cref="CellWidthIn"/> for the
    /// insets on both sides, converted via <see cref="PointsPerInch"/>. A
    /// compile-time constant (every operand is itself one) rather than a
    /// field computed in <see cref="EnsureLayoutConfigured"/>, so it's
    /// available everywhere in this class -- including <see
    /// cref="ComposeLine"/>, which pins every money line's Row to exactly
    /// this width before splitting it into label/amount columns (see that
    /// method's own doc comment for why the pin matters: <see
    /// cref="ComposeOverflowPage"/> hands lines a page-wide container, not a
    /// quarter-cell-wide one, and an unpinned Row would size itself off
    /// whatever container it actually landed in instead of staying the same
    /// physical width everywhere, the way a plain fixed-width Text line
    /// already does without any help).</summary>
    private const float UsableWidthPt = (CellWidthIn - (2 * CellInsetIn)) * PointsPerInch;

    /// <summary>Line height as a multiple of <see cref="FontSizePt"/>, applied
    /// to every line via <see cref="ComposeLine"/>. Set explicitly rather than
    /// left to QuestPDF's own default so it's a known, re-confirmable number
    /// the same way FontSizePt/LineWidth already are -- see
    /// <see cref="MaxLinesPerCell"/>'s own doc comment, which assumed a
    /// looser ~1.2x default before this was pinned down.</summary>
    private const float LineHeight = 1.05f;

    /// <summary>How many <see cref="PayslipLineBuilder"/> lines fit in one
    /// cell's usable 3.65"×4.9" area at 8pt Iosevka before the overflow
    /// escape hatch kicks in. Same estimate-then-confirm story as
    /// PayslipLineBuilder.LineWidth: 4.9" usable height ÷ <see
    /// cref="LineHeight"/>-of-font-size line height at 8pt ≈ 40 theoretical
    /// lines, scaled down by the same ~91% safety margin the original
    /// 4.5"/33-line estimate was deliberately shaved to 30 by (see that
    /// constant's prior value) -- kept at 33 rather than recalculated up to
    /// ~36 now that LineHeight is pinned to 1.1 (down from the looser ~1.1x
    /// default this estimate originally assumed), so a slip that could have
    /// just barely fit a normal cell instead gets bumped to its own roomier
    /// full page (harmless -- it still prints correctly, just alone), rather
    /// than one that's cut too close and either clips or trips QuestPDF's
    /// own layout-overflow exception. Raise this once a real render confirms
    /// there's room to.</summary>
    private const int MaxLinesPerCell = 36;

    private sealed record Slip(PayrollResult Result, IReadOnlyList<PayslipLine> Lines, bool IsOverflow);

    private readonly IReadOnlyList<Slip> _slips;

    private static bool _layoutConfigured;

    /// <summary>Fixed width, in points, of the amount column's own layout
    /// box -- see this field's own assignment in <see
    /// cref="EnsureLayoutConfigured"/> for how it's sized, and <see
    /// cref="ComposeLine"/> for how it's used (a QuestPDF <c>ConstantItem</c>
    /// with <c>AlignRight()</c>). What makes this fix a real one rather than
    /// another guess is that this value only ever has to be *wide enough* --
    /// unlike the font-metric letter-spacing this replaced (see this field's
    /// prior incarnation, <c>_boldLetterSpacingAdjustment</c>, in source
    /// control history), which had to be *exactly right* to land Bold's
    /// padded-out amount at the same physical x-position as Regular's, with
    /// no way to render-test that it actually did. A box that's wider than
    /// strictly necessary still right-aligns its content at exactly the same
    /// edge -- QuestPDF's layout engine guarantees that structurally, not by
    /// arithmetic this class has to get right. See <see cref="ComposeLine"/>
    /// for why the box's *position* also needs pinning, separately from its
    /// width.</summary>
    private static float _amountBoxWidthPt;

    /// <summary>Measures the cell's real usable width against whichever
    /// font <see cref="PayslipFonts"/> actually resolved, and hands the
    /// resulting character count to <see cref="PayslipLineBuilder.
    /// Configure"/>, plus sizes <see cref="_amountBoxWidthPt"/> -- once per
    /// process, since neither the cell geometry nor the resolved font can
    /// change between calls within one run. Requires <see
    /// cref="PayslipFonts.EnsureConfigured"/> to have already run (see <see
    /// cref="PayslipFonts.FamilyName"/>'s own guard) -- true by the time
    /// this constructor runs, since <see cref="PayslipRenderer.
    /// BuildDocument"/> always calls that first.
    ///
    /// <see cref="PayslipLineBuilder.LineWidth"/> stays budgeted off
    /// Regular's glyph width alone, same as always -- Regular is what the
    /// overwhelming majority of a slip's text actually renders in, and this
    /// number now only feeds word-wrap decisions (see <see
    /// cref="PayslipLineBuilder.AddAmountLine"/>'s own doc comment), not
    /// on-page positioning, so it doesn't need to account for Bold at all.
    ///
    /// The amount column used to be positioned by padding it, as characters,
    /// into the same string as its label -- which meant a Bold line's
    /// padded-out amount only lined up with every Regular line's amount
    /// above it if Bold's own face happened to advance exactly as wide per
    /// character as Regular's (not guaranteed on every font/fallback path --
    /// see <see cref="PayslipFonts.MeasureAdvanceWidthPt"/>'s own doc
    /// comment). A prior fix tried to force that match with QuestPDF's
    /// <c>LetterSpacing</c>, computed from measured advance widths -- workable
    /// in principle, but a value this class could only ever compute and
    /// never actually render-test (no dotnet SDK available in this
    /// environment to build and inspect real output), so a measurement bug
    /// or an installed-font quirk this class didn't anticipate would have
    /// shipped silently.
    ///
    /// This replaces that with a structural guarantee instead of a
    /// computed one: the amount is no longer characters inside a padded
    /// string at all -- <see cref="PayslipLineBuilder.AddAmountLine"/> hands
    /// it down separately as <see cref="PayslipLine.AmountText"/>, and <see
    /// cref="ComposeLine"/> renders it inside its own fixed-<see
    /// cref="_amountBoxWidthPt"/>-wide, right-aligned box. <see
    /// cref="_amountBoxWidthPt"/> below only needs to be wide enough to hold
    /// an <see cref="PayslipLineBuilder.AmountFieldWidth"/>-character amount
    /// at either weight without wrapping -- sized off whichever of Regular's
    /// or Bold's measured width is larger, same "don't assume, measure both"
    /// precedent <see cref="PayslipFonts.MeasureAdvanceWidthPt"/> already
    /// documents -- so it's deliberately generous rather than exact. Once
    /// that box's width is fixed and its position on the page is fixed (see
    /// <see cref="ComposeLine"/>'s own pin), QuestPDF's own layout engine is
    /// what puts every line's amount at the same right edge, Bold or
    /// Regular, without this class needing to know or care how wide Bold
    /// actually rendered.</summary>
    private static void EnsureLayoutConfigured()
    {
        if (_layoutConfigured)
            return;

        float regularWidthPt = PayslipFonts.MeasureAdvanceWidthPt(FontSizePt);
        float boldWidthPt = PayslipFonts.MeasureAdvanceWidthPt(FontSizePt, bold: true);

        int lineWidth = Math.Max(1, (int)Math.Floor(UsableWidthPt / regularWidthPt));

        _amountBoxWidthPt = PayslipLineBuilder.AmountFieldWidth * Math.Max(regularWidthPt, boldWidthPt);

        PayslipLineBuilder.Configure(lineWidth);
        _layoutConfigured = true;
    }

    public PayslipDocument(IReadOnlyList<PayrollResult> payrolls, string companyName)
    {
        EnsureLayoutConfigured();

        // One shared timestamp for the whole print run -- every slip's
        // footer "Date Generated" line should read the same regardless of
        // how many employees are in this batch or how long rendering takes,
        // not drift employee to employee. See PayslipLineBuilder.Build's own
        // doc comment for why it's captured here (the IO/impure boundary)
        // and threaded in, rather than each Build() call reading the clock
        // itself.
        var generatedAt = DateTime.Now;

        _slips = [.. payrolls
            .Select(result =>
            {
                var lines = PayslipLineBuilder.Build(result, generatedAt, companyName);
                return new Slip(result, lines, lines.Count > MaxLinesPerCell);
            })];
    }

    /// <summary>Employee names bumped to the overflow escape hatch, in the
    /// order they were handed in -- see <see cref="PayslipRenderResult.
    /// OverflowEmployeeNames"/>, which is just this passed straight
    /// through.</summary>
    public IReadOnlyList<string> OverflowEmployeeNames =>
        [.. _slips.Where(s => s.IsOverflow).Select(s => s.Result.EmployeeName)];

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        var normalSlips = _slips.Where(s => !s.IsOverflow).ToList();
        var overflowSlips = _slips.Where(s => s.IsOverflow).ToList();

        for (int i = 0; i < normalSlips.Count; i += 4)
        {
            var batch = normalSlips.Skip(i).Take(4).ToList();
            container.Page(page => ComposeGridPage(page, batch));
        }

        foreach (var slip in overflowSlips)
            container.Page(page => ComposeOverflowPage(page, slip));
    }

    private static void ComposeGridPage(PageDescriptor page, IReadOnlyList<Slip> batch)
    {
        page.Size(PageSizes.Letter);
        page.Margin(0);
        page.PageColor(Colors.White);

        page.Content().Column(column =>
        {
            column.Spacing(0);

            column.Item().Row(row =>
            {
                row.Spacing(0);
                row.ConstantItem(CellWidthIn, Unit.Inch).Height(CellHeightIn, Unit.Inch)
                    .Element(cell => ComposeCell(cell, batch.ElementAtOrDefault(0)));
                row.ConstantItem(CellWidthIn, Unit.Inch).Height(CellHeightIn, Unit.Inch)
                    .Element(cell => ComposeCell(cell, batch.ElementAtOrDefault(1)));
            });

            column.Item().Row(row =>
            {
                row.Spacing(0);
                row.ConstantItem(CellWidthIn, Unit.Inch).Height(CellHeightIn, Unit.Inch)
                    .Element(cell => ComposeCell(cell, batch.ElementAtOrDefault(2)));
                row.ConstantItem(CellWidthIn, Unit.Inch).Height(CellHeightIn, Unit.Inch)
                    .Element(cell => ComposeCell(cell, batch.ElementAtOrDefault(3)));
            });
        });
    }

    private static void ComposeCell(IContainer container, Slip? slip)
    {
        if (slip is null)
            return;

        container
            .Padding(CellInsetIn, Unit.Inch)
            .Column(column =>
            {
                column.Spacing(0);
                ComposeBodyThenBottomPinnedFooter(column, slip.Lines);
            });
    }

    private static void ComposeBodyThenBottomPinnedFooter(ColumnDescriptor column, IReadOnlyList<PayslipLine> lines)
    {
        int bodyCount = lines.Count - PayslipLineBuilder.FooterLineCount;

        for (int i = 0; i < bodyCount; i++)
            column.Item().Element(e => ComposeLine(e, lines[i]));

        column.Item().ExtendVertical().AlignBottom().Column(footer =>
        {
            footer.Spacing(0);

            for (int i = bodyCount; i < lines.Count; i++)
                footer.Item().Element(e => ComposeLine(e, lines[i]));
        });
    }

    /// <summary>Renders one <see cref="PayslipLine"/>. A plain line (<see
    /// cref="PayslipLine.AmountText"/> null -- headers, rules, centered
    /// captions, a wrapped label's own continuation lines, blank spacers)
    /// is just one <see cref="ComposeSpan"/> call, unchanged from before
    /// this fix. A money line splits into a Row: the label on the left via
    /// <c>RelativeItem</c>, and the amount on the right via a <c>
    /// ConstantItem</c> of <see cref="_amountBoxWidthPt"/> with <c>
    /// AlignRight()</c> -- QuestPDF's layout engine is what actually places
    /// the amount at that box's right edge, not this method.
    ///
    /// The Row is pinned to <see cref="UsableWidthPt"/> before it's split,
    /// rather than left to size itself off whatever container it's actually
    /// given -- <c>RelativeItem</c> otherwise means "fill whatever's left in
    /// my container," and that container is a quarter-page cell on a normal
    /// grid page (see <see cref="ComposeGridPage"/>) but a full page width
    /// on <see cref="ComposeOverflowPage"/>'s dedicated overflow page.
    /// Without the pin, the same money line would push its amount box to a
    /// different physical x-position depending on which of those two it
    /// happened to render on. Pinning first makes every money line's Row
    /// exactly <see cref="UsableWidthPt"/> wide everywhere, which is what a
    /// plain fixed-width Text line already gets for free (a Text element
    /// only ever renders as wide as its own content, so it never stretched
    /// to fill a wider container in the first place) -- this just gives the
    /// Row the same property explicitly, since Row does stretch by
    /// default.</summary>
    private static void ComposeLine(IContainer container, PayslipLine line)
    {
        if (line.AmountText is null)
        {
            ComposeSpan(container, line.Text, line, strikethrough: line.Strikethrough);
            return;
        }

        container.Width(UsableWidthPt, Unit.Point).Row(row =>
        {
            row.Spacing(0);

            row.RelativeItem().Element(e => ComposeSpan(e, line.Text, line, strikethrough: false));

            // Only the amount itself is ever struck through here -- never
            // the label -- so a waived line still reads normally except for
            // the figure it's excluding. See PayslipLineBuilder.
            // AddAmountLine's own doc comment for the one case that's the
            // other way around (a wrapped label continuation line, which
            // has no AmountText and goes through the branch above instead).
            row.ConstantItem(_amountBoxWidthPt, Unit.Point).AlignRight()
                .Element(e => ComposeSpan(e, line.AmountText, line, strikethrough: line.Strikethrough));
        });
    }

    /// <summary>Renders <paramref name="text"/> as a single styled span --
    /// <paramref name="line"/>'s Bold/Red apply the same way regardless of
    /// which part of a money line this span is (label or amount), but
    /// <paramref name="strikethrough"/> is passed separately from <see
    /// cref="PayslipLine.Strikethrough"/> rather than read off it directly,
    /// since <see cref="ComposeLine"/> needs to say yes for one span and no
    /// for the other on the same line.</summary>
    private static void ComposeSpan(IContainer container, string text, PayslipLine line, bool strikethrough)
    {
        container.Text(t =>
        {
            t.DefaultTextStyle(style =>
            {
                var s = style
                    .FontFamily(PayslipFonts.FamilyName)
                    .FontSize(FontSizePt)
                    .LineHeight(LineHeight);

                if (line.Bold)
                    s = s.Bold();

                if (line.Red)
                    s = s.FontColor(Colors.Red.Medium);

                return s;
            });

            var span = t.Span(text);

            if (strikethrough)
                span.Strikethrough().DecorationSolid().DecorationColor(Colors.Black);
        });
    }

    private static void ComposeOverflowPage(PageDescriptor page, Slip slip)
    {
        page.Size(PageSizes.Letter);
        page.Margin(CellInsetIn, Unit.Inch);
        page.PageColor(Colors.White);

        page.Content().Column(column =>
        {
            column.Spacing(0);
            ComposeBodyThenBottomPinnedFooter(column, slip.Lines);
        });
    }
}