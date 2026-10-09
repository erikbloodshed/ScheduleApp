using System.Reactive.Linq;
using NSubstitute;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class ViewInteractionsTests
{
    private readonly IPayrollRunRepository _runs = Substitute.For<IPayrollRunRepository>();

    public ViewInteractionsTests() =>
        _runs.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new List<PayrollRun>
        {
            new() { Id = 7, Label = "Oct 1-15" },
        }));

    [Fact]
    public Task ShowDialog_opens_the_view_for_the_view_model_and_reports_acceptance() => UiThread.RunAsync(async () =>
    {
        var asker = new ManageHolidaysViewModel(Substitute.For<IHolidayRepository>(), new AttendanceDataVersion());
        using var registration = ViewInteractions.Register(asker);
        var picker = new LoadPayrollGroupViewModel(_runs);

        UiThread.WhenLoaded<LoadPayrollGroupDialog>(dialog =>
        {
            Assert.Same(picker, dialog.ViewModel);
            dialog.RunsListBox.SelectedIndex = 0;
            dialog.LoadButton.Command.Execute(null);
        });
        var accepted = await asker.ShowDialog.Handle(picker);

        Assert.True(accepted);
        Assert.Equal(7, picker.ChosenRun?.Id);
    });

    [Fact]
    public Task ShowDialog_reports_a_cancelled_dialog() => UiThread.RunAsync(async () =>
    {
        var asker = new ManageHolidaysViewModel(Substitute.For<IHolidayRepository>(), new AttendanceDataVersion());
        using var registration = ViewInteractions.Register(asker);
        var picker = new LoadPayrollGroupViewModel(_runs);

        UiThread.WhenLoaded<LoadPayrollGroupDialog>(dialog => dialog.Close());
        var accepted = await asker.ShowDialog.Handle(picker);

        Assert.False(accepted);
        Assert.Null(picker.ChosenRun);
    });
}
