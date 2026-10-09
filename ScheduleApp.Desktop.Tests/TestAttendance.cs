using NSubstitute;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Tests;

/// <summary>A whole Attendance tab -- the AttendanceViewModel and its children -- over stubbed
/// repositories and the test roster, for the Attendance pages' view tests.</summary>
internal sealed class TestAttendance : IDisposable
{
    public TestAttendance()
    {
        ReactiveTestSetup.EnsureInitialized();
        Busy = new AttendanceBusyState(StatusBar);
        ViewModel = new AttendanceViewModel(
            Runner, Logs, ManualLogs, Roster.Repository, StatusBar, new AttendanceSettings(), new ViewStateStore(),
            Busy, Roster.DataVersion, Roster.Provider, PairingLauncher);
    }

    public TestRoster Roster { get; } = new();
    public IAttendanceRunner Runner { get; } = Substitute.For<IAttendanceRunner>();
    public IAttendanceLogRepository Logs { get; } = Substitute.For<IAttendanceLogRepository>();
    public IManualAttendanceLogRepository ManualLogs { get; } = Substitute.For<IManualAttendanceLogRepository>();
    public IStatusBarService StatusBar { get; } = Substitute.For<IStatusBarService>();
    public IDayPunchPairingEditorLauncher PairingLauncher { get; } = Substitute.For<IDayPunchPairingEditorLauncher>();
    public AttendanceBusyState Busy { get; }
    public AttendanceViewModel ViewModel { get; }

    public void Dispose()
    {
        ViewModel.Dispose();
        Busy.Dispose();
    }
}
