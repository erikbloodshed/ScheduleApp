using System.Windows;
using System.Windows.Media;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class ManualLogEntryDialogTests
{
    private readonly TestRoster _roster = new();
    private readonly IAttendanceLogRepository _logs = Substitute.For<IAttendanceLogRepository>();

    private ManualLogEntryDialog NewDialog() => new()
    {
        ViewModel = new ManualLogEntryViewModel([.. _roster.Everyone], _logs),
    };

    [Fact]
    public Task Fields_bind_both_ways() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            var vm = dialog.ViewModel!;
            Assert.Equal("Add Manual Entry", dialog.Title);
            Assert.Equal("Add", dialog.SaveButton.Label);
            Assert.Equal(DateTime.Today, dialog.DateBox.DateTime);
            Assert.Equal(Environment.UserName, dialog.EnteredByBox.Text);
            Assert.Equal("Pick an employee to see that day's machine punches.", dialog.MachinePunchesPlaceholder.Text);

            // The Reason box shows its default grayed out, and follows the punch type.
            Assert.Equal("Clock In", dialog.ReasonBox.Text);
            Assert.Equal(Brushes.Gray, dialog.ReasonBox.Foreground);
            dialog.PunchTypeCombo.SelectedIndex = 1;
            await UiThread.IdleAsync();
            Assert.Equal(1, vm.PunchType);
            Assert.Equal("Clock Out", dialog.ReasonBox.Text);

            dialog.ReasonBox.Text = "Forgot";
            await UiThread.IdleAsync();
            Assert.Equal("Forgot", vm.Reason.Text);
            Assert.NotEqual(Brushes.Gray, dialog.ReasonBox.Foreground);

            dialog.EmployeeBox.Text = "Espino";
            await UiThread.IdleAsync();
            Assert.Equal("Espino", vm.EmployeeText);
            Assert.True(dialog.EmployeeSuggestionsPopup.IsOpen);
            Assert.Single(dialog.EmployeeSuggestionsList.Items);

            vm.Time = new TimeOnly(9, 15);
            await UiThread.IdleAsync();
            Assert.Equal(new TimeOnly(9, 15), dialog.TimeBox.SelectedTime);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Save_closes_once_the_entry_is_complete() => UiThread.RunAsync(async () =>
    {
        var dialog = NewDialog();

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            d.ViewModel!.EmployeeText = "3";
            d.ViewModel.Time = new TimeOnly(8, 0);
            d.SaveButton.Command.Execute(null);
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal(3, dialog.ViewModel!.AcceptedLog?.EmployeeId);
    });
}

public class PunchTimeEntryDialogTests
{
    [Fact]
    public Task Shows_the_slot_and_turns_away_a_missing_time() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(new PunchTimeEntryDialog
        {
            ViewModel = new PunchTimeEntryViewModel("Cruz, Juan", new DateOnly(2026, 9, 16), "Time In", initialTime: null),
        });
        try
        {
            Assert.Equal("Add Manual Punch", dialog.Title);
            Assert.Equal("Cruz, Juan", dialog.ContextEmployeeText.Text);
            Assert.Equal("Time In", dialog.ReasonBox.Text);
            Assert.Equal(Visibility.Collapsed, dialog.ErrorText.Visibility);

            dialog.SaveButton.Command.Execute(null);
            await UiThread.IdleAsync();

            Assert.Equal(Visibility.Visible, dialog.ErrorText.Visibility);
            Assert.Equal("Select a time.", dialog.ErrorText.Text);
            Assert.True(dialog.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    });
}
