using System.Reactive.Linq;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

public class ManualLogEntryViewModelTests
{
    private static readonly DateOnly Day = new(2026, 9, 16);

    private readonly TestRoster _roster = new();
    private readonly IAttendanceLogRepository _logs = Substitute.For<IAttendanceLogRepository>();
    private Notice? _notice;

    public ManualLogEntryViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _logs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(
            [
                new AttendanceLog { EmployeeId = 3, Timestamp = Day.ToDateTime(new TimeOnly(17, 2)), PunchType = 1 },
                new AttendanceLog { EmployeeId = 3, Timestamp = Day.ToDateTime(new TimeOnly(7, 58)), PunchType = 0 },
                new AttendanceLog { EmployeeId = 4, Timestamp = Day.ToDateTime(new TimeOnly(8, 0)), PunchType = 0 },
            ]);
    }

    private ManualLogEntryViewModel NewViewModel(ManualAttendanceLog? existing = null, (Employee, DateOnly)? day = null)
    {
        var vm = new ManualLogEntryViewModel([.. _roster.Everyone], _logs, existing, day);
        vm.Notify.RegisterHandler(ctx =>
        {
            _notice = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        return vm;
    }

    [Fact]
    public void A_new_entry_starts_on_today_with_a_default_reason()
    {
        var vm = NewViewModel();

        Assert.Equal("Add Manual Entry", vm.Title);
        Assert.Equal("Add", vm.SaveText);
        Assert.False(vm.IsEmployeeAndDateLocked);
        Assert.Equal(DateTime.Today, vm.Date);
        Assert.Equal(Environment.UserName, vm.EnteredBy);
        Assert.Equal("Clock In", vm.Reason.Text);
        Assert.True(vm.Reason.IsDefault);
        Assert.Equal("Pick an employee to see that day's machine punches.", vm.MachinePunchesMessage);
    }

    [Fact]
    public void Typing_suggests_matching_employees_by_id_or_name()
    {
        var vm = NewViewModel();

        vm.EmployeeText = "dizon";
        Assert.Equal(["4 — " + TestRoster.Employee(4, "Dizon").DisplayName], vm.Suggestions);
        Assert.True(vm.IsSuggestionsOpen);

        vm.EmployeeText = "zzz";
        Assert.Empty(vm.Suggestions);
        Assert.False(vm.IsSuggestionsOpen);
    }

    [Fact]
    public async Task Picking_an_employee_shows_their_machine_punches_for_the_day()
    {
        var vm = NewViewModel();
        vm.Date = Day.ToDateTime(TimeOnly.MinValue);

        vm.EmployeeText = "Cruz";
        vm.PickSuggestion(vm.Suggestions[0]);
        await vm.WaitForMachinePunchesAsync();

        Assert.False(vm.IsSuggestionsOpen);
        Assert.Equal(3, vm.ResolvedEmployeeId);
        Assert.Equal(["07:58 AM — Clock In", "05:02 PM — Clock Out"], vm.MachinePunches);
        Assert.Null(vm.MachinePunchesMessage);
    }

    [Fact]
    public async Task Says_when_the_day_has_no_punches_or_the_fetch_failed()
    {
        var vm = NewViewModel(day: (TestRoster.Employee(6, "Flores"), Day));
        await vm.WaitForMachinePunchesAsync();
        Assert.Equal("No machine punches for this day.", vm.MachinePunchesMessage);

        _logs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("offline"));
        vm.Date = Day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        await vm.WaitForMachinePunchesAsync();
        Assert.Equal("Couldn't load machine punches for this day.", vm.MachinePunchesMessage);
    }

    [Fact]
    public async Task A_newer_request_drops_a_stale_one()
    {
        var first = new TaskCompletionSource<List<AttendanceLog>>();
        _logs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(first.Task, Task.FromResult(new List<AttendanceLog>()));
        var vm = NewViewModel(day: (TestRoster.Employee(3, "Cruz"), Day));

        vm.Date = Day.AddDays(1).ToDateTime(TimeOnly.MinValue);     // queued behind the first
        first.SetResult([new AttendanceLog { EmployeeId = 3, Timestamp = Day.ToDateTime(new TimeOnly(9, 0)) }]);
        await vm.WaitForMachinePunchesAsync();

        Assert.Empty(vm.MachinePunches);
        Assert.Equal("No machine punches for this day.", vm.MachinePunchesMessage);
    }

    [Fact]
    public void A_calendar_tile_locks_the_employee_and_date()
    {
        var vm = NewViewModel(day: (TestRoster.Employee(3, "Cruz"), Day));

        Assert.True(vm.IsEmployeeAndDateLocked);
        Assert.StartsWith("3 —", vm.EmployeeText);
        Assert.Equal(Day.ToDateTime(TimeOnly.MinValue), vm.Date);
        Assert.False(vm.IsSuggestionsOpen);
    }

    [Fact]
    public async Task Editing_prefills_and_saves_back_under_the_same_id()
    {
        var vm = NewViewModel(new ManualAttendanceLog
        {
            Id = 9, EmployeeId = 4, Timestamp = Day.ToDateTime(new TimeOnly(17, 30)), PunchType = 1, Reason = "", EnteredBy = "maria",
        });
        Assert.Equal("Edit Manual Entry", vm.Title);
        Assert.Equal("Save", vm.SaveText);
        Assert.Equal(new TimeOnly(17, 30), vm.Time);
        Assert.Equal("Clock Out", vm.Reason.Text);      // a blank saved reason shows the default

        vm.Reason.Text = "Forgot to badge out";
        vm.PunchType = 0;
        Assert.Equal("Forgot to badge out", vm.Reason.Text);     // typed text no longer follows

        Assert.True(await vm.AcceptCommand.Execute());
        Assert.Equal(9, vm.AcceptedLog!.Id);
        Assert.Equal(4, vm.AcceptedLog.EmployeeId);
        Assert.Equal(0, vm.AcceptedLog.PunchType);
        Assert.Equal("Forgot to badge out", vm.AcceptedLog.Reason);
        Assert.Equal("maria", vm.AcceptedLog.EnteredBy);
    }

    [Fact]
    public async Task Save_says_what_is_missing()
    {
        var vm = NewViewModel();

        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("Pick an employee from the list (start typing an Employee ID or name).", _notice?.Message);
        Assert.Equal(NoticeKind.Warning, _notice?.Kind);

        vm.EmployeeText = "3";
        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("Select a time.", _notice?.Message);
        Assert.Null(vm.AcceptedLog);

        vm.Time = new TimeOnly(8, 5);
        Assert.True(await vm.AcceptCommand.Execute());
        Assert.Equal(DateTime.Today.AddHours(8).AddMinutes(5), vm.AcceptedLog!.Timestamp);
        Assert.Equal("Clock In", vm.AcceptedLog.Reason);
    }
}

public class DefaultedTextTests
{
    public DefaultedTextTests() => ReactiveTestSetup.EnsureInitialized();

    [Fact]
    public void Tracks_the_default_until_typed_over_and_restores_it_when_emptied()
    {
        var label = "Time In";
        var text = new DefaultedText(() => label);
        Assert.Equal("Time In", text.Text);
        Assert.True(text.IsDefault);

        label = "Time Out";
        text.RefreshDefault();
        Assert.Equal("Time Out", text.Text);

        text.Text = "Late bus";
        Assert.False(text.IsDefault);
        label = "Time In";
        text.RefreshDefault();
        Assert.Equal("Late bus", text.Value);

        text.Text = "  ";
        text.FinishEditing();
        Assert.Equal("Time In", text.Text);
        Assert.True(text.IsDefault);
    }

    [Fact]
    public void Starts_from_a_saved_text()
    {
        var text = new DefaultedText(() => "Clock In", "Forgot");

        Assert.Equal("Forgot", text.Value);
        Assert.False(text.IsDefault);
    }
}

public class PunchTimeEntryViewModelTests
{
    public PunchTimeEntryViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    [Fact]
    public async Task Needs_a_time_and_defaults_the_reason_to_the_slot()
    {
        var vm = new PunchTimeEntryViewModel("Cruz, Juan", new DateOnly(2026, 9, 16), "Time In", initialTime: null);
        Assert.Equal("Add Manual Punch", vm.Title);
        Assert.Equal("Time In · Wednesday, September 16, 2026", vm.SlotText);

        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("Select a time.", vm.ErrorMessage);

        vm.Time = new TimeOnly(7, 55);
        Assert.True(await vm.AcceptCommand.Execute());
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new TimeOnly(7, 55), vm.AcceptedTime);
        Assert.Equal("Time In", vm.AcceptedReason);
        Assert.Equal(Environment.UserName, vm.AcceptedEnteredBy);
    }

    [Fact]
    public void Editing_keeps_the_saved_reason_and_who_entered_it()
    {
        var vm = new PunchTimeEntryViewModel("Cruz, Juan", new DateOnly(2026, 9, 16), "Time Out", new TimeOnly(17, 0),
            new ManualAttendanceLog { Reason = "Badge broke", EnteredBy = "maria" });

        Assert.Equal("Edit Manual Punch", vm.Title);
        Assert.Equal("Save", vm.SaveText);
        Assert.Equal("Badge broke", vm.Reason.Text);
        Assert.Equal("maria", vm.EnteredBy);
    }
}
