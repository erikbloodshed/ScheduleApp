using System.Diagnostics;
using System.IO;
using System.Windows;
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
/// this dialog adds -- see PrintButton_Click's own doc comment for why it's a
/// shell "print" verb rather than a direct printer API call.
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
    /// the initial preview or a Save/Print's own PDF generation is in
    /// flight.</summary>
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

    /// <summary>Writes to a temp file (if Save… hasn't already written one)
    /// and shell-executes the OS's own "print" verb against it. QuestPDF has
    /// no direct print-to-printer API of its own -- only Generate*/
    /// GenerateImages (see PayslipRenderer's own doc comment) -- so this
    /// hands off to whatever's registered as the default PDF handler (Edge,
    /// Acrobat Reader, etc.), the same "print" action that would run if a
    /// person right-clicked the file and chose Print themselves, just
    /// without that extra manual step. Reuses _savedPath if Save… already
    /// ran (the exact same file, no reason to write it twice); otherwise
    /// writes a throwaway temp copy just for this one print.</summary>
    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            string path = _savedPath ?? Path.Combine(Path.GetTempPath(), BuildDefaultFileName());

            if (_savedPath is null)
            {
                var result = await Task.Run(() => PayslipRenderer.RenderToPdf(_payrolls, _companyName));
                await File.WriteAllBytesAsync(path, result.PdfBytes);
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not send this to your printer directly ({ex.Message}). Try Save… then Open, and print from there instead.",
                "Could not print", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string BuildDefaultFileName()
    {
        string suffix = _payrolls.Count == 1
            ? _payrolls[0].EmployeeName.Replace(' ', '_')
            : $"{_payrolls.Count}_Employees";

        return $"Payslips_{suffix}_{DateTime.Now:MMddyy}.pdf";
    }
}
