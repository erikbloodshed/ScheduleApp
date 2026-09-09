using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScheduleApp.Payroll;
using ScheduleApp.Payroll.Pdf;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Build-order step 3 (Print_Feature.md): the in-app Prev/Next preview,
/// rasterized via QuestPDF's own <c>GenerateImages</c> (see <see
/// cref="PayslipRenderer.RenderPreview"/>) rather than a separate PDF-
/// rendering dependency. Opened by both <c>PayrollViewModel.
/// PrintCurrentPayslipCommand</c> (a single-employee <see
/// cref="PayrollResult"/>) and the "Print Payslips…" batch command (many) --
/// this dialog itself doesn't care which; it always takes a plain list.
///
/// Rendering happens on a background thread (see <see
/// cref="LoadPreviewAsync"/>) since composing/rasterizing a multi-employee
/// batch is real CPU work that would otherwise freeze the dialog while it
/// runs. Save…/Open/Print here keep Open as a separate, repeatable button
/// (unlike ReportViewModel.ExportSummary, which now opens the file itself the
/// moment it's written, with no separate Open button left at all -- see that
/// method's own doc comment) because Open and Print are two independent
/// actions a person might reach for at different times after Save…, not just
/// once right after saving; _savedPath below is what lets either one reuse
/// the same written file on demand. Print is the one action beyond Save/Open
/// this dialog adds -- see PrintButton_Click's own doc comment for why it
/// opens a custom, in-app preview window rather than going straight to WPF's
/// own PrintDialog (or shelling out to whatever's registered as the default
/// PDF handler, which is what an even earlier version of this method did).
/// </summary>
public partial class PayslipPreviewDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IReadOnlyList<PayrollResult> _payrolls;

    /// <summary>Already resolved to whatever's effective before this dialog was
    /// constructed (Settings' Payroll:CompanyName, or PayslipLineBuilder.DefaultCompanyName
    /// if that's blank/unset) -- see PayrollSummaryViewModel/PayrollPrintExportViewModel's
    /// own construction sites for where that resolution happens. This dialog doesn't read
    /// configuration itself, same "pure data in" boundary _payrolls above already draws.</summary>
    private readonly string _companyName;

    private List<BitmapImage> _pageImages = [];
    private int _currentPageIndex;

    /// <summary>Set once Save… succeeds -- lets Open/Print reuse the exact
    /// file that was already written instead of asking again or writing a
    /// second throwaway copy. See this class's own doc comment for why Open
    /// stays a separate, repeatable button here rather than firing
    /// automatically the way ReportViewModel.ExportSummary's now does.</summary>
    private string? _savedPath;

    public PayslipPreviewDialog(IReadOnlyList<PayrollResult> payrolls, string companyName)
    {
        InitializeComponent();

        ArgumentNullException.ThrowIfNull(payrolls);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        _payrolls = payrolls;
        _companyName = companyName;

        Loaded += async (_, _) => await LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        SetBusy(true);
        try
        {
            var preview = await Task.Run(() => PayslipRenderer.RenderPreview(_payrolls, _companyName));

            _pageImages = preview.PageImages.Select(BytesToBitmapImage).ToList();
            _currentPageIndex = 0;
            UpdatePageDisplay();

            if (preview.OverflowEmployeeNames.Count > 0)
            {
                string who = string.Join(", ", preview.OverflowEmployeeNames);
                OverflowNoticeText.Text = preview.OverflowEmployeeNames.Count == 1
                    ? $"{who} needed a full page to themself -- too much content for a quarter-page cell."
                    : $"{who} each needed a full page to themselves -- too much content for a quarter-page cell.";
                OverflowNoticeText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not render the payslip preview: {ex.Message}",
                "Preview failed", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Disables paging/Save/Print (Open is separately gated on
    /// _savedPath, not busy state, since it does no rendering of its own)
    /// and swaps the page image for a "Rendering…" placeholder while either
    /// the initial preview or a Save's own PDF generation is in flight.
    /// Print no longer renders anything of its own -- it reuses the page
    /// images already loaded -- so PrintButton_Click doesn't call this; its
    /// IsEnabled stays gated on whatever the initial preview load last set
    /// here.</summary>
    private void SetBusy(bool busy)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PageImage.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = !busy;
        PrintButton.IsEnabled = !busy;
        UpdatePagingButtons(busy);
    }

    private void UpdatePageDisplay()
    {
        if (_pageImages.Count == 0)
        {
            PageImage.Source = null;
            PageLabel.Text = "No pages";
            UpdatePagingButtons(busy: true);
            return;
        }

        PageImage.Source = _pageImages[_currentPageIndex];
        PageLabel.Text = $"Page {_currentPageIndex + 1} of {_pageImages.Count}";
        UpdatePagingButtons(busy: false);
    }

    private void UpdatePagingButtons(bool busy)
    {
        PrevPageButton.IsEnabled = !busy && _currentPageIndex > 0;
        NextPageButton.IsEnabled = !busy && _currentPageIndex < _pageImages.Count - 1;
    }

    private void PrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPageIndex <= 0) return;
        _currentPageIndex--;
        UpdatePageDisplay();
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPageIndex >= _pageImages.Count - 1) return;
        _currentPageIndex++;
        UpdatePageDisplay();
    }

    private static BitmapImage BytesToBitmapImage(byte[] bytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Payslips",
            Filter = "PDF Document (*.pdf)|*.pdf",
            FileName = BuildDefaultFileName(),
        };

        if (dialog.ShowDialog(this) != true) return;

        await SaveToAsync(dialog.FileName);
    }

    private async Task SaveToAsync(string path)
    {
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => PayslipRenderer.RenderToPdf(_payrolls, _companyName));
            await File.WriteAllBytesAsync(path, result.PdfBytes);

            _savedPath = path;
            OpenButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedPath is null) return;
        Process.Start(new ProcessStartInfo(_savedPath) { UseShellExecute = true });
    }

    /// <summary>Opens the custom print preview built by <see
    /// cref="ShowPrintPreview"/> instead of going straight to WPF's own
    /// <see cref="PrintDialog"/>. WPF's PrintDialog has a preview pane in its
    /// chrome, but there's nothing behind it -- WPF never implemented a
    /// hookup to feed it a previewable package, so it just shows "This app
    /// doesn't support print preview" (a known WPF limitation, not something
    /// specific to this app). This replaces an even earlier version that
    /// shell-executed the OS's registered PDF handler's "print" verb, which
    /// had the opposite problem: no dialog at all, since QuestPDF has no
    /// direct print-to-printer API of its own (only Generate*/GenerateImages
    /// -- see PayslipRenderer's own doc comment) and Edge's PDF handler sends
    /// straight to the default printer with no dialog and no way to pick a
    /// different one. A real Windows PrintDialog still shows up -- there's no
    /// way around one somewhere in the flow, since that's what actually
    /// offers the printer picker -- but only once <see cref="DocumentViewer"/>'s
    /// own Print command is invoked from inside the preview window, so a
    /// person has already seen the real thing before that point rather than
    /// a placeholder message.</summary>
    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageImages.Count == 0) return;

        ShowPrintPreview(BuildPrintDocument());
    }

    /// <summary>One <see cref="FixedPage"/> per already-rendered preview
    /// image (see LoadPreviewAsync/_pageImages), sized to a nominal Letter
    /// page (8.5x11", the same page every payslip is composed at -- see
    /// PayslipDocument's own doc comment) at WPF's 96-DPI device-independent
    /// units. This is the one document both <see cref="ShowPrintPreview"/>'s
    /// DocumentViewer displays and its own Print command sends to the
    /// printer -- one render, reused for both looking and printing, same
    /// "what's previewed is what prints" property PayslipRenderer.RenderPreview's
    /// own doc comment describes for the PDF path. Reusing these images
    /// (already rasterized at ImageGenerationSettings.Default's 288 DPI)
    /// also skips composing/rendering anything a second time.</summary>
    private FixedDocument BuildPrintDocument()
    {
        const double PageWidth = 8.5 * 96;
        const double PageHeight = 11 * 96;

        var document = new FixedDocument();
        foreach (var pageImage in _pageImages)
        {
            var image = new Image
            {
                Source = pageImage,
                Stretch = Stretch.Uniform,
                Width = PageWidth,
                Height = PageHeight,
            };

            var fixedPage = new FixedPage { Width = PageWidth, Height = PageHeight };
            fixedPage.Children.Add(image);

            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(fixedPage);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    /// <summary>The custom print preview itself -- a plain <see
    /// cref="DocumentViewer"/> (page thumbnails, zoom, Prev/Next -- all built
    /// in) showing <paramref name="document"/> inside its own modal window.
    /// No print logic lives here: DocumentViewer's own toolbar already has a
    /// Print button wired to <c>ApplicationCommands.Print</c>, which opens
    /// WPF's PrintDialog and sends <c>document.DocumentPaginator</c> to
    /// whatever printer is picked there -- the same PrintDialog
    /// PrintButton_Click used to call directly, just reached one step later,
    /// after an actual look at the payslip instead of a "doesn't support
    /// preview" placeholder.</summary>
    private void ShowPrintPreview(FixedDocument document)
    {
        var previewWindow = new Window
        {
            Title = "Print Preview",
            Width = 850,
            Height = 950,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DocumentViewer { Document = document },
        };

        previewWindow.ShowDialog();
    }

    private string BuildDefaultFileName()
    {
        string suffix = _payrolls.Count == 1
            ? _payrolls[0].EmployeeName.Replace(' ', '_')
            : $"{_payrolls.Count}_Employees";

        return $"Payslips_{suffix}_{DateTime.Now:MMddyy}.pdf";
    }
}
