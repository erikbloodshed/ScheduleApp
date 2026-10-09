using System.Reactive.Linq;
using System.Windows;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class ManageHolidaysDialogTests
{
    private readonly IHolidayRepository _repository = Substitute.For<IHolidayRepository>();

    public ManageHolidaysDialogTests() =>
        _repository.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new List<Holiday>
        {
            new() { Id = 1, Date = new DateOnly(2026, 1, 1), Name = "New Year's Day" },
            new() { Id = 2, Date = new DateOnly(2026, 12, 30), Name = "Rizal Day" },
        }));

    private ManageHolidaysDialog NewDialog() =>
        new() { ViewModel = new ManageHolidaysViewModel(_repository, new AttendanceDataVersion()) };

    private Task WithDialogAsync(Func<ManageHolidaysDialog, Task> test) => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            await test(dialog);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Loads_the_list_when_shown() => WithDialogAsync(dialog =>
    {
        Assert.Same(dialog.ViewModel!.Rows, dialog.HolidaysList.ItemsSource);
        Assert.Equal(2, dialog.HolidaysList.Items.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Selection_binds_both_ways() => WithDialogAsync(async dialog =>
    {
        var vm = dialog.ViewModel!;

        dialog.HolidaysList.SelectedItem = vm.Rows[1];
        await UiThread.IdleAsync();
        Assert.Same(vm.Rows[1], vm.SelectedRow);

        vm.SelectedRow = vm.Rows[0];
        await UiThread.IdleAsync();
        Assert.Same(vm.Rows[0], dialog.HolidaysList.SelectedItem);

        dialog.HolidaysList.SelectedItem = null;
        await UiThread.IdleAsync();
        Assert.Null(vm.SelectedRow);
    });

    [Fact]
    public Task Buttons_run_their_commands_and_follow_can_execute() => WithDialogAsync(async dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Same(vm.AddCommand, dialog.AddButton.Command);
        Assert.Same(vm.EditOrSaveCommand, dialog.EditButton.Command);
        Assert.Same(vm.DeleteCommand, dialog.DeleteButton.Command);
        Assert.Equal("Edit…", dialog.EditButton.Label);
        Assert.False(dialog.EditButton.IsEnabled);

        dialog.HolidaysList.SelectedItem = vm.Rows[0];
        await UiThread.IdleAsync();
        Assert.True(dialog.EditButton.IsEnabled);
        Assert.True(dialog.DeleteButton.IsEnabled);

        dialog.EditButton.Command.Execute(null);
        await UiThread.IdleAsync();
        Assert.True(vm.IsEditingRow);
        Assert.Equal("Save", dialog.EditButton.Label);
        Assert.False(dialog.AddButton.IsEnabled);
        Assert.False(dialog.DeleteButton.IsEnabled);
    });

    [Fact]
    public Task Error_line_shows_only_with_a_message() => WithDialogAsync(async dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Equal(Visibility.Collapsed, dialog.ErrorText.Visibility);

        await vm.AddCommand.Execute();
        await vm.EditOrSaveCommand.Execute();
        await UiThread.IdleAsync();

        Assert.Equal(Visibility.Visible, dialog.ErrorText.Visibility);
        Assert.Equal("Pick a date.", dialog.ErrorText.Text);
    });

    [Fact]
    public Task Load_failure_is_shown() => UiThread.RunAsync(async () =>
    {
        _repository.ListAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("offline"));
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            Assert.Equal("Could not load holidays.\n\noffline", dialog.ErrorText.Text);
        }
        finally
        {
            dialog.Close();
        }
    });
}
