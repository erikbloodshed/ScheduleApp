using System.IO;
using System.Reactive.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScheduleApp.Payroll;
using ScheduleApp.Payroll.Pdf;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Build-order step 3 (Print_Feature.md): the in-app Prev/Next payslip preview, rasterized via
/// QuestPDF's own <c>GenerateImages</c> (see <see cref="PayslipRenderer.RenderPreview"/>) rather
/// than a separate PDF-rendering dependency. Shown for one employee (Print Current Payslip) or
/// many ("Print Payslips…") -- it always takes a plain list.
///
/// Rendering, and decoding the rendered pages, happen on a background thread, since
/// composing/rasterizing a multi-employee batch is real CPU work that would otherwise freeze the
/// dialog. Save…/Open/Print keep Open as a separate, repeatable button because Open and Print are
/// two independent actions a person might reach for at different times after Save…;
/// <see cref="SavedPath"/> is what lets either reuse the same written file on demand.
/// </summary>
public partial class PayslipPreviewViewModel : ReactiveViewModel
{
    private readonly IReadOnlyList<PayrollResult> _payrolls;

    /// <summary>Already resolved to whatever's effective (Settings' Payroll:CompanyName, or
    /// PayslipLineBuilder.DefaultCompanyName if that's blank) by whoever opens the preview; this
    /// doesn't read configuration itself.</summary>
    private readonly string _companyName;

    private readonly Func<IReadOnlyList<PayrollResult>, string, PayslipPreviewResult> _renderPreview;
    private readonly Func<IReadOnlyList<PayrollResult>, string, byte[]> _renderPdf;

    private readonly IObservable<bool> _canPreviousPage;
    private readonly IObservable<bool> _canNextPage;
    private readonly IObservable<bool> _canSave;
    private readonly IObservable<bool> _canOpenSaved;
    private readonly IObservable<bool> _canPrint;

    public PayslipPreviewViewModel(IReadOnlyList<PayrollResult> payrolls, string companyName)
        : this(payrolls, companyName, PayslipRenderer.RenderPreview, (p, c) => PayslipRenderer.RenderToPdf(p, c).PdfBytes)
    {
    }

    /// <summary>With the renderers swapped out -- for tests, which don't need QuestPDF.</summary>
    internal PayslipPreviewViewModel(
        IReadOnlyList<PayrollResult> payrolls,
        string companyName,
        Func<IReadOnlyList<PayrollResult>, string, PayslipPreviewResult> renderPreview,
        Func<IReadOnlyList<PayrollResult>, string, byte[]> renderPdf)
    {
        ArgumentNullException.ThrowIfNull(payrolls);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        _payrolls = payrolls;
        _companyName = companyName;
        _renderPreview = renderPreview;
        _renderPdf = renderPdf;

        var notRendering = this.WhenAnyValue(x => x.IsRendering).Select(rendering => !rendering);
        _canPreviousPage = this.WhenAnyValue(x => x.IsRendering, x => x.PageIndex,
            (rendering, index) => !rendering && index > 0);
        _canNextPage = this.WhenAnyValue(x => x.IsRendering, x => x.PageIndex, x => x.Pages,
            (rendering, index, pages) => !rendering && index < pages.Count - 1);
        _canSave = notRendering;
        _canOpenSaved = this.WhenAnyValue(x => x.SavedPath).Select(path => path is not null);
        _canPrint = this.WhenAnyValue(x => x.IsRendering, x => x.Pages,
            (rendering, pages) => !rendering && pages.Count > 0);

        // Paging, Save and Print wait while either the preview or a Save's PDF is rendering.
        _isRenderingHelper = Observable.CombineLatest(LoadPreviewCommand.IsExecuting, SaveCommand.IsExecuting,
                (loading, saving) => loading || saving)
            .ToProperty(this, x => x.IsRendering);
        _currentPageHelper = this.WhenAnyValue(x => x.Pages, x => x.PageIndex,
                (pages, index) => pages.Count == 0 ? null : pages[index])
            .ToProperty(this, x => x.CurrentPage);
        _pageLabelHelper = this.WhenAnyValue(x => x.Pages, x => x.PageIndex,
                (pages, index) => pages.Count == 0 ? "No pages" : $"Page {index + 1} of {pages.Count}")
            .ToProperty(this, x => x.PageLabel);
    }

    /// <summary>The rendered pages, in print order -- frozen, so they were decoded off the UI
    /// thread and can be shown or printed from it.</summary>
    [Reactive]
    public partial IReadOnlyList<ImageSource> Pages { get; private set; } = [];

    [Reactive]
    public partial int PageIndex { get; private set; }

    [ObservableAsProperty]
    public partial ImageSource? CurrentPage { get; }

    [ObservableAsProperty(InitialValue = "No pages")]
    public partial string PageLabel { get; }

    [ObservableAsProperty]
    public partial bool IsRendering { get; }

    /// <summary>Who needed a full page of their own -- too much content for a quarter-page cell
    /// (see Print_Feature.md's Layout section: "flagged in the export result"); null when
    /// everyone fit.</summary>
    [Reactive]
    public partial string? OverflowNotice { get; private set; }

    /// <summary>Set once Save… succeeds, so Open reuses the file already written instead of
    /// asking again.</summary>
    [Reactive]
    public partial string? SavedPath { get; private set; }

    /// <summary>Shows the pages in a print preview -- the View answers it with a
    /// DocumentViewer, whose own Print button offers the printer picker.</summary>
    public Interaction<IReadOnlyList<ImageSource>, RxVoid> PrintPages { get; } = new();

    /// <summary>Renders the preview; false if it couldn't, after saying why -- there's nothing
    /// to show then, so the dialog closes.</summary>
    [ReactiveCommand]
    private async Task<bool> LoadPreviewAsync()
    {
        try
        {
            var (pages, overflow) = await Task.Run(() =>
            {
                var preview = _renderPreview(_payrolls, _companyName);
                return ((IReadOnlyList<ImageSource>)[.. preview.PageImages.Select(Decode)], preview.OverflowEmployeeNames);
            });

            PageIndex = 0;
            Pages = pages;
            OverflowNotice = DescribeOverflow(overflow);
            return true;
        }
        catch (Exception ex)
        {
            await NotifyAsync($"Could not render the payslip preview: {ex.Message}", "Preview failed", NoticeKind.Error);
            return false;
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canPreviousPage))]
    private void PreviousPage() => PageIndex--;

    [ReactiveCommand(CanExecute = nameof(_canNextPage))]
    private void NextPage() => PageIndex++;

    [ReactiveCommand(CanExecute = nameof(_canSave))]
    private async Task SaveAsync()
    {
        if (await PickFileToSaveAsync("PDF Document (*.pdf)|*.pdf", DefaultFileName(), "Save Payslips") is not { } path)
            return;

        try
        {
            var pdf = await Task.Run(() => _renderPdf(_payrolls, _companyName));
            await File.WriteAllBytesAsync(path, pdf);
            SavedPath = path;
        }
        catch (Exception ex)
        {
            await NotifyAsync(ex.Message, "Could not save", NoticeKind.Error);
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canOpenSaved))]
    private async Task OpenSavedAsync() => await OpenFileAsync(SavedPath!);

    [ReactiveCommand(CanExecute = nameof(_canPrint))]
    private async Task PrintAsync() => await PrintPages.Handle(Pages);

    internal string DefaultFileName()
    {
        var suffix = _payrolls.Count == 1
            ? _payrolls[0].EmployeeName.Replace(' ', '_')
            : $"{_payrolls.Count}_Employees";

        return $"Payslips_{suffix}_{DateTime.Now:MMddyy}.pdf";
    }

    private static string? DescribeOverflow(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return null;

        var who = string.Join(", ", names);
        return names.Count == 1
            ? $"{who} needed a full page to themself -- too much content for a quarter-page cell."
            : $"{who} each needed a full page to themselves -- too much content for a quarter-page cell.";
    }

    private static BitmapImage Decode(byte[] png)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
