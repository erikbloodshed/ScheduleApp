using System.Reactive.Linq;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class ManageHolidaysViewModelTests
{
    private static readonly Holiday NewYear = new() { Id = 1, Date = new DateOnly(2026, 1, 1), Name = "New Year's Day" };
    private static readonly Holiday Rizal = new() { Id = 2, Date = new DateOnly(2026, 12, 30), Name = "Rizal Day" };

    private readonly IHolidayRepository _repository = Substitute.For<IHolidayRepository>();
    private readonly AttendanceDataVersion _version = new();
    private readonly ManageHolidaysViewModel _vm;

    public ManageHolidaysViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _repository.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new List<Holiday> { NewYear, Rizal }));
        _vm = new ManageHolidaysViewModel(_repository, _version);
    }

    private async Task LoadAsync() => await _vm.LoadCommand.Execute();

    private static bool CanExecute(System.Windows.Input.ICommand command) => command.CanExecute(null);

    [Fact]
    public async Task Load_lists_every_holiday_and_selects_nothing()
    {
        await LoadAsync();

        Assert.Equal(["New Year's Day", "Rizal Day"], _vm.Rows.Select(r => r.Name));
        Assert.Equal("January 1, 2026", _vm.Rows[0].DateDisplay);
        Assert.Null(_vm.SelectedRow);
        Assert.False(_vm.IsEditingRow);
        Assert.Equal("Edit…", _vm.EditButtonText);
        Assert.Null(_vm.ErrorMessage);
    }

    [Fact]
    public async Task Load_failure_shows_the_error()
    {
        _repository.ListAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("offline"));

        await LoadAsync();

        Assert.Equal("Could not load holidays.\n\noffline", _vm.ErrorMessage);
    }

    [Fact]
    public async Task Commands_follow_selection_and_editing()
    {
        await LoadAsync();

        Assert.True(CanExecute(_vm.AddCommand));
        Assert.False(CanExecute(_vm.EditOrSaveCommand));
        Assert.False(CanExecute(_vm.DeleteCommand));

        _vm.SelectedRow = _vm.Rows[0];
        Assert.True(CanExecute(_vm.AddCommand));
        Assert.True(CanExecute(_vm.EditOrSaveCommand));
        Assert.True(CanExecute(_vm.DeleteCommand));

        await _vm.EditOrSaveCommand.Execute();
        Assert.True(_vm.IsEditingRow);
        Assert.Equal("Save", _vm.EditButtonText);
        Assert.False(CanExecute(_vm.AddCommand));
        Assert.True(CanExecute(_vm.EditOrSaveCommand));
        Assert.False(CanExecute(_vm.DeleteCommand));
    }

    [Fact]
    public async Task Add_appends_a_blank_row_in_edit_mode_and_cancel_removes_it()
    {
        await LoadAsync();

        await _vm.AddCommand.Execute();

        Assert.Equal(3, _vm.Rows.Count);
        var row = _vm.Rows[2];
        Assert.Same(row, _vm.SelectedRow);
        Assert.True(row.IsNew);
        Assert.True(row.IsEditing);
        Assert.Null(row.EditDate);
        Assert.Equal("", row.EditName);
        Assert.True(_vm.IsEditingRow);

        await _vm.CancelEditCommand.Execute();

        Assert.Equal(2, _vm.Rows.Count);
        Assert.False(_vm.IsEditingRow);
        Assert.Equal("Edit…", _vm.EditButtonText);
    }

    [Fact]
    public async Task Saving_a_new_row_validates_then_adds_and_reloads()
    {
        await LoadAsync();
        await _vm.AddCommand.Execute();
        var row = _vm.SelectedRow!;

        await _vm.EditOrSaveCommand.Execute();
        Assert.Equal("Pick a date.", _vm.ErrorMessage);

        row.EditDate = new DateTime(2026, 6, 12);
        row.EditName = "   ";
        await _vm.EditOrSaveCommand.Execute();
        Assert.Equal("Enter a name for the holiday.", _vm.ErrorMessage);

        row.EditDate = new DateTime(2026, 1, 1);
        row.EditName = "Duplicate";
        await _vm.EditOrSaveCommand.Execute();
        Assert.Equal("January 1, 2026 is already listed as a holiday.", _vm.ErrorMessage);
        await _repository.DidNotReceive().AddAsync(Arg.Any<Holiday>(), Arg.Any<CancellationToken>());

        row.EditDate = new DateTime(2026, 6, 12);
        row.EditName = " Independence Day ";
        await _vm.EditOrSaveCommand.Execute();

        await _repository.Received(1).AddAsync(
            Arg.Is<Holiday>(h => h.Date == new DateOnly(2026, 6, 12) && h.Name == "Independence Day"),
            Arg.Any<CancellationToken>());
        Assert.Equal(1, _version.HolidayVersion);
        Assert.False(_vm.IsEditingRow);
        Assert.Null(_vm.ErrorMessage);
        await _repository.Received(2).ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_a_row_starts_from_its_saved_values_and_updates_it()
    {
        await LoadAsync();
        var row = _vm.Rows[1];
        row.EditName = "stale attempt";
        _vm.SelectedRow = row;

        await _vm.EditOrSaveCommand.Execute();
        Assert.True(row.IsEditing);
        Assert.Equal("Rizal Day", row.EditName);
        Assert.Equal(new DateTime(2026, 12, 30), row.EditDate);

        row.EditName = "Rizal Day (observed)";
        await _vm.EditOrSaveCommand.Execute();

        await _repository.Received(1).UpdateAsync(
            Arg.Is<Holiday>(h => h.Id == 2 && h.Date == new DateOnly(2026, 12, 30) && h.Name == "Rizal Day (observed)"),
            Arg.Any<CancellationToken>());
        Assert.Equal(1, _version.HolidayVersion);
    }

    [Fact]
    public async Task Double_click_edits_only_when_nothing_is_being_edited()
    {
        await LoadAsync();
        _vm.EditSelected();
        Assert.False(_vm.IsEditingRow);

        _vm.SelectedRow = _vm.Rows[0];
        _vm.EditSelected();
        Assert.True(_vm.IsEditingRow);
        Assert.True(_vm.Rows[0].IsEditing);
    }

    [Fact]
    public async Task Delete_asks_first_and_deletes_only_on_yes()
    {
        await LoadAsync();
        _vm.SelectedRow = _vm.Rows[0];
        var answer = false;
        string? asked = null;
        _vm.Confirm.RegisterHandler(ctx =>
        {
            asked = ctx.Input.Message;
            ctx.SetOutput(answer);
        });

        await _vm.DeleteCommand.Execute();
        Assert.Equal("Delete \"New Year's Day\" (January 1, 2026)? This can't be undone.", asked);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        answer = true;
        await _vm.DeleteCommand.Execute();
        await _repository.Received(1).DeleteAsync(1, Arg.Any<CancellationToken>());
        Assert.Equal(1, _version.HolidayVersion);
    }

    [Fact]
    public async Task Save_failure_keeps_the_row_open_with_the_error()
    {
        await LoadAsync();
        _vm.SelectedRow = _vm.Rows[0];
        await _vm.EditOrSaveCommand.Execute();
        _repository.UpdateAsync(Arg.Any<Holiday>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("locked"));

        await _vm.EditOrSaveCommand.Execute();

        Assert.Equal("Could not save the holiday.\n\nlocked", _vm.ErrorMessage);
        Assert.True(_vm.IsEditingRow);
        Assert.Equal(0, _version.HolidayVersion);
    }
}
