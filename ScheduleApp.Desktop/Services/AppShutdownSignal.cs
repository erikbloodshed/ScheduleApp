namespace ScheduleApp.Desktop.Services;

/// <summary>
/// One CancellationTokenSource for the app's entire run, cancelled from App.xaml.cs's
/// OnExit right before the DI scope/ScheduleDbContext get disposed. Registered as a
/// singleton (see App.xaml.cs) -- there's exactly one of these for the whole process,
/// same reasoning as ViewStateStore's own singleton registration.
///
/// This is the root of Phase 5 of the Attendance cancellation rollout: AttendanceBusyState
/// links each per-operation CancellationTokenSource it creates to this one's Token (see
/// AttendanceBusyState's constructor), so either an explicit Cancel click (AttendanceBusyState.
/// Cancel(), Phase 4) or the app actually closing can interrupt whatever's running. Without
/// this, closing the app mid-Load/Export/Generate-Reports/Import/Fetch would leave
/// AttendanceBusyState's own token sitting there uncancelled while _scope?.Dispose() tore
/// down the very ScheduleDbContext (and its underlying SqlConnection) that in-flight call was
/// still using -- SqlClient does honor a genuinely cancelled CancellationToken by sending SQL
/// Server an attention signal to actually stop the command, not just refusing to start a new
/// one, so cancelling here first gives that in-flight call a real, clean way to unwind
/// (OperationCanceledException, already caught quietly by every Core method -- see Phase 3)
/// instead of however a disposed-out-from-under-it DbContext happens to fail.
///
/// Originally wired into the Attendance tab only (see AttendanceViewModel's constructor)
/// -- the 5-phase cancellation plan this belongs to was scoped to Attendance's DB access
/// path specifically. MainViewModel now links its own AttendanceBusyState to this same
/// token too (see MainViewModel's constructor and RefreshScheduleForSelectedEmployeeAsync),
/// so the Schedule page's calendar refresh unwinds the same clean way on app shutdown.
/// PushListenerViewModel still doesn't consume this.
/// </summary>
public sealed class AppShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    /// <summary>Called once, from App.xaml.cs's OnExit. Safe to call more than once
    /// (CancellationTokenSource.Cancel() already tolerates that) -- not that anything
    /// in this app currently would.</summary>
    public void Cancel() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}