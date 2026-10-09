using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using ScheduleApp.Payroll.Pdf;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class PayslipPreviewDialogTests
{
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

    private static PayslipPreviewDialog NewDialog(params string[] overflow) => new()
    {
        ViewModel = new PayslipPreviewViewModel(
            [TestPayroll.Result(TestRoster.Employee(1, "Alcantara"), 1000m, 100m)],
            "Panaderia",
            (_, _) => new PayslipPreviewResult { PageImages = [TinyPng, TinyPng], OverflowEmployeeNames = overflow },
            (_, _) => []),
    };

    private static Task WithDialogAsync(Func<PayslipPreviewDialog> newDialog, Func<PayslipPreviewDialog, Task> test) =>
        UiThread.RunAsync(async () =>
        {
            var dialog = await UiThread.ShowAsync(newDialog());
            try
            {
                // Rendering runs on the thread pool; let it land.
                for (var i = 0; i < 100 && dialog.ViewModel!.Pages.Count == 0; i++)
                    await Task.Delay(10);
                await UiThread.IdleAsync();
                await test(dialog);
            }
            finally
            {
                dialog.Close();
            }
        });

    [Fact]
    public Task Shows_the_first_page_once_rendered() => WithDialogAsync(() => NewDialog(), dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Same(vm.Pages[0], dialog.PageImage.Source);
        Assert.Equal(Visibility.Visible, dialog.PageImage.Visibility);
        Assert.Equal(Visibility.Collapsed, dialog.BusyPanel.Visibility);
        Assert.Equal("Page 1 of 2", dialog.PageLabel.Text);
        Assert.Equal(Visibility.Collapsed, dialog.OverflowNoticeText.Visibility);
        Assert.False(dialog.PrevPageButton.IsEnabled);
        Assert.True(dialog.NextPageButton.IsEnabled);
        Assert.True(dialog.SaveButton.IsEnabled);
        Assert.True(dialog.PrintButton.IsEnabled);
        Assert.False(dialog.OpenButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Next_and_prev_page_through() => WithDialogAsync(() => NewDialog(), async dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Same(vm.NextPageCommand, dialog.NextPageButton.Command);
        Assert.Same(vm.PreviousPageCommand, dialog.PrevPageButton.Command);

        dialog.NextPageButton.Command.Execute(null);
        await UiThread.IdleAsync();

        Assert.Same(vm.Pages[1], dialog.PageImage.Source);
        Assert.Equal("Page 2 of 2", dialog.PageLabel.Text);
        Assert.False(dialog.NextPageButton.IsEnabled);
        Assert.True(dialog.PrevPageButton.IsEnabled);
    });

    [Fact]
    public Task Shows_the_overflow_notice() => WithDialogAsync(() => NewDialog("Cruz, Juan"), dialog =>
    {
        Assert.Equal(Visibility.Visible, dialog.OverflowNoticeText.Visibility);
        Assert.StartsWith("Cruz, Juan needed a full page", dialog.OverflowNoticeText.Text);
        return Task.CompletedTask;
    });
}
