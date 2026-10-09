using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Schedule;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class ScheduleAssignmentViewModelTests : IDisposable
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);

    private readonly TestRoster _roster = new();
    private readonly IHolidayRepository _holidays = Substitute.For<IHolidayRepository>();
    private readonly IAttendanceRunner _runner = Substitute.For<IAttendanceRunner>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly IDayPunchPairingEditorLauncher _launcher = Substitute.For<IDayPunchPairingEditorLauncher>();
    private readonly ViewStateStore _viewState = new();
    private readonly MultiSelectModeState _multiSelect = new();
    private readonly List<Holiday> _storedHolidays = [];
    private readonly AttendanceBusyState _busy;
    private readonly ManualEntryEditorViewModel _manualEntryEditor;
    private readonly EmployeeTreeViewModel _tree;
    private readonly ScheduleCalendarViewModel _calendar;
    private readonly ScheduleAssignmentViewModel _vm;
    private bool _confirmAnswer = true;
    private Func<ReactiveViewModel, bool> _dialog = _ => false;

    public ScheduleAssignmentViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _viewState.Schedule.DisplayedMonth = new DateTime(2026, 9, 1);
        _busy = new AttendanceBusyState(_statusBar);

        _roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Department> { _roster.Bakery, _roster.Kitchen }));
        _roster.Repository.GetUnassignedEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_roster.Unassigned));
        _roster.Repository.GetScheduleEntriesForEmployeeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ScheduleEntry>
            {
                new() { Date = Sep1, ScheduleType = ScheduleType.Normal, TimeIn = new TimeOnly(8, 0), WorkTimeHours = 8m },
            }));
        _holidays.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_storedHolidays.ToList()));
        _runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AttendanceRunResult()));

        var dataVersion = _roster.DataVersion;
        var manualLogs = Substitute.For<IManualAttendanceLogRepository>();
        var directory = new AttendanceEmployeeDirectory(_roster.Repository);
        var manualEntries = new ManualEntriesViewModel(manualLogs, _statusBar, _busy, dataVersion, directory,
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 15), initialIsManualEntriesTabSelected: false, () => { },
            new AttendanceTabActivationGate { IsReady = true });
        _manualEntryEditor = new ManualEntryEditorViewModel(manualLogs, Substitute.For<IAttendanceLogRepository>(), _statusBar,
            _busy, dataVersion, directory, manualEntries);

        _tree = new EmployeeTreeViewModel(_roster.Repository, _statusBar, _viewState, dataVersion, () => { }, _busy,
            new PayrollPolicy(), new AttendanceSettings());
        _calendar = new ScheduleCalendarViewModel(_roster.Repository, _holidays, _statusBar, new AttendanceSettings(), _runner, _busy,
            _viewState, () => { }, _tree, _multiSelect, dataVersion);
        _vm = new ScheduleAssignmentViewModel(_roster.Repository, _holidays, _statusBar, new AttendanceSettings(), new PayrollSettings(),
            _busy, dataVersion, _manualEntryEditor, _tree, _calendar, _multiSelect, _launcher);

        _vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        _vm.ShowDialog.RegisterHandler(ctx => ctx.SetOutput(_dialog(ctx.Input)));
    }

    public void Dispose() => _busy.Dispose();

    private static bool CanExecute(ICommand command, object? parameter = null) => command.CanExecute(parameter);

    private CalendarDayViewModel Day(DateOnly date) => _calendar.CalendarDays.Single(d => d.Date == date);

    private Employee Pin(int pin) => _roster.Everyone.Single(e => e.Pin == pin);

    private void Select(params DateOnly[] dates) => _calendar.SelectDates(dates.ToHashSet());

    /// <summary>Loads the tree, selects Cruz (Pin 3), and waits for the calendar to show his
    /// schedule.</summary>
    private async Task ShowCruzAsync()
    {
        await _tree.LoadCommand.Execute();
        _tree.SelectedEmployee = Pin(3);
        await Until.TrueAsync(() => Day(Sep1).Entry is not null && !_busy.IsRunning, "Cruz's schedule");
        _roster.Repository.ClearReceivedCalls();
    }

    private void Caution(string title) =>
        _statusBar.Received().Show(title, Arg.Any<string>(), StatusKind.Caution, Arg.Any<TimeSpan>());

    /// <summary>Accepts the Set Schedule dialog as a 7:00 AM, 9-hour Normal day.</summary>
    private static bool AcceptNineHourDay(ReactiveViewModel dialog)
    {
        var editor = Assert.IsType<ApplyScheduleViewModel>(dialog);
        editor.WorkTimeText = "9";
        editor.TimeIn = new TimeOnly(7, 0);
        return editor.AcceptCommand.Execute().Wait();
    }

    [Fact]
    public async Task Commands_wait_out_anything_running()
    {
        await ShowCruzAsync();
        var day = Day(Sep1);
        Assert.True(CanExecute(_vm.SetScheduleForSelectionCommand));
        Assert.True(CanExecute(_vm.ClearScheduleForSelectionCommand));
        Assert.True(CanExecute(_vm.ToggleHolidayForSelectionCommand));
        Assert.True(CanExecute(_vm.EditPunchPairingForDayCommand, day));

        var gate = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: false, _ => gate.Task);
        Assert.False(CanExecute(_vm.SetScheduleForSelectionCommand));
        Assert.False(CanExecute(_vm.SetLeaveForSelectionCommand));
        Assert.False(CanExecute(_vm.ClearScheduleForSelectionCommand));
        Assert.False(CanExecute(_vm.ToggleHolidayForSelectionCommand));
        Assert.False(CanExecute(_vm.AddManualEntryForDayCommand, day));
        Assert.False(CanExecute(_vm.EditPunchPairingForDayCommand, day));

        gate.SetResult();
        await running;
        Assert.True(CanExecute(_vm.SetScheduleForSelectionCommand));
    }

    [Fact]
    public async Task Multi_select_mode_needs_someone_checked_and_has_no_remove()
    {
        await ShowCruzAsync();

        _multiSelect.IsMultiSelectMode = true;
        Assert.False(CanExecute(_vm.SetScheduleForSelectionCommand));
        Assert.False(CanExecute(_vm.ClearScheduleForSelectionCommand));

        TestRoster.Node(_tree.Departments, 1).IsSelected = true;
        Assert.True(CanExecute(_vm.SetScheduleForSelectionCommand));
        Assert.True(CanExecute(_vm.SetLeaveForSelectionCommand));
        Assert.False(CanExecute(_vm.ClearScheduleForSelectionCommand));
    }

    [Fact]
    public async Task Setting_a_schedule_needs_an_employee_and_days()
    {
        await _vm.SetScheduleForSelectionCommand.Execute(null);
        Caution("No employee selected");

        await ShowCruzAsync();
        await _vm.SetScheduleForSelectionCommand.Execute(null);
        Caution("No days selected");

        await _roster.Repository.DidNotReceiveWithAnyArgs().SetScheduleForDatesAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task Setting_a_schedule_writes_what_the_dialog_settles_on_and_reloads()
    {
        await ShowCruzAsync();
        Select(Sep1, Sep1.AddDays(1));
        ApplyScheduleViewModel? editor = null;
        _dialog = dialog =>
        {
            editor = (ApplyScheduleViewModel)dialog;
            return AcceptNineHourDay(dialog);
        };
        var schedule = _roster.DataVersion.ScheduleVersion;

        await _vm.SetScheduleForSelectionCommand.Execute(ScheduleType.Normal);

        // One of the two days already has a schedule, the other doesn't: an edit, no prefill.
        Assert.Equal("Edit Schedule for Selected Days", editor!.Title);
        Assert.True(editor.ShowsMixedScheduleNote);
        await _roster.Repository.Received(1).SetScheduleForDatesAsync(
            3, Arg.Is<IEnumerable<DateOnly>>(dates => dates.SequenceEqual(new[] { Sep1, Sep1.AddDays(1) })),
            ScheduleType.Normal, 9m, new TimeOnly(7, 0), Arg.Any<IEnumerable<(TimeOnly, TimeOnly, double?, double?)>?>(),
            null, null, null, null, null, null, null, null, null, null, null, null, Arg.Any<CancellationToken>());
        Assert.True(_roster.DataVersion.ScheduleVersion > schedule);
        await _roster.Repository.Received(1).GetScheduleEntriesForEmployeeAsync(3, Arg.Any<CancellationToken>());
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task A_uniform_selection_is_prefilled_and_a_cancelled_dialog_writes_nothing()
    {
        await ShowCruzAsync();
        Select(Sep1);
        ApplyScheduleViewModel? editor = null;
        _dialog = dialog =>
        {
            editor = (ApplyScheduleViewModel)dialog;
            return false;
        };

        await _vm.SetScheduleForSelectionCommand.Execute(ScheduleType.Flexible);

        Assert.Equal(ScheduleType.Flexible, editor!.SelectedType);
        Assert.Equal(new TimeOnly(8, 0), editor.TimeIn);
        Assert.Equal("8", editor.WorkTimeText);
        await _roster.Repository.DidNotReceiveWithAnyArgs().SetScheduleForDatesAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task A_bulk_schedule_goes_to_every_checked_employee_and_leaves_multi_select()
    {
        await ShowCruzAsync();
        _multiSelect.IsMultiSelectMode = true;
        await Until.TrueAsync(() => !_busy.IsRunning, "the blank calendar");
        TestRoster.Node(_tree.Departments, 1).IsSelected = true;
        TestRoster.Node(_tree.Departments, 6).IsSelected = true;
        Select(Sep1.AddDays(2));
        ApplyScheduleViewModel? editor = null;
        _dialog = dialog =>
        {
            editor = (ApplyScheduleViewModel)dialog;
            return AcceptNineHourDay(dialog);
        };

        await _vm.SetScheduleForSelectionCommand.Execute(null);

        Assert.Equal("Set Schedule for 2 Employees", editor!.Title);
        await _roster.Repository.Received(1).SetScheduleForDatesAsync(1, Arg.Any<IEnumerable<DateOnly>>(), ScheduleType.Normal,
            9m, new TimeOnly(7, 0), Arg.Any<IEnumerable<(TimeOnly, TimeOnly, double?, double?)>?>(),
            cancellationToken: Arg.Any<CancellationToken>());
        await _roster.Repository.Received(1).SetScheduleForDatesAsync(6, Arg.Any<IEnumerable<DateOnly>>(), ScheduleType.Normal,
            9m, new TimeOnly(7, 0), Arg.Any<IEnumerable<(TimeOnly, TimeOnly, double?, double?)>?>(),
            cancellationToken: Arg.Any<CancellationToken>());
        Assert.False(_multiSelect.IsMultiSelectMode);
        _statusBar.Received(1).Show("Bulk assign complete", "Schedule applied to 2 employee(s) across 1 day(s).",
            StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task A_bulk_write_needs_someone_checked()
    {
        await ShowCruzAsync();
        _multiSelect.IsMultiSelectMode = true;
        Select(Sep1);

        await _vm.SetLeaveForSelectionCommand.Execute();

        Caution("No employees selected");
    }

    [Fact]
    public async Task Leave_is_set_without_a_dialog()
    {
        await ShowCruzAsync();
        Select(Sep1.AddDays(3));
        var shown = false;
        _dialog = _ => shown = true;

        await _vm.SetLeaveForSelectionCommand.Execute();

        Assert.False(shown);
        await _roster.Repository.Received(1).SetScheduleForDatesAsync(3, Arg.Any<IEnumerable<DateOnly>>(), ScheduleType.Leave,
            null, null, cancellationToken: Arg.Any<CancellationToken>());
        _statusBar.Received(1).Show(Arg.Any<string>(), "Set to Leave for 1 day(s).", StatusKind.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Removing_a_schedule_is_confirmed_first()
    {
        await ShowCruzAsync();
        Select(Sep1);

        _confirmAnswer = false;
        await _vm.ClearScheduleForSelectionCommand.Execute();
        await _roster.Repository.DidNotReceiveWithAnyArgs().ClearScheduleForDatesAsync(default, default!, default);

        _confirmAnswer = true;
        await _vm.ClearScheduleForSelectionCommand.Execute();
        await _roster.Repository.Received(1).ClearScheduleForDatesAsync(3, Arg.Any<IEnumerable<DateOnly>>(), Arg.Any<CancellationToken>());
        await _roster.Repository.Received(1).GetScheduleEntriesForEmployeeAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_write_is_reported_and_the_calendar_still_reloads()
    {
        await ShowCruzAsync();
        Select(Sep1);
        _roster.Repository.ClearScheduleForDatesAsync(3, Arg.Any<IEnumerable<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("locked")));

        await _vm.ClearScheduleForSelectionCommand.Execute();

        _statusBar.Received(1).Show("Could not remove the schedule", Arg.Any<string>(), StatusKind.Error, Arg.Any<TimeSpan>());
        await _roster.Repository.Received(1).GetScheduleEntriesForEmployeeAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marking_a_holiday_asks_its_name_and_keeps_the_day_selected()
    {
        await ShowCruzAsync();
        Select(Sep1.AddDays(6));
        _holidays.AddAsync(Arg.Any<Holiday>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _storedHolidays.Add(call.Arg<Holiday>());
            return Task.FromResult(call.Arg<Holiday>());
        });
        _dialog = dialog =>
        {
            var prompt = Assert.IsType<TextPromptViewModel>(dialog);
            prompt.Text = "Labor Day";
            return true;
        };
        var holiday = _roster.DataVersion.HolidayVersion;

        await _vm.ToggleHolidayForSelectionCommand.Execute();

        Assert.True(Day(Sep1.AddDays(6)).IsHoliday);
        Assert.Equal("Labor Day", Day(Sep1.AddDays(6)).HolidayName);
        Assert.True(Day(Sep1.AddDays(6)).IsSelected);
        Assert.Equal(holiday + 1, _roster.DataVersion.HolidayVersion);
    }

    [Fact]
    public async Task A_holiday_someone_else_just_marked_counts_as_done()
    {
        await ShowCruzAsync();
        Select(Sep1.AddDays(6));
        _holidays.AddAsync(Arg.Any<Holiday>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Holiday>(new DuplicateHolidayDateException(Sep1.AddDays(6), "Labor Day")));
        _dialog = _ => true;

        await _vm.ToggleHolidayForSelectionCommand.Execute();

        _statusBar.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<string>(), StatusKind.Error, Arg.Any<TimeSpan>());
        await _holidays.Received(2).ListAsync(Arg.Any<CancellationToken>());   // the selection load, then the refresh
    }

    [Fact]
    public async Task Removing_holidays_is_confirmed_and_spares_the_other_days()
    {
        _storedHolidays.Add(new Holiday { Id = 7, Date = Sep1.AddDays(6), Name = "Labor Day" });
        await ShowCruzAsync();
        Select(Sep1.AddDays(5), Sep1.AddDays(6));
        _holidays.DeleteAsync(7, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _storedHolidays.Clear();
            return Task.CompletedTask;
        });

        await _vm.ToggleHolidayForSelectionCommand.Execute();

        await _holidays.Received(1).DeleteAsync(7, Arg.Any<CancellationToken>());
        Assert.False(Day(Sep1.AddDays(6)).IsHoliday);
        Assert.Equal([Sep1.AddDays(5), Sep1.AddDays(6)], _calendar.GetSelectedDates());
    }

    [Fact]
    public async Task Several_non_holidays_cant_be_marked_at_once()
    {
        await ShowCruzAsync();
        Select(Sep1.AddDays(5), Sep1.AddDays(6));

        await _vm.ToggleHolidayForSelectionCommand.Execute();

        Caution("One day at a time");
    }

    [Fact]
    public async Task A_saved_pairing_recomputes_the_markers()
    {
        await ShowCruzAsync();
        _launcher.OpenAsync(Pin(3), Sep1, Arg.Any<PunchStatus?>(), _vm.ShowDialog,
                Arg.Any<CancellationToken>())
            .Returns(true);
        _runner.ClearReceivedCalls();
        _roster.DataVersion.BumpManualLogs();   // so the recompute can't come from cache

        await _vm.EditPunchPairingForDayCommand.Execute(Day(Sep1));

        await _runner.ReceivedWithAnyArgs(1).RunAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_cancelled_pairing_recomputes_nothing()
    {
        await ShowCruzAsync();
        _runner.ClearReceivedCalls();
        _roster.DataVersion.BumpManualLogs();

        await _vm.EditPunchPairingForDayCommand.Execute(Day(Sep1));

        await _launcher.Received(1).OpenAsync(Pin(3), Sep1, Arg.Any<PunchStatus?>(), _vm.ShowDialog, Arg.Any<CancellationToken>());
        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_manual_entry_from_a_tile_goes_through_the_attendance_editor()
    {
        await ShowCruzAsync();
        ReactiveViewModel? shown = null;
        _manualEntryEditor.ShowDialog.RegisterHandler(ctx =>
        {
            shown = ctx.Input;
            ctx.SetOutput(false);
        });

        await _vm.AddManualEntryForDayCommand.Execute(Day(Sep1));

        var entry = Assert.IsType<ManualLogEntryViewModel>(shown);
        Assert.Equal(Sep1.ToDateTime(TimeOnly.MinValue), entry.Date);
        Assert.False(_busy.IsRunning);
    }
}
