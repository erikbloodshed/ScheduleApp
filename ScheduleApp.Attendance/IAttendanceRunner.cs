using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Attendance;

/// <summary>
/// Bundles the inputs to a single attendance run. Introduced so
/// IAttendanceRunner.RunAsync doesn't grow another positional parameter every
/// time a new input is needed -- add a property here instead of changing the
/// method signature (and therefore every implementer and every caller) again.
/// </summary>
public sealed class AttendanceRunRequest
{
    public required AttendancePolicy Policy { get; init; }
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }

    /// <summary>Optional. When null, every scheduled employee (matched by Employee ID)
    /// is processed. When populated, only these Employee IDs (Pin values) are.</summary>
    public HashSet<int>? TargetPins { get; init; }
}

/// <summary>
/// Abstracts running the attendance workflow, so callers (the WPF ViewModel,
/// a future console harness, etc.) don't need to know about concrete
/// repository or workflow-service types. This is the seam that lets a caller
/// be unit-tested with a fake runner instead of a real database/.dat file.
/// </summary>
public interface IAttendanceRunner
{
    Task<AttendanceRunResult> RunAsync(AttendanceRunRequest request, IProgress<string> progress,
        CancellationToken cancellationToken = default);
}
