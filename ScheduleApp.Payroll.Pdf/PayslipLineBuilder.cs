using System.Globalization;
using System.Text;
using ScheduleApp.Core.Enums;

namespace ScheduleApp.Payroll.Pdf;

/// <summary>
/// One pre-formatted output line plus the presentation hints
/// <see cref="PayslipDocument"/> needs to render it, paired with which lines
/// get emphasis: the GROSS PAY/DEDUCTIONS/NET PAY titles and the Total Gross
/// Pay/Total Deductions lines are bold, and every line inside the Deductions
/// section (including its own Total) prints in red. Carrying that decision
/// here means PayslipDocument doesn't need to pattern-match line text back
/// into styling choices this class already made once while building it.
///
/// <see cref="AmountText"/> is what turns this into a two-column money
/// line: when it's non-null, <see cref="Text"/> holds only the indented
/// label -- unpadded, since nothing about its own length needs to reach a
/// target column anymore -- and <see cref="PayslipDocument.ComposeLine"/>
/// renders the two in separate boxes (label, then a fixed-width
/// right-aligned box for the amount) so their right edges land in the same
/// page column via QuestPDF's own layout engine, not via character-count
/// padding that has to assume every font weight advances identically (see
/// <see cref="AddAmountLine"/>'s own doc comment for the bug this
/// replaced). Every other kind of line -- headers, rules, centered
/// captions, a wrapped label's own continuation lines, blank spacer lines --
/// leaves <see cref="AmountText"/> null and puts its complete content in
/// <see cref="Text"/> exactly as before, rendered as one plain span.
/// </summary>
public readonly record struct PayslipLine(string Text, bool Bold = false, bool Red = false, bool Strikethrough = false, string? AmountText = null);

/// <summary>
/// Turns one employee's computed <see cref="PayrollResult"/> into the ordered,
/// pre-formatted monospace text lines a payslip prints as -- see
/// Print_Feature.md's "Payslip content" section for the exact layout this
/// reproduces (that section's own sample block isn't itself column-aligned --
/// it's hand-typed markdown, not fixed-width text -- so what matters is the
/// content/order/hierarchy it shows, not its literal character spacing; this
/// class is what actually produces consistent fixed-width alignment).
///
/// Pure and synchronous (PayrollResult in, lines out, no QuestPDF/IO/page-
/// layout knowledge at all) so it's checkable by hand-tracing a sample
/// PayrollResult the same way PayrollCalculator.Calculate itself is -- see
/// that class's own doc comment for the same reasoning applied one layer up.
///
/// Deliberately doesn't know how many lines fit on a slip (see
/// <see cref="LineWidth"/>'s own doc comment for the analogous "estimate,
/// confirm once actually rendering" story Print_Feature.md flags). Whether a
/// given Build() result fits an ordinary quarter-page cell or needs the
/// overflow escape hatch's own dedicated full page is PayslipDocument's call
/// (build-order step 2) -- this class just emits however many lines the data
/// actually needs and never truncates or drops anything to make it fit.
/// </summary>
public static class PayslipLineBuilder
{
    /// <summary>Fallback header text for a payslip when nothing more specific was
    /// configured -- see PayrollSettings.CompanyName's own doc comment for where the
    /// actually-effective name comes from (the Settings dialog's "Payslip" section,
    /// same "SignIn:LogoPath, blank means use the built-in default" shape
    /// SignInSettings.LogoPath already established for the sign-in logo). This class
    /// itself stays pure either way -- see Build's own <paramref name="companyName"/>
    /// parameter -- callers resolve the blank-means-default fallback before it ever
    /// reaches here, the same "resolve once, pass the final value in" boundary
    /// generatedAt already draws for the clock.</summary>
    public const string DefaultCompanyName = "TINAPAYAN FESTIVAL BAKESHOPPE";

    private static int? _lineWidth;

    /// <summary>Characters per line -- what a plain single-string line
    /// (headers, rules, centered captions) is padded or centered to fill,
    /// and what a money line's label is word-wrapped against before it would
    /// collide with the amount column (see <see cref="AddAmountLine"/>'s own
    /// doc comment; the amount column itself is no longer part of this
    /// character budget). No longer a hand-calibrated constant: <see
    /// cref="Configure"/> sets this once, from the cell's actual usable
    /// width in points divided by the actually-resolved font's own measured
    /// glyph advance width (see <see
    /// cref="PayslipFonts.MeasureAdvanceWidthPt"/>) -- so it's correct
    /// whether that font turns out to be Iosevka or the Lucida Console
    /// fallback, rather than a number that was only ever confirmed correct
    /// for one specific font at one specific inset. Reading this before
    /// <see cref="Configure"/> has run is a bug in the caller, not something
    /// to silently paper over with a default -- same shape as <see
    /// cref="PayslipFonts.FamilyName"/>.</summary>
    public static int LineWidth =>
        _lineWidth ?? throw new InvalidOperationException(
            $"{nameof(PayslipLineBuilder)}.{nameof(Configure)} must run before {nameof(LineWidth)} is read.");

    /// <summary>Sets <see cref="LineWidth"/> for every subsequent <see
    /// cref="Build"/> call in this process -- called once by <see
    /// cref="PayslipDocument"/>, which owns the cell geometry and font-size
    /// constants this number is derived from (see that class's own
    /// EnsureLayoutConfigured). Cheap to call more than once (no I/O),
    /// so unlike <see cref="PayslipFonts.EnsureConfigured"/> this doesn't
    /// need its own "already configured" guard -- PayslipDocument's caller
    /// already only measures once and reuses the result.</summary>
    public static void Configure(int lineWidth)
    {
        if (lineWidth <= AmountFieldWidth)
            throw new ArgumentOutOfRangeException(nameof(lineWidth), lineWidth,
                $"Must be greater than {nameof(AmountFieldWidth)} ({AmountFieldWidth}) to leave room for a label next to the amount column.");

        _lineWidth = lineWidth;
    }

    /// <summary>Character-count budget for a two-column money line's amount
    /// -- covers up to ±999,999.99 (11 characters including a minus sign,
    /// the one place a payslip amount can go negative; see <see
    /// cref="PayrollResult.NetPay"/>'s own doc comment). No longer the
    /// literal on-page column width (that's now a fixed points-wide layout
    /// box <see cref="PayslipDocument"/> sizes for itself off this same
    /// count -- see its own EnsureLayoutConfigured); this constant's actual
    /// job today is (1) how much of <see cref="AddAmountLine"/>'s available
    /// width it reserves before deciding a label needs to word-wrap, and (2)
    /// the character count that box gets sized to hold. A figure that
    /// somehow exceeds 11 digits isn't truncated -- it simply renders wider
    /// than the box was sized for and may crowd or overflow it, rather than
    /// silently dropping a digit.
    /// Internal rather than private: <see cref="PayslipDocument"/> reads it
    /// to size that box off the exact same count this class budgets label
    /// space against, so the two stay in lockstep by construction rather
    /// than by two call sites happening to agree on 11.</summary>
    internal const int AmountFieldWidth = 11;

    /// <summary>How many trailing lines <see cref="Build"/> always appends as
    /// the signature/footer block: "Received by:", two blank lines, the
    /// signature Rule, the centered "Name & Signature" caption, one blank
    /// line, then the centered "Date Generated" line -- seven in total.
    /// Exists purely so a renderer that wants the footer visually anchored
    /// to the bottom of its own fixed-height page/cell -- rather than
    /// wherever it happens to land right after the content above it --
    /// knows exactly where the split is, without this class needing to know
    /// anything about pages or cells itself (see the class doc comment). A
    /// plain count is all that's needed rather than a richer split/section
    /// marker because every one of these seven lines is a fixed, unwrapped
    /// string (see the footer block at the end of <see cref="Build"/>) --
    /// there's no scenario where the footer is anything other than exactly
    /// these seven lines. Keep this in sync if that block ever changes.
    /// </summary>
    public const int FooterLineCount = 7;

    /// <summary>One employee's full payslip, in print order. See the class
    /// doc comment for what this deliberately doesn't do (no width budget
    /// awareness for pagination purposes -- only for its own line-wrapping).
    /// <paramref name="generatedAt"/> is the wall-clock timestamp the
    /// footer's "Date Generated" line prints -- passed in rather than read
    /// via <c>DateTime.Now</c> here, so this method stays pure/hand-
    /// traceable (see the class doc comment) instead of silently depending
    /// on when it happens to run. <see cref="PayslipDocument"/> captures one
    /// <c>DateTime.Now</c> per print run and passes the same value into
    /// every employee's Build() call, so a multi-slip batch shows one
    /// consistent generation time rather than drifting slip to slip while
    /// the PDF renders. <paramref name="companyName"/> is the same
    /// already-resolved-to-a-default shape -- <see cref="PayslipDocument"/>
    /// passes the same one value through to every slip in the run, so a
    /// batch never shows two different names even if Settings were somehow
    /// edited mid-render.</summary>
    public static IReadOnlyList<PayslipLine> Build(PayrollResult result, DateTime generatedAt, string companyName)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);

        var lines = new List<PayslipLine>
        {
            new(Center(companyName)),
            new(Center("PAYSLIP")),
            new(Rule()),
            new(JustifyTwoColumns(result.EmployeeName, $"ID {result.EmployeeId}")),
            new(PeriodText(result.PeriodStart, result.PeriodEnd)),
            new(Rule()),
            new("GROSS PAY", Bold: true),
        };

        foreach (var line in result.ComputedGrossPay)
            AddAmountLine(lines, indent: 2, line.Label, line.Amount);

        foreach (var group in result.GrossPayAdjustmentGroups)
            AddAdjustmentGroup(lines, group);

        AddAmountLine(lines, indent: 0, "Subtotal", result.TotalGrossPay, bold: true);

        lines.Add(new(string.Empty));
        lines.Add(new("DEDUCTIONS", Bold: true, Red: true));

        foreach (var line in result.ComputedDeductions)
        {
            // A waived line (currently only ever Undertime -- see PayrollLineItem.Waived)
            // still prints its real computed Amount, same as PayrollSummaryView shows it --
            // "should still be calculated" holds on paper too, not just on screen.
            // Total Deductions below excludes it (see PayrollResult.TotalDeductions).
            AddAmountLine(lines, indent: 2, line.Label, line.Amount, red: true, strikethrough: line.Waived);
        }

        foreach (var group in result.DeductionAdjustmentGroups)
            AddAdjustmentGroup(lines, group, red: true);

        AddAmountLine(lines, indent: 0, "Subtotal", result.TotalDeductions, bold: true, red: true);

        lines.Add(new(string.Empty));
        lines.Add(new(Rule()));
        AddAmountLine(lines, indent: 0, "NET PAY", result.NetPay, bold: true);
        lines.Add(new(Rule()));

        // Signature footer -- two blank lines of physical space above the
        // rule (the rule itself doubles as the line to sign on) captioned
        // underneath, so a printed and cut stub can be handed to the
        // employee and physically signed for as received. Exactly
        // FooterLineCount (7) lines from here to the end of this method --
        // see that constant's own doc comment for why a renderer cares
        // about that count.
        lines.Add(new("Received by:"));
        lines.Add(new(string.Empty));
        lines.Add(new(string.Empty));
        lines.Add(new(Rule()));
        lines.Add(new(Center("Name & Signature")));
        lines.Add(new(string.Empty));

        // Very bottom of the slip -- when this particular PDF was produced,
        // not when the period was worked or paid (PeriodText, above, already
        // covers that). "hh:mm tt" (12-hour clock + AM/PM) rather than "HH:mm"
        // to match the rest of this payslip's plain, non-technical tone.
        // Centered like "Name & Signature" just above it, rather than
        // left-aligned like every other line on the slip.
        lines.Add(new(Center($"Date Generated: {generatedAt:MMMM d, yyyy hh:mm tt}")));

        return lines;
    }

    /// <summary>One adjustment category's contribution -- nothing at all when
    /// empty, one plain amount line for a single-value type (Allowance/Premium
    /// Pay/SSS/PhilHealth/Pag-IBIG/Cash Advance), or a category header line plus
    /// one indented line per row for a genuinely itemized type (Incentive/
    /// Other Charges). No per-category Subtotal line --
    /// every row feeds straight into the section's own Total Gross Pay/Total
    /// Deductions line instead, which is the only running total a slip
    /// shows. <paramref name="red"/> is just threaded straight down from
    /// the Deductions-side caller in <see cref="Build"/> so every line this
    /// emits inherits the section's color without re-deriving it here.
    /// </summary>
    private static void AddAdjustmentGroup(List<PayslipLine> lines, PayrollAdjustmentGroup group, bool red = false)
    {
        if (group.Adjustments.Count == 0)
            return;

        if (group.IsSingleValue)
        {
            AddAmountLine(lines, indent: 2, group.Type.ToText(), group.SingleValueAdjustment!.Amount, red: red);
            return;
        }

        lines.Add(new(Indent(2) + group.Type.ToText(), Red: red));

        foreach (var adjustment in group.Adjustments)
            AddAmountLine(lines, indent: 4, adjustment.Description, adjustment.Amount, red: red);
    }

    /// <summary>Appends one label/amount line, attaching the formatted amount
    /// as <see cref="PayslipLine.AmountText"/> rather than folding it into
    /// <see cref="PayslipLine.Text"/> via padding -- <see
    /// cref="PayslipDocument.ComposeLine"/> is what actually right-aligns it,
    /// inside its own fixed-width layout box, so every money line's amount
    /// lands in the same page column via QuestPDF's layout engine itself,
    /// not char-count arithmetic that has to assume every font weight
    /// advances identically (an assumption that doesn't hold for every
    /// installed-font/fallback path -- see <see cref="PayslipDocument.
    /// EnsureLayoutConfigured"/>'s own doc comment for the fragile
    /// letter-spacing fix this replaced). Word-wraps the label onto its own
    /// continuation line(s) first if it's too long to share a line with the
    /// amount column at all -- e.g. a hand-typed itemized Description longer
    /// than the usual "Uniform"/"Loan repayment 2/5" case. A wrapped label
    /// costs extra lines the same way any other content does, which is
    /// exactly what feeds PayslipDocument's own line-count overflow check
    /// (build-order step 2) -- this class doesn't special-case it any
    /// further than that.
    /// <paramref name="bold"/>/<paramref name="red"/>/<paramref
    /// name="strikethrough"/> apply to every line the wrap produces,
    /// including continuation lines, since they're all still visually one
    /// row -- for a line carrying AmountText, ComposeLine only ever applies
    /// Strikethrough to the amount span (never the label), and for a
    /// wrapped continuation line with no AmountText of its own,
    /// Strikethrough applies to the whole line, since label text is all
    /// that line has.</summary>
    private static void AddAmountLine(
        List<PayslipLine> lines, int indent, string label, decimal amount, bool bold = false, bool red = false, bool strikethrough = false)
    {
        string amountText = amount.ToString("N2", CultureInfo.InvariantCulture);
        string indentStr = Indent(indent);
        int availableForLabel = Math.Max(0, LineWidth - indent - AmountFieldWidth);

        // The overwhelmingly common case -- every computed/single-value line,
        // and the great majority of hand-typed Descriptions -- fits the label
        // and the amount on one line together.
        if (label.Length <= availableForLabel)
        {
            lines.Add(new(indentStr + label, bold, red, strikethrough, amountText));
            return;
        }

        foreach (var wrapped in WordWrap(label, Math.Max(1, availableForLabel)))
            lines.Add(new(indentStr + wrapped, bold, red, strikethrough));

        lines.Add(new(string.Empty, bold, red, strikethrough, amountText));
    }

    private static string Indent(int spaces) => new(' ', spaces);

    private static string Rule() => new('-', LineWidth);

    /// <summary>Full-width rule indented to match a Total line's own
    /// <paramref name="indent"/> -- same right edge as <see cref="Rule"/>
    /// (column LineWidth), just starting <paramref name="indent"/> columns
    /// in instead of at column 0, so it reads as "this total sums everything
    /// above it" the way an indented running total does on a paper receipt,
    /// rather than the section-closing rule <see cref="Rule"/> itself is
    /// used for. Used directly above both Total Gross Pay and Total
    /// Deductions.</summary>
    private static string IndentedRule(int indent) => Indent(indent) + new string('-', LineWidth - indent);

    /// <summary>Horizontally centers text within LineWidth -- used for the
    /// signature footer's "Name & Signature" caption under its own Rule.
    /// Leftover odd space (LineWidth minus text.Length doesn't split evenly
    /// in two) goes on the right, same as PadRight elsewhere in this class
    /// -- trailing whitespace is invisible either way, so there's nothing to
    /// get wrong by rounding down on the left instead of up.</summary>
    private static string Center(string text)
    {
        int padding = Math.Max(0, LineWidth - text.Length);
        int left = padding / 2;
        return new string(' ', left) + text;
    }

    private static string JustifyTwoColumns(string left, string right)
    {
        int gap = LineWidth - left.Length - right.Length;
        return gap > 0 ? left + new string(' ', gap) + right : left + " " + right;
    }

    /// <summary>"August 1 - August 15, 2026" for a same-year period, repeating
    /// the /// month name on both ends even within a single month, matching
    /// Print_Feature.md's own sample exactly -- or "December 20, 2026 - January 3,
    /// 2027" once a period crosses a year boundary, so the year is never
    /// ambiguous on either end.</summary>
    private static string PeriodText(DateOnly start, DateOnly end) =>
        start.Year == end.Year
            ? $"{start:MMMM d} - {end:MMMM d, yyyy}"
            : $"{start:MMMM d, yyyy} - {end:MMMM d, yyyy}";

    /// <summary>Greedy word-wrap -- fills each line up to <paramref
    /// name="width"/> with whole words, and hard-splits a single word longer
    /// than <paramref name="width"/> by itself rather than looping forever
    /// (a pathological all-one-word Description with no spaces in it at
    /// all). Always yields at least one line, even for an empty label, so
    /// the amount line that follows in <see cref="AddAmountLine"/> never
    /// loses its own row entirely.</summary>
    private static IEnumerable<string> WordWrap(string text, int width)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var current = new StringBuilder();

        foreach (var word in words)
        {
            string remaining = word;

            while (remaining.Length > width)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return remaining[..width];
                remaining = remaining[width..];
            }

            int neededLength = current.Length == 0 ? remaining.Length : current.Length + 1 + remaining.Length;

            if (neededLength > width && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');

            current.Append(remaining);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}