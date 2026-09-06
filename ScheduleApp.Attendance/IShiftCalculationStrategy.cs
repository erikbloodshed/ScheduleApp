using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Attendance;

/// <summary>
/// One implementation per ScheduleType-driven calculation shape. Picking a
/// strategy from a lookup table (see AttendanceCalculator) instead of an
/// if/else chain means a future schedule type only needs a new class + one
/// dictionary entry, without touching the existing strategies -- Flexible
/// (see FlexibleShiftCalculationStrategy) was the first one added this way,
/// followed by SplitShift (see SplitShiftCalculationStrategy).
///
/// Returns a ShiftCalculationResult rather than a single AttendanceSummary
/// both because a SplitShift day produces one row per FlexibleSegment (each
/// evaluated as its own scheduled window) rather than one row for the whole
/// day -- every other case (Normal, Leave, Flexible) still yields exactly
/// one -- and because callers now also need to know which punches were
/// used/considered, for Orphaned/Unscheduled detection (see
/// ShiftCalculationResult).
/// </summary>
internal interface IShiftCalculationStrategy
{
    ShiftCalculationResult Calculate(
        ScheduleEntry schedule,
        List<AttendanceLog> employeePunches,
        AttendancePolicy policy);
}
