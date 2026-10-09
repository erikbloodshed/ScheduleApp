using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// The payslip preview (see <see cref="PayslipPreviewViewModel"/>): Prev/Next through the
/// rendered pages, then Save…, Open or Print. Print opens a custom, in-app print preview
/// (<see cref="ShowPrintPreview"/>) rather than going straight to WPF's own PrintDialog, whose
/// preview pane WPF never wired up to anything ("This app doesn't support print preview").
/// </summary>
public partial class PayslipPreviewDialog
{
    /// <summary>Shown for a PayslipPreviewViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public PayslipPreviewDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);
            viewModel.PrintPages.RegisterHandler(interaction =>
            {
                ShowPrintPreview(BuildPrintDocument(interaction.Input));
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.CurrentPage, v => v.PageImage.Source).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsRendering, v => v.PageImage.Visibility,
                rendering => rendering ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsRendering, v => v.BusyPanel.Visibility,
                rendering => rendering ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PageLabel, v => v.PageLabel.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.OverflowNotice, v => v.OverflowNoticeText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.OverflowNotice, v => v.OverflowNoticeText.Visibility,
                notice => notice is null ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.PreviousPageCommand, v => v.PrevPageButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.NextPageCommand, v => v.NextPageButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.SaveCommand, v => v.SaveButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.OpenSavedCommand, v => v.OpenButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.PrintCommand, v => v.PrintButton).DisposeWith(d);

            // Nothing to show if the preview couldn't render (the ViewModel has said why).
            viewModel.LoadPreviewCommand
                .Where(rendered => !rendered)
                .Subscribe(_ => DialogResult = false)
                .DisposeWith(d);
            viewModel.LoadPreviewCommand.Execute().Subscribe().DisposeWith(d);
        });
    }

    /// <summary>One <see cref="FixedPage"/> per already-rendered preview page, sized to a
    /// nominal Letter page (8.5x11", the page every payslip is composed at -- see
    /// PayslipDocument's own doc comment) in WPF's 96-DPI units. The one document both the print
    /// preview shows and its Print command prints -- what's previewed is what prints, without
    /// composing or rendering anything a second time.</summary>
    private static FixedDocument BuildPrintDocument(IReadOnlyList<ImageSource> pages)
    {
        const double PageWidth = 8.5 * 96;
        const double PageHeight = 11 * 96;

        var document = new FixedDocument();
        foreach (var page in pages)
        {
            var fixedPage = new FixedPage { Width = PageWidth, Height = PageHeight };
            fixedPage.Children.Add(new Image
            {
                Source = page,
                Stretch = Stretch.Uniform,
                Width = PageWidth,
                Height = PageHeight,
            });

            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(fixedPage);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    /// <summary>A plain <see cref="DocumentViewer"/> (page thumbnails, zoom, Prev/Next -- all
    /// built in) showing <paramref name="document"/> in its own modal window. Its toolbar's
    /// Print button opens WPF's PrintDialog -- the printer picker -- only after a person has
    /// seen the real thing.</summary>
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
}
