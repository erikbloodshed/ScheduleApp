using System.Windows;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class ManualEntriesPageTests : IDisposable
{
    private readonly TestAttendance _attendance = new();

    public ManualEntriesPageTests() =>
        _attendance.ManualLogs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ManualAttendanceLog>
            {
                new() { Id = 1, EmployeeId = 3, Timestamp = DateTime.Today.AddHours(8), PunchType = 0, Reason = "Clock In" },
            }));

    public void Dispose() => _attendance.Dispose();

    private Task WithPageAsync(Func<ManualEntriesPage, Task> test) => UiThread.RunAsync(async () =>
    {
        var page = new ManualEntriesPage(_attendance.ViewModel);
        var window = await UiThread.ShowAsync(new Window { Content = page, Width = 1200, Height = 700 });
        try
        {
            await test(page);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Toolbar_reaches_the_tab_and_the_editor() => WithPageAsync(page =>
    {
        var vm = page.ViewModel!;
        var view = page.EntriesView;
        Assert.Same(vm, view.ViewModel);
        Assert.Same(vm.ManualEntryEditor.AddManualEntryCommand, view.AddButton.Command);
        Assert.Same(vm.ManualEntriesTab.ExportManualEntriesCommand, view.ExportButton.Command);
        Assert.Same(vm.ManualEntriesTab.ImportManualEntriesCommand, view.ImportButton.Command);
        Assert.Same(vm.ManualEntriesTab.PreviousPeriodCommand, view.PreviousPeriodButton.Command);
        Assert.Same(vm.ManualEntriesTab.NextPeriodCommand, view.NextPeriodButton.Command);
        Assert.Same(vm.ManualEntriesTab.RefreshOrCancelManualEntriesCommand, view.RefreshOrCancelButton.Command);
        Assert.Equal("", view.RefreshOrCancelButton.Tag);
        Assert.Equal(vm.ManualEntriesTab.ManualEntriesStart, view.StartPicker.DateTime);
        Assert.Equal(Visibility.Visible, view.EmptyText.Visibility);
        Assert.Equal(Visibility.Collapsed, view.CountLine.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Visiting_loads_the_grid() => WithPageAsync(async page =>
    {
        await page.OnNavigatedToAsync();
        await Until.TrueAsync(() => page.ViewModel!.ManualEntriesTab.HasLoadedManualEntries && !_attendance.Busy.IsRunning, "the entries");
        await UiThread.IdleAsync();

        var view = page.EntriesView;
        Assert.Same(page.ViewModel!.ManualEntriesTab.ManualEntries, view.EntriesGrid.ItemsSource);
        Assert.Equal(Visibility.Visible, view.CountLine.Visibility);
        Assert.Equal("1", view.CountRun.Text);
        Assert.Equal(Visibility.Collapsed, view.EmptyText.Visibility);
    });

    [Fact]
    public Task The_range_pickers_bind_both_ways() => WithPageAsync(async page =>
    {
        var tab = page.ViewModel!.ManualEntriesTab;

        page.EntriesView.EndPicker.DateTime = new DateTime(2026, 9, 30);
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 9, 30), tab.ManualEntriesEnd);

        tab.ManualEntriesStart = new DateTime(2026, 9, 1);
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 9, 1), page.EntriesView.StartPicker.DateTime);
    });
}
