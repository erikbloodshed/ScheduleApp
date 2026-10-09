using System.Globalization;
using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Schedule;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class ScheduleCalendarViewModelTests : IDisposable
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);

    private readonly TestRoster _roster = new();
    private readonly IHolidayRepository _holidays = Substitute.For<IHolidayRepository>();
    private readonly IAttendanceRunner _runner = Substitute.For<IAttendanceRunner>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly ViewStateStore _viewState = new();
    private readonly MultiSelectModeState _multiSelect = new();
    private readonly AttendanceBusyState _busy;
    private readonly EmployeeTreeViewModel _tree;
    private readonly ScheduleCalendarViewModel _vm;
    private int _saves;

    public ScheduleCalendarViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _viewState.Schedule.DisplayedMonth = new DateTime(2026, 9, 1);
        _busy = new AttendanceBusyState(_statusBar);

        _roster.Repository.GetScheduleEntriesForEmployeeAsync(3, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ScheduleEntry>
            {
                Entry(Sep1, new TimeOnly(8, 0)),
                Entry(Sep1.AddDays(1), new TimeOnly(8, 0)),
                Entry(Sep1.AddDays(7), new TimeOnly(9, 0)),
            }));
        _roster.Repository.GetScheduleEntriesForEmployeeAsync(4, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ScheduleEntry>()));
        _holidays.ListAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Holiday> { new() { Date = new DateOnly(2026, 9, 21), Name = "Founders' Day" } }));
        _runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AttendanceRunResult
            {
                Summaries =
                [
                    Summary(Sep1, PunchStatus.Complete),
                    Summary(Sep1.AddDays(1), PunchStatus.Complete),
                    Summary(Sep1.AddDays(1), PunchStatus.Absent),
                    Summary(Sep1.AddDays(2), PunchStatus.Leave),
                    Summary(Sep1.AddDays(3), PunchStatus.RestDay, ScheduleType.RestDay, workedHours: 8),
                    Summary(Sep1.AddDays(4), PunchStatus.RestDay, ScheduleType.RestDay),
                    Summary(Sep1.AddDays(6), PunchStatus.Complete, clockInIsManual: true),
                ],
            }));

        _tree = new EmployeeTreeViewModel(_roster.Repository, _statusBar, _viewState, _roster.DataVersion, () => _saves++, _busy,
            new PayrollPolicy(), new AttendanceSettings());
        _vm = new ScheduleCalendarViewModel(_roster.Repository, _holidays, _statusBar, new AttendanceSettings(), _runner, _busy,
            _viewState, () => _saves++, _tree, _multiSelect, _roster.DataVersion);
    }

    public void Dispose() => _busy.Dispose();

    private static ScheduleEntry Entry(DateOnly date, TimeOnly timeIn) =>
        new() { Date = date, ScheduleType = ScheduleType.Normal, TimeIn = timeIn, WorkTimeHours = 8m };

    private static AttendanceSummary Summary(DateOnly date, PunchStatus status, ScheduleType type = ScheduleType.Normal,
        double workedHours = 0, bool clockInIsManual = false) =>
        new() { EmployeeId = 3, ShiftDate = date, Status = status, ScheduleType = type, WorkedHours = workedHours, ClockInIsManual = clockInIsManual };

    private Employee Pin(int pin) => _roster.Everyone.Single(e => e.Pin == pin);

    private CalendarDayViewModel Day(DateOnly date) => _vm.CalendarDays.Single(d => d.Date == date);

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    /// <summary>Selects Cruz (Pin 3) and waits for the schedule and markers to land.</summary>
    private async Task SelectCruzAsync()
    {
        _tree.SelectedEmployee = Pin(3);
        await Until.TrueAsync(() => Day(Sep1).Entry is not null && Day(Sep1).AttendanceStatus is not null && !_busy.IsRunning,
            "Cruz's schedule and markers");
    }

    [Fact]
    public void Opens_on_the_saved_month_with_a_blank_six_week_grid()
    {
        Assert.Equal(new DateTime(2026, 9, 1), _vm.DisplayedMonth);
        Assert.Equal(new DateTime(2026, 9, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture), _vm.DisplayedMonthText);
        Assert.Equal(42, _vm.CalendarDays.Count);
        Assert.Equal(new DateOnly(2026, 8, 30), _vm.CalendarDays[0].Date);
        Assert.Equal(DayOfWeek.Sunday, _vm.CalendarDays[0].Date.DayOfWeek);
        Assert.False(_vm.CalendarDays[0].IsCurrentMonth);
        Assert.True(Day(Sep1).IsCurrentMonth);
        Assert.All(_vm.CalendarDays, d => Assert.Null(d.Entry));
        Assert.Equal("Set Schedule for Selected Days", _vm.SetScheduleButtonText);
        Assert.False(CanExecute(_vm.RecalculateScheduleCommand));
        Assert.Equal("", _vm.RefreshOrCancelGlyph);
    }

    [Fact]
    public void Month_navigation_rebuilds_the_grid_and_saves_the_view()
    {
        var saves = _saves;

        ((ICommand)_vm.NextMonthCommand).Execute(null);
        Assert.Equal(new DateTime(2026, 10, 1), _vm.DisplayedMonth);
        Assert.Equal(new DateOnly(2026, 9, 27), _vm.CalendarDays[0].Date);
        Assert.Equal(new DateTime(2026, 10, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture), _vm.DisplayedMonthText);

        ((ICommand)_vm.PreviousMonthCommand).Execute(null);
        ((ICommand)_vm.PreviousMonthCommand).Execute(null);
        Assert.Equal(new DateTime(2026, 8, 1), _vm.DisplayedMonth);
        Assert.Equal(new DateOnly(2026, 7, 26), _vm.CalendarDays[0].Date);
        Assert.Equal(saves + 3, _saves);
    }

    [Fact]
    public async Task Selecting_an_employee_loads_their_schedule_holidays_and_markers()
    {
        await SelectCruzAsync();

        Assert.Equal(3, _vm.ScheduleEntries.Count);
        Assert.Equal(new TimeOnly(8, 0), Day(Sep1).Entry!.TimeIn);
        Assert.True(Day(new DateOnly(2026, 9, 21)).IsHoliday);
        Assert.Equal("Founders' Day", Day(new DateOnly(2026, 9, 21)).HolidayName);

        Assert.Equal(PunchStatus.Complete, Day(Sep1).AttendanceStatus);
        Assert.Equal(PunchStatus.Absent, Day(Sep1.AddDays(1)).AttendanceStatus);       // worst segment wins
        Assert.Null(Day(Sep1.AddDays(2)).AttendanceStatus);                             // Leave: no marker
        Assert.Equal(PunchStatus.Complete, Day(Sep1.AddDays(3)).AttendanceStatus);     // worked Rest Day
        Assert.Equal(PunchStatus.RestDay, Day(Sep1.AddDays(4)).AttendanceStatus);
        Assert.True(Day(Sep1.AddDays(6)).HasManualPunch);
        Assert.False(Day(Sep1).HasManualPunch);
        Assert.Null(Day(Sep1.AddDays(10)).AttendanceStatus);

        var request = (AttendanceRunRequest)_runner.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Equal(3, Assert.Single(request.TargetPins!));
        Assert.Equal(new DateOnly(2026, 8, 30), request.PeriodStart);
        Assert.Equal(new DateOnly(2026, 10, 10), request.PeriodEnd);
        Assert.True(CanExecute(_vm.RecalculateScheduleCommand));
    }

    [Fact]
    public async Task Revisiting_an_employee_serves_their_markers_from_cache_until_the_data_moves()
    {
        await SelectCruzAsync();
        _tree.SelectedEmployee = Pin(4);
        await Until.TrueAsync(() => Day(Sep1).Entry is null && !_busy.IsRunning, "Dizon's empty schedule");
        _runner.ClearReceivedCalls();

        await SelectCruzAsync();
        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default);

        _roster.DataVersion.BumpManualLogs();
        await _vm.RefreshCalendarAttendanceStatusesAsync();
        await _runner.ReceivedWithAnyArgs(1).RunAsync(default!, default!, default);
        Assert.Equal(PunchStatus.Complete, Day(Sep1).AttendanceStatus);
    }

    [Fact]
    public async Task A_selection_while_busy_waits_for_idle()
    {
        var gate = new TaskCompletionSource();
        var running = _busy.RunAsync(visibly: false, _ => gate.Task);
        Assert.True(_busy.IsRunning);

        _tree.SelectedEmployee = Pin(3);
        await _roster.Repository.DidNotReceive().GetScheduleEntriesForEmployeeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        gate.SetResult();
        await running;
        await Until.TrueAsync(() => Day(Sep1).Entry is not null && Day(Sep1).AttendanceStatus is not null && !_busy.IsRunning,
            "the deferred load");
        await _roster.Repository.Received(1).GetScheduleEntriesForEmployeeAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multi_select_mode_blanks_the_grid()
    {
        await SelectCruzAsync();

        _multiSelect.IsMultiSelectMode = true;
        Assert.False(CanExecute(_vm.RecalculateScheduleCommand));
        Assert.True(await _vm.RefreshScheduleForSelectedEmployeeAsync());

        Assert.Empty(_vm.ScheduleEntries);
        Assert.All(_vm.CalendarDays, d => Assert.Null(d.Entry));
        Assert.All(_vm.CalendarDays, d => Assert.Null(d.AttendanceStatus));
        Assert.True(Day(new DateOnly(2026, 9, 21)).IsHoliday);
    }

    [Fact]
    public async Task The_set_button_offers_to_edit_once_a_scheduled_day_is_selected()
    {
        await SelectCruzAsync();

        Day(Sep1.AddDays(10)).IsSelected = true;
        Assert.Equal("Set Schedule for Selected Days", _vm.SetScheduleButtonText);
        Day(Sep1).IsSelected = true;
        Assert.Equal("Edit Schedule for Selected Days", _vm.SetScheduleButtonText);

        Assert.Equal([Sep1, Sep1.AddDays(10)], _vm.GetSelectedDates());

        ((ICommand)_vm.ClearCalendarSelectionCommand).Execute(null);
        Assert.Empty(_vm.GetSelectedDates());
        Assert.Equal("Set Schedule for Selected Days", _vm.SetScheduleButtonText);

        _vm.SelectDates(new HashSet<DateOnly> { Sep1.AddDays(1) });
        Assert.Equal("Edit Schedule for Selected Days", _vm.SetScheduleButtonText);

        // A rebuilt grid starts unselected.
        ((ICommand)_vm.NextMonthCommand).Execute(null);
        Assert.Equal("Set Schedule for Selected Days", _vm.SetScheduleButtonText);
    }

    [Fact]
    public async Task Analyzes_whether_the_selection_shares_one_schedule()
    {
        await SelectCruzAsync();

        Assert.Equal((false, null), _vm.AnalyzeSelectionSchedule([Sep1.AddDays(10)]));
        Assert.Equal((true, null), _vm.AnalyzeSelectionSchedule([Sep1, Sep1.AddDays(10)]));
        Assert.Equal((true, null), _vm.AnalyzeSelectionSchedule([Sep1, Sep1.AddDays(7)]));

        var (hasAny, uniform) = _vm.AnalyzeSelectionSchedule([Sep1, Sep1.AddDays(1)]);
        Assert.True(hasAny);
        Assert.Equal(new TimeOnly(8, 0), uniform!.TimeIn);
    }

    [Fact]
    public void Groups_dates_into_contiguous_ranges()
    {
        var ranges = ScheduleCalendarViewModel.GroupIntoContiguousRanges(
            [Sep1, Sep1.AddDays(1), Sep1.AddDays(2), Sep1.AddDays(5), Sep1.AddDays(7), Sep1.AddDays(8)]);

        Assert.Equal([(Sep1, Sep1.AddDays(2)), (Sep1.AddDays(5), Sep1.AddDays(5)), (Sep1.AddDays(7), Sep1.AddDays(8))], ranges);
    }

    [Fact]
    public async Task Recalculate_reports_success_and_a_failed_load_is_reported()
    {
        await SelectCruzAsync();

        await _vm.RecalculateScheduleCommand.Execute();
        _statusBar.Received(1).Show(Arg.Any<string>(), "Schedule recalculated.", StatusKind.Success, Arg.Any<TimeSpan>());

        _roster.Repository.GetScheduleEntriesForEmployeeAsync(3, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<ScheduleEntry>>(new InvalidOperationException("offline")));
        Assert.False(await _vm.RefreshScheduleForSelectedEmployeeAsync(visibly: true));
        _statusBar.Received(1).Show("Schedule load failed", Arg.Any<string>(), StatusKind.Error, Arg.Any<TimeSpan>());
        Assert.Empty(_vm.ScheduleEntries);
    }

    [Fact]
    public async Task The_header_button_cancels_while_anything_visibly_runs()
    {
        var gate = new TaskCompletionSource();
        var cancelled = false;
        var running = _busy.RunAsync(visibly: true, async token =>
        {
            using (token.Register(() => cancelled = true))
                await gate.Task;
        });

        Assert.Equal("", _vm.RefreshOrCancelGlyph);
        Assert.Equal("Stop whatever's currently running.", _vm.RefreshOrCancelToolTip);
        Assert.True(CanExecute(_vm.RefreshOrCancelScheduleCommand));

        ((ICommand)_vm.RefreshOrCancelScheduleCommand).Execute(null);
        Assert.True(cancelled);

        gate.SetResult();
        await running;
        Assert.Equal("", _vm.RefreshOrCancelGlyph);
        Assert.False(CanExecute(_vm.RefreshOrCancelScheduleCommand));
    }

    [Fact]
    public async Task Holidays_reload_on_their_own()
    {
        await _vm.RefreshHolidaysAsync();

        Assert.True(Day(new DateOnly(2026, 9, 21)).IsHoliday);
        await _roster.Repository.DidNotReceive().GetScheduleEntriesForEmployeeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default);
    }
}
