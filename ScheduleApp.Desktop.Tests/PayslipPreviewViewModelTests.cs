using System.IO;
using System.Reactive.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Payroll;
using ScheduleApp.Payroll.Pdf;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

public sealed class PayslipPreviewViewModelTests : IDisposable
{
    private readonly List<PayrollResult> _payrolls =
    [
        TestPayroll.Result(TestRoster.Employee(1, "Alcantara"), 1000m, 100m),
        TestPayroll.Result(TestRoster.Employee(2, "Bautista"), 1000m, 100m),
    ];

    private readonly string _pdfPath = Path.Combine(Path.GetTempPath(), $"payslips-{Guid.NewGuid():N}.pdf");
    private int _pageCount = 3;
    private IReadOnlyList<string> _overflow = [];
    private Exception? _renderFailure;
    private Notice? _notice;
    private string? _savePath;
    private FileRequest? _saveAsked;
    private string? _opened;
    private IReadOnlyList<ImageSource>? _printed;

    public PayslipPreviewViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    public void Dispose() => File.Delete(_pdfPath);

    private PayslipPreviewViewModel NewViewModel(IReadOnlyList<PayrollResult>? payrolls = null)
    {
        var vm = new PayslipPreviewViewModel(payrolls ?? _payrolls, "Panaderia", Render, (_, _) => [1, 2, 3]);
        vm.Notify.RegisterHandler(ctx =>
        {
            _notice = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        vm.PickFileToSave.RegisterHandler(ctx =>
        {
            _saveAsked = ctx.Input;
            ctx.SetOutput(_savePath);
        });
        vm.OpenFile.RegisterHandler(ctx =>
        {
            _opened = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        vm.PrintPages.RegisterHandler(ctx =>
        {
            _printed = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        return vm;
    }

    private PayslipPreviewResult Render(IReadOnlyList<PayrollResult> payrolls, string companyName)
    {
        if (_renderFailure is not null)
            throw _renderFailure;

        return new PayslipPreviewResult
        {
            PageImages = [.. Enumerable.Range(0, _pageCount).Select(_ => TinyPng)],
            OverflowEmployeeNames = _overflow,
        };
    }

    private static readonly byte[] TinyPng = EncodeTinyPng();

    private static byte[] EncodeTinyPng()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null, new byte[4], 2);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    [Fact]
    public async Task Loading_shows_the_first_of_the_rendered_pages()
    {
        var vm = NewViewModel();
        Assert.Equal("No pages", vm.PageLabel);
        Assert.Null(vm.CurrentPage);

        Assert.True(await vm.LoadPreviewCommand.Execute());

        Assert.Equal(3, vm.Pages.Count);
        Assert.All(vm.Pages, page => Assert.True(page.IsFrozen));
        Assert.Same(vm.Pages[0], vm.CurrentPage);
        Assert.Equal("Page 1 of 3", vm.PageLabel);
        Assert.Null(vm.OverflowNotice);
        Assert.False(vm.IsRendering);
    }

    [Fact]
    public async Task Paging_stops_at_either_end()
    {
        var vm = NewViewModel();
        await vm.LoadPreviewCommand.Execute();
        Assert.False(CanExecute(vm.PreviousPageCommand));
        Assert.True(CanExecute(vm.NextPageCommand));

        await vm.NextPageCommand.Execute();
        await vm.NextPageCommand.Execute();

        Assert.Equal("Page 3 of 3", vm.PageLabel);
        Assert.Same(vm.Pages[2], vm.CurrentPage);
        Assert.False(CanExecute(vm.NextPageCommand));
        Assert.True(CanExecute(vm.PreviousPageCommand));

        await vm.PreviousPageCommand.Execute();
        Assert.Equal("Page 2 of 3", vm.PageLabel);
    }

    [Fact]
    public async Task Says_who_needed_a_page_of_their_own()
    {
        _overflow = ["Cruz, Juan"];
        var vm = NewViewModel();
        await vm.LoadPreviewCommand.Execute();
        Assert.Equal("Cruz, Juan needed a full page to themself -- too much content for a quarter-page cell.", vm.OverflowNotice);

        _overflow = ["Cruz, Juan", "Dizon, Juan"];
        await vm.LoadPreviewCommand.Execute();
        Assert.StartsWith("Cruz, Juan, Dizon, Juan each needed", vm.OverflowNotice);
    }

    [Fact]
    public async Task A_failed_render_says_why_and_reports_nothing_to_show()
    {
        _renderFailure = new InvalidOperationException("font missing");
        var vm = NewViewModel();

        Assert.False(await vm.LoadPreviewCommand.Execute());

        Assert.Equal("Could not render the payslip preview: font missing", _notice?.Message);
        Assert.Equal(NoticeKind.Error, _notice?.Kind);
        Assert.Empty(vm.Pages);
        Assert.False(CanExecute(vm.PrintCommand));
    }

    [Fact]
    public async Task Save_writes_the_pdf_where_asked_and_then_open_reuses_it()
    {
        var vm = NewViewModel();
        await vm.LoadPreviewCommand.Execute();
        Assert.False(CanExecute(vm.OpenSavedCommand));
        _savePath = _pdfPath;

        await vm.SaveCommand.Execute();

        Assert.Equal("PDF Document (*.pdf)|*.pdf", _saveAsked?.Filter);
        Assert.Matches(@"^Payslips_2_Employees_\d{6}\.pdf$", _saveAsked?.FileName);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_pdfPath));
        Assert.Equal(_pdfPath, vm.SavedPath);
        Assert.True(CanExecute(vm.OpenSavedCommand));

        await vm.OpenSavedCommand.Execute();
        Assert.Equal(_pdfPath, _opened);
    }

    [Fact]
    public async Task Cancelling_save_writes_nothing()
    {
        var vm = NewViewModel();
        await vm.LoadPreviewCommand.Execute();

        await vm.SaveCommand.Execute();

        Assert.NotNull(_saveAsked);
        Assert.Null(vm.SavedPath);
        Assert.False(File.Exists(_pdfPath));
    }

    [Fact]
    public async Task One_payslip_is_named_after_its_employee()
    {
        var vm = NewViewModel([_payrolls[0]]);
        await vm.LoadPreviewCommand.Execute();

        await vm.SaveCommand.Execute();

        Assert.StartsWith($"Payslips_{_payrolls[0].EmployeeName.Replace(' ', '_')}_", _saveAsked?.FileName);
    }

    [Fact]
    public async Task Print_hands_over_every_page()
    {
        var vm = NewViewModel();
        await vm.LoadPreviewCommand.Execute();

        await vm.PrintCommand.Execute();

        Assert.Same(vm.Pages, _printed);
    }
}
