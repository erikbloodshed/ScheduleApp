using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;

namespace ScheduleApp.Attendance;

/// <summary>
/// Owns the ingest -> calculate pipeline for a single attendance run.
/// Deliberately has no dependency on WPF or any other UI concern -- it
/// reports progress via IProgress&lt;string&gt;, so the ViewModel can render it
/// however suits the UI.
///
/// IMPORTANT: AttendanceLog.EmployeeId and ScheduleEntry.EmployeeId are both the
/// punch clock's own employee code -- they match Employee.Pin directly, not
/// Employee.Id (the ScheduleApp database key) -- see either entity's own doc
/// comment. Matching a schedule entry to its punches is therefore just comparing
/// EmployeeId values directly (see logsByPin below), no join through Employee
/// required for that part. schedule.Employee, unlike AttendanceLog's own
/// EmployeeId, IS a real FK/navigation (see ScheduleEntry.Employee's own doc
/// comment) -- Employee.Pin being required and unique for every Employee row
/// (see that property's own doc comment) is what makes that possible, so
/// GetScheduleEntriesForPeriodAsync below Includes it directly rather than this
/// class needing its own separate employee fetch/attach step. Every
/// AttendanceSummary produced below carries that same Pin as its own EmployeeId
/// (see AttendanceSummary.EmployeeId) -- so the "Id" column in an Attendance
/// Summary report and the "Employee ID" column in Stored Punch Logs always agree
/// for the same person.
///
/// Manual entries (ManualAttendanceLog, from its own table) are folded into
/// the same punch pool as real device punches -- via
/// ManualAttendanceLog.ToAttendanceLog(), tagged AttendanceLogSource.Manual
/// -- before matching, rather than being handled as a separate step. Each
/// shift-calculation strategy still prefers a device punch over a manual one
/// wherever both could fill the same slot (see PunchMatching); a manual
/// entry only ever fills in where the device side has nothing.
/// </summary>
public class AttendanceWorkflowService(
    IScheduleRepository scheduleRepository,
    IAttendanceLogRepository attendanceLogRepository,
    IManualAttendanceLogRepository manualAttendanceLogRepository,
    IDayPunchPairingRepository dayPunchPairingRepository,
    AttendancePolicy policy)
{
    public async Task<AttendanceRunResult> RunAsync(
        DateOnly periodStart,
        DateOnly periodEnd,
        HashSet<int>? targetPins = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Ingesting data...");

        // targetPins narrows every fetch below to just the requested employees (via the
        // Pin joins GetScheduleEntriesForPeriodAsync/GetLogsAsync already need to perform
        // anyway), instead of pulling the whole company's roster/schedule/punches across
        // and filtering down to targetPins afterward the way this used to work -- see
        // GetEmployeesByPinsAsync's own doc comment. null (the "everyone" case -- a
        // full-company report, see ReportScopeViewModel's own doc comment on
        // GetSelectedPins) still takes the company-wide roster path below, since there's
        // no narrower roster to resolve instead.
        List<Employee> allEmployees;
        if (targetPins is null)
        {
            var departments = await scheduleRepository.GetDepartmentsWithEmployeesAsync(cancellationToken);
            var unassigned = await scheduleRepository.GetUnassignedEmployeesAsync(cancellationToken);
            allEmployees = departments.SelectMany(d => d.Employees).Concat(unassigned).ToList();
        }
        else
        {
            allEmployees = await scheduleRepository.GetEmployeesByPinsAsync(targetPins, cancellationToken);
        }

        // Punches are matched against a scheduled shift's TimeIn/TimeOut with buffer
        // windows either side (see SingleWindowShiftCalculationStrategy) -- a shift near
        // the edge of the period can need a punch just outside [periodStart, periodEnd],
        // and a shift crossing midnight has its TimeOut on the following calendar day.
        // Padding the query range by the widest configured buffer, plus a day for the
        // midnight case, keeps every punch a shift in this period could possibly match --
        // a real bound, not "every punch ever recorded" (which is what unfiltered
        // GetLogsAsync used to mean before this range existed). The same padded range is
        // used for manual entries below, since they need to be found by exactly the same
        // buffer windows a device punch would be.
        //
        // This policy-only figure is used purely to size the day-level padding on the
        // schedule-entries fetch just below -- day padding is Ceiling(hours/24)+1, which a
        // segment's own override (see FlexibleSegment.ClockInBufferHours/
        // ClockOutBufferHours) could only change if it were >= 24h, an unrealistic width
        // for a single punch-search window, so the policy value alone safely bounds that
        // fetch. It is NOT what the actual punch query range below is computed from --
        // see maxBufferHours further down, which folds in the real per-segment maximum
        // once schedule entries are in hand, since an individual segment can legitimately
        // widen past the policy default and an hour-range sized off the policy value alone
        // could silently drop a real punch near the edge of the period.
        var policyMaxBufferHours = new[]
        {
            policy.ClockInBufferBefore, policy.ClockInBufferAfter,
            policy.ClockOutBufferBefore, policy.ClockOutBufferAfter,
            policy.FlexibleSegmentClockInBuffer, policy.FlexibleSegmentClockOutBuffer
        }.Max();

        // A schedule entry dated just outside [periodStart, periodEnd] can still own a
        // buffer window (see above) that reaches into a punch inside the period -- e.g. an
        // overnight shift scheduled the day before periodStart, whose clock-out lands on
        // periodStart morning, or a shift scheduled the day after periodEnd whose clock-in
        // buffer reaches back into the night of periodEnd. The punch query below is padded
        // to fetch those punches; without padding this query the same way, the schedule
        // entry that should actually claim/consider them is never loaded, so they'd fall
        // out as Unscheduled even though a real schedule for that employee exists -- just
        // dated one day outside the strict period bounds. +1 day covers the
        // midnight-crossing case the same way the punch range's AddDays(1) does below; the
        // buffer-hours portion covers the rest. Entries pulled in only by this padding are
        // filtered back out of the visible report below (see the isInRequestedPeriod checks
        // in the loop) -- they exist here purely to let their buffer windows do their job.
        var scheduleDayPadding = (int)Math.Ceiling(policyMaxBufferHours / 24.0) + 1;
        var scheduleEntries = await scheduleRepository.GetScheduleEntriesForPeriodAsync(
            periodStart.AddDays(-scheduleDayPadding), periodEnd.AddDays(scheduleDayPadding), targetPins, cancellationToken);

        // The real bound the punch query range needs: whichever is wider between the
        // policy defaults and any per-segment override actually present among the
        // schedule entries just fetched (see FlexibleSegment.ClockInBufferHours/
        // ClockOutBufferHours) -- a segment left null still falls back to the policy
        // value it's compared against here, so it never narrows the bound below what
        // policyMaxBufferHours alone would have given.
        var segmentOverrideMaxBufferHours = scheduleEntries
            .SelectMany(s => s.FlexibleSegments)
            .SelectMany(seg => new[] { seg.ClockInBufferHours, seg.ClockOutBufferHours })
            .Where(b => b is not null)
            .Select(b => b!.Value)
            .DefaultIfEmpty(0)
            .Max();

        // Same idea as segmentOverrideMaxBufferHours above, but for Normal's own
        // buffers (see ScheduleEntry.ClockInBufferBeforeHours/.../
        // ClockOutBufferAfterHours, SingleWindowShiftCalculationStrategy) --
        // resolved per entry through the full three-tier day/employee/policy
        // cascade (see NormalBufferResolver), not just the entry's own raw
        // override fields, since an employee-level default (Employee.
        // ClockInBufferBeforeHours/.../ClockOutBufferAfterHours) can now widen a
        // day's effective buffer past the policy default too, even when that day
        // has no per-day override of its own. Folding the resolved value in this
        // way never narrows the bound below policyMaxBufferHours alone -- an
        // entry with nothing set at either tier still just resolves back to the
        // policy value it's compared against here.
        var normalOverrideMaxBufferHours = scheduleEntries
            .Select(s => NormalBufferResolver.Resolve(s, policy))
            .SelectMany(b => new[] { b.ClockInBefore, b.ClockInAfter, b.ClockOutBefore, b.ClockOutAfter })
            .DefaultIfEmpty(0)
            .Max();

        var maxBufferHours = new[] { policyMaxBufferHours, segmentOverrideMaxBufferHours, normalOverrideMaxBufferHours }.Max();

        var rangeStart = periodStart.ToDateTime(TimeOnly.MinValue).AddHours(-maxBufferHours);
        var rangeEnd = periodEnd.ToDateTime(TimeOnly.MaxValue).AddDays(1).AddHours(maxBufferHours);

        var deviceLogs = await attendanceLogRepository.GetLogsAsync(rangeStart, rangeEnd, targetPins, cancellationToken);
        var manualLogs = await manualAttendanceLogRepository.GetLogsAsync(rangeStart, rangeEnd, targetPins, cancellationToken);
        var rawLogs = deviceLogs.Concat(manualLogs.Select(m => m.ToAttendanceLog())).ToList();

        // Hand-edited punch pairings (see DayPunchPairing). Fetched over the
        // requested period rather than the padded range the punch query above
        // uses: an override is keyed by the schedule entry's own Date, and an
        // entry pulled in only by scheduleDayPadding never surfaces a summary row
        // anyway (see isInRequestedPeriod below), so a padded fetch here would
        // only ever load rows nothing consults. Only Flexible days can have one
        // (see AttendanceCalculator.CalculateShift's pairingOverride parameter);
        // the dictionary is keyed by (Pin, Date) to match the per-entry lookup.
        var dayPunchPairings = await dayPunchPairingRepository.GetForRangeAsync(
            periodStart, periodEnd, targetPins, cancellationToken);
        var pairingsByEmployeeDate = dayPunchPairings
            .ToDictionary(p => (p.EmployeeId, p.Date));

        progress?.Report("Calculating attendance...");

        var logsByPin = rawLogs
            .OrderBy(l => l.Timestamp)
            .GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var summaries = new List<AttendanceSummary>();

        // Per employee (keyed by pin), every punch that any of their
        // schedule entries this run either claimed (as a clock-in/out) or at
        // least considered but didn't pick (unclaimed -- see
        // ShiftCalculationResult). Anything left over once every schedule
        // entry has been processed is Unscheduled -- see below.
        // HashSet<AttendanceLog> relies on reference equality, which holds
        // here since the exact same AttendanceLog instances from
        // logsByPin flow through every strategy call within this one
        // run -- nothing clones or recreates them along the way.
        var consideredByEmployee = new Dictionary<int, HashSet<AttendanceLog>>();
        var orphanedPunches = new List<AttendanceLog>();

        foreach (var schedule in scheduleEntries)
        {
            var employee = schedule.Employee;

            // Defensive, not expected -- GetScheduleEntriesForPeriodAsync above always
            // Includes Employee, and the FK on ScheduleEntry.EmployeeId (see
            // ScheduleDbContext's own remarks on that entity) guarantees every row
            // resolves to a real one. Null here would mean this method started reading
            // scheduleEntries without that Include, not a genuinely un-matched employee --
            // there's no such state for a persisted ScheduleEntry to be in anymore.
            if (employee is null)
                continue;

            // scheduleEntries now includes entries dated just outside [periodStart,
            // periodEnd] (see scheduleDayPadding above), fetched solely so their buffer
            // windows can claim/consider punches near the edge of the period -- they
            // shouldn't surface a summary row for a day the user never asked about.
            var isInRequestedPeriod = schedule.Date >= periodStart && schedule.Date <= periodEnd;

            var pin = employee.Pin;
            if (targetPins is not null && !targetPins.Contains(pin))
                continue;

            var punches = logsByPin.TryGetValue(pin, out var found) ? found : [];

            // Null for every day without a hand-edited pairing, which is almost
            // all of them -- CalculateShift then behaves exactly as it always
            // has. The ScheduleType check isn't strictly required (CalculateShift
            // ignores an override for any non-Flexible type), but it keeps the
            // dictionary lookup off the hot path for the other five types.
            var pairingOverride = schedule.ScheduleType == ScheduleType.Flexible
                && pairingsByEmployeeDate.TryGetValue((pin, schedule.Date), out var savedPairing)
                    ? savedPairing
                    : null;

            var result = AttendanceCalculator.CalculateShift(schedule, punches, policy, pairingOverride);

            if (isInRequestedPeriod)
                summaries.AddRange(result.Summaries);

            if (!consideredByEmployee.TryGetValue(pin, out var considered))
            {
                considered = [];
                consideredByEmployee[pin] = considered;
            }
            foreach (var p in result.ClaimedPunches) considered.Add(p);
            foreach (var p in result.UnclaimedPunches) considered.Add(p);

            orphanedPunches.AddRange(result.UnclaimedPunches);
        }

        // Anything belonging to an in-scope employee that no schedule entry
        // this run ever claimed or even considered -- a punch on a day with
        // no schedule at all, or from a pin that doesn't match any
        // employee/schedule entry in the system whatsoever (e.g. a stale
        // device enrollment or a data-entry mistake on the terminal side).
        var unscheduledPunches = new List<AttendanceLog>();
        foreach (var (pin, punches) in logsByPin)
        {
            if (targetPins is not null && !targetPins.Contains(pin))
                continue;

            var considered = consideredByEmployee.TryGetValue(pin, out var c) ? c : [];
            unscheduledPunches.AddRange(punches.Where(p => !considered.Contains(p)));
        }

        progress?.Report("Done.");

        // rawLogs (used for matching above) was fetched over the padded range, which can
        // reach into the day(s) before/after the period the user actually asked for --
        // that's correct for catching a shift's buffer window near the edges, but the
        // raw punch-log export (and the "Total punches" count) should reflect only what
        // was actually requested, not the wider window fetched to support matching.
        var periodStartInclusive = periodStart.ToDateTime(TimeOnly.MinValue);
        var periodEndInclusive = periodEnd.ToDateTime(TimeOnly.MaxValue);
        var rawLogsInPeriod = rawLogs
            .Where(l => l.Timestamp >= periodStartInclusive && l.Timestamp <= periodEndInclusive)
            .ToList();

        // Same trim, same reason, for Orphaned/Unscheduled: both were computed
        // over the padded fetch range above, so a punch just outside
        // [periodStart, periodEnd] that only got pulled in to support
        // edge-of-period window matching shouldn't surface as a surprise
        // exception in a report for a period that never asked about it.
        // Distinct() on orphanedPunches guards against the same punch being
        // reported unclaimed by two different schedule entries (e.g. an
        // overnight shift's out-window and the next day's in-window both
        // reaching close enough to midnight to overlap) -- unscheduledPunches
        // can't have that problem, since it's built by iterating
        // logsByPin once per punch rather than accumulating per schedule
        // entry.
        var orphanedPunchesInPeriod = orphanedPunches
            .Where(l => l.Timestamp >= periodStartInclusive && l.Timestamp <= periodEndInclusive)
            .Distinct()
            .ToList();
        var unscheduledPunchesInPeriod = unscheduledPunches
            .Where(l => l.Timestamp >= periodStartInclusive && l.Timestamp <= periodEndInclusive)
            .ToList();

        return new AttendanceRunResult
        {
            Summaries = summaries,
            RawLogs = rawLogsInPeriod,
            Employees = allEmployees,
            OrphanedPunches = orphanedPunchesInPeriod,
            UnscheduledPunches = unscheduledPunchesInPeriod,
        };
    }
}