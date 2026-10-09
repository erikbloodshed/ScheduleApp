using NSubstitute;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Tests;

/// <summary>A whole Schedule tab -- the MainViewModel and its children -- over stubbed
/// repositories and the test roster, opening on September 2026. Cruz (Pin 3) has a Normal day
/// on Sep 1 that the attendance run reads as Partial; Sep 21 is a holiday.</summary>
internal sealed class TestSchedule : IDisposable
{
    public static readonly DateOnly Sep1 = new(2026, 9, 1);

    public TestSchedule()
    {
        ReactiveTestSetup.EnsureInitialized();
        ViewState.Schedule.DisplayedMonth = new DateTime(2026, 9, 1);
        Busy = new AttendanceBusyState(StatusBar);

        Roster.Repository.GetDepartmentsWithEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Department> { Roster.Bakery, Roster.Kitchen }));
        Roster.Repository.GetUnassignedEmployeesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Roster.Unassigned));
        Roster.Repository.GetScheduleEntriesForEmployeeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<int>(0) == 3
                ? [new ScheduleEntry { Date = Sep1, ScheduleType = ScheduleType.Normal, TimeIn = new TimeOnly(8, 0), WorkTimeHours = 8m }]
                : new List<ScheduleEntry>()));
        Holidays.ListAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<Holiday> { new() { Id = 1, Date = new DateOnly(2026, 9, 21), Name = "Founders' Day" } }));
        Runner.RunAsync(Arg.Any<AttendanceRunRequest>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AttendanceRunResult
            {
                Summaries = [new AttendanceSummary { EmployeeId = 3, ShiftDate = Sep1, Status = PunchStatus.Partial }],
            }));

        var manualLogs = Substitute.For<IManualAttendanceLogRepository>();
        var directory = new AttendanceEmployeeDirectory(Roster.Repository);
        var manualEntries = new ManualEntriesViewModel(manualLogs, StatusBar, Busy, Roster.DataVersion, directory,
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 15), initialIsManualEntriesTabSelected: false, () => { },
            new AttendanceTabActivationGate { IsReady = true });
        var manualEntryEditor = new ManualEntryEditorViewModel(manualLogs, Substitute.For<IAttendanceLogRepository>(), StatusBar,
            Busy, Roster.DataVersion, directory, manualEntries);

        ViewModel = new MainViewModel(Roster.Repository, Holidays, StatusBar, ViewState, new AttendanceSettings(), Runner, Busy,
            Roster.DataVersion, new PayrollSettings(), manualEntryEditor, Roster.Provider, Launcher);
    }

    public TestRoster Roster { get; } = new();
    public IHolidayRepository Holidays { get; } = Substitute.For<IHolidayRepository>();
    public IAttendanceRunner Runner { get; } = Substitute.For<IAttendanceRunner>();
    public IStatusBarService StatusBar { get; } = Substitute.For<IStatusBarService>();
    public IDayPunchPairingEditorLauncher Launcher { get; } = Substitute.For<IDayPunchPairingEditorLauncher>();
    public ViewStateStore ViewState { get; } = new();
    public AttendanceBusyState Busy { get; }
    public MainViewModel ViewModel { get; }

    public Employee Pin(int pin) => Roster.Everyone.Single(e => e.Pin == pin);

    public CalendarDayViewModel Day(DateOnly date) => ViewModel.Calendar.CalendarDays.Single(d => d.Date == date);

    /// <summary>Waits for the selected employee's schedule and markers (or a blank calendar)
    /// to land.</summary>
    public Task SettledAsync() => Until.TrueAsync(() => !Busy.IsRunning, "the schedule tab to settle");

    public void Dispose() => Busy.Dispose();
}
