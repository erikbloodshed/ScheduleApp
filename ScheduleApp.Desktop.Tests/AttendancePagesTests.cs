using System.Windows;
using System.Windows.Media;
using NSubstitute;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The Summary and Punch Records pages, shown, bound to a whole Attendance tab.</summary>
public sealed class AttendancePagesTests : IDisposable
{
    private readonly TestAttendance _attendance = new();

    public AttendancePagesTests()
    {
        _attendance.Runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AttendanceRunResult
            {
                Summaries =
                [
                    new AttendanceSummary { EmployeeId = 3, EmployeeName = "Cruz", ShiftDate = call.Arg<AttendanceRunRequest>().PeriodStart, Status = PunchStatus.Partial },
                    new AttendanceSummary { EmployeeId = 4, EmployeeName = "Dizon", ShiftDate = call.Arg<AttendanceRunRequest>().PeriodStart, Status = PunchStatus.Complete },
                ],
                Employees = [TestRoster.Employee(3, "Cruz"), TestRoster.Employee(4, "Dizon")],
            }));
        _attendance.Logs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<AttendanceLog> { new() { EmployeeId = 3, Timestamp = DateTime.Today.AddHours(8) } }));
        _attendance.Roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Core.Models.Department> { _attendance.Roster.Bakery, _attendance.Roster.Kitchen }));
        _attendance.Roster.Repository.GetUnassignedEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_attendance.Roster.Unassigned));
    }

    public void Dispose() => _attendance.Dispose();

    private static Task WithWindowAsync<TPage>(Func<TPage> newPage, Func<TPage, Task> test) where TPage : FrameworkElement =>
        UiThread.RunAsync(async () =>
        {
            var page = newPage();
            var window = await UiThread.ShowAsync(new Window { Content = page, Width = 1300, Height = 800 });
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
    public Task The_summary_page_shows_the_report_and_its_tiles() => WithWindowAsync(
        () => new AttendanceSummaryPage(_attendance.ViewModel),
        async page =>
        {
            var view = page.SummaryView;
            Assert.Equal(Visibility.Collapsed, view.CountsCard.Visibility);
            Assert.Equal(Visibility.Visible, view.EmptyText.Visibility);

            await page.OnNavigatedToAsync();
            await Until.TrueAsync(() => page.ViewModel!.Report.HasResults && !_attendance.Busy.IsRunning, "the report");
            await UiThread.IdleAsync();

            var report = page.ViewModel!.Report;
            Assert.Same(page.ViewModel.ReportScope.VisibleDepartments, view.ScopeTree.ItemsSource);
            Assert.Equal("Whole company (all employees checked)", view.ScopeText.Text);
            Assert.Equal(Visibility.Visible, view.CountsCard.Visibility);
            Assert.Equal(Visibility.Visible, view.SummaryGrid.Visibility);
            Assert.Equal(Visibility.Collapsed, view.EmptyText.Visibility);
            Assert.Same(report.SummaryRowsView, view.SummaryGrid.ItemsSource);
            Assert.Equal(1, view.PartialTile.Content);
            Assert.Same(report.ShowStatusDetailCommand, view.PartialTile.Command);
            Assert.Equal(Visibility.Collapsed, view.ClearFilterButton.Visibility);

            // A tile narrows the grid and lights up.
            view.PartialTile.Command.Execute(view.PartialTile.CommandParameter);
            await UiThread.IdleAsync();
            Assert.Equal(PunchStatus.Partial, report.SelectedStatusFilter);
            Assert.Equal(Visibility.Visible, view.ClearFilterButton.Visibility);
            Assert.NotEqual(Brushes.Transparent, view.PartialTile.Background);
            Assert.Equal(Brushes.Transparent, view.CompleteTile.Background);

            view.ClearFilterButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal(Brushes.Transparent, view.PartialTile.Background);
        });

    [Fact]
    public Task The_summary_period_pickers_and_scope_search_bind_both_ways() => WithWindowAsync(
        () => new AttendanceSummaryPage(_attendance.ViewModel),
        async page =>
        {
            await page.OnNavigatedToAsync();
            var view = page.SummaryView;
            var vm = page.ViewModel!;

            view.PeriodStartPicker.DateTime = new DateTime(2026, 9, 1);
            await UiThread.IdleAsync();
            Assert.Equal(new DateTime(2026, 9, 1), vm.Report.PeriodStart);

            view.ScopeSearchBox.Text = "Bakery";
            await UiThread.IdleAsync();
            Assert.Equal("Bakery", Assert.Single(vm.ReportScope.VisibleDepartments).Name);

            Assert.Same(vm.ReportScope.SelectAllTreeCommand, view.SelectAllButton.Command);
            Assert.Same(vm.Report.ExportSummaryCommand, view.ExportSummaryButton.Command);
            Assert.Same(vm.Report.RefreshOrCancelSummaryCommand, view.RefreshOrCancelButton.Command);
        });

    [Fact]
    public Task The_punch_records_page_shows_the_punches_and_its_toolbar() => WithWindowAsync(
        () => new PunchRecordsPage(_attendance.ViewModel),
        async page =>
        {
            await page.OnNavigatedToAsync();
            await Until.TrueAsync(() => page.ViewModel!.PunchRecords.HasLoadedStoredLogs && !_attendance.Busy.IsRunning, "the punches");
            await UiThread.IdleAsync();

            var view = page.RecordsView;
            var vm = page.ViewModel!;
            Assert.Same(vm.PunchRecords.StoredLogsView, view.LogsGrid.ItemsSource);
            Assert.Equal("1", view.CountRun.Text);
            Assert.Equal(Visibility.Visible, view.CountLine.Visibility);
            Assert.Equal("Reload", view.RefreshOrCancelButton.Label);
            Assert.Same(vm.Import.ImportPunchLogCommand, view.ImportButton.Command);
            Assert.Same(vm.DeviceFetch.FetchFromDeviceCommand, view.FetchButton.Command);
            Assert.Same(vm.PunchRecords.ExportStoredLogsCommand, view.ExportButton.Command);

            view.PunchSearchTextBox.Text = "Cru";
            await Until.TrueAsync(() => view.SuggestionsPopup.IsOpen, "the suggestions");
            Assert.Same(vm.PunchRecords.LogViewSuggestions, view.PunchSearchSuggestionList.ItemsSource);
        });
}
