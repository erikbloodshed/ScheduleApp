using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Payroll;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// One employee's IAttendanceRunner + IPayrollAdjustmentRepository +
/// PayrollCalculator round trip, plus the SSS/PhilHealth/Pag-IBIG/Premium Pay/Allowance/Cash
/// Advance default-contribution seeding that goes with it -- extracted out of
/// PayrollViewModel.ComputeOneAsync (build-order
/// step 4) so PayrollWizardViewModel's own Step 3 (Group C) can reuse the exact same
/// computation over many employees without duplicating it, the same reason
/// PayrollPrintExportViewModel.PrintPayslipsAsync/ExportPayrollReportAsync already looped the private
/// method version of this. Registered Scoped (see App.xaml.cs) since it depends on
/// IAttendanceRunner/IPayrollAdjustmentRepository/IPayrollUndertimeWaiverRepository, which are
/// all themselves Scoped.
/// </summary>
public interface IPayrollComputationService
{
    /// <summary>Runs attendance, fetches/seeds this employee/period's adjustments, and hands
    /// both to PayrollCalculator. See PayrollComputationService.ComputeOneAsync's own doc
    /// comment for the full sequencing.</summary>
    Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeOneAsync(
        Employee employee, DateOnly start, DateOnly end, CancellationToken cancellationToken);

    /// <summary>The batch counterpart to ComputeOneAsync -- runs the IAttendanceRunner/
    /// IPayrollAdjustmentRepository/IPayrollUndertimeWaiverRepository round trips ONCE for
    /// every pin in <paramref name="pins"/>, instead of once per employee. See
    /// PayrollBatchContext's own doc comment for why that's a real fetch-count reduction and
    /// not just a restructuring: AttendanceWorkflowService.RunAsync accepts a multi-pin
    /// TargetPins set and scopes its underlying fetch to all of them in one round trip, so
    /// ComputeOneAsync looped N times was paying for N separate round trips where one,
    /// wider-scoped call does the same job. Callers that need payroll
    /// for more than one employee at once (PayrollGroupViewModel, PayrollPrintExportViewModel,
    /// PayrollWizardViewModel's Step 3) should call this once, then ComputeOneFromBatchAsync
    /// per employee against the result, rather than looping ComputeOneAsync.</summary>
    Task<PayrollBatchContext> PrepareBatchAsync(
        IReadOnlyCollection<int> pins, DateOnly start, DateOnly end, CancellationToken cancellationToken);

    /// <summary>Same per-employee result ComputeOneAsync returns (seeded/corrected
    /// adjustments, then PayrollCalculator.Calculate), but reading attendance/adjustments/
    /// waiver data out of a PayrollBatchContext already prepared for the whole batch instead
    /// of fetching them fresh for just this one employee. Still does this employee's own
    /// contribution seed-or-reseed write (see SeedOrReseedContributionsAsync) when
    /// <paramref name="batch"/>.ContributionsSeeded is false -- that's a real per-row database
    /// write, not something a batch fetch can front-load the way the three reads above were.
    /// A caller that ran the whole batch through SeedContributionsForBatchAsync first should
    /// pass that call's own returned context here instead -- ContributionsSeeded being true on
    /// it is what lets this skip straight to PayrollCalculator.Calculate with no further
    /// database writes at all.</summary>
    Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeOneFromBatchAsync(
        Employee employee, DateOnly start, DateOnly end, PayrollBatchContext batch, CancellationToken cancellationToken);

    /// <summary>Batch counterpart to SeedOrReseedContributionsAsync -- seeds/corrects every
    /// employee in <paramref name="employees"/>'s SSS/PhilHealth/Pag-IBIG/Premium Pay/
    /// Allowance/Cash Advance rows for this period the same way that method does for one
    /// employee, but decides every employee's changes first and writes the whole batch in at
    /// most two round trips total (IPayrollAdjustmentRepository.AddRangeAsync/UpdateRangeAsync)
    /// instead of up to a dozen per employee. Call this once, right after PrepareBatchAsync,
    /// and use its returned PayrollBatchContext (not <paramref name="batch"/> itself) for every
    /// following ComputeOneFromBatchAsync call in the same batch -- see
    /// PayrollBatchContext.ContributionsSeeded's own doc comment for what that context carries
    /// forward and why ComputeOneFromBatchAsync needs to see it. Employees with no Pin are
    /// skipped, same "nothing to compute for them" convention every other batch caller already
    /// follows. A no-op, cost-wise, when nothing in the batch actually needs seeding or
    /// correcting (the overwhelming majority of loads once a batch's contributions already
    /// match the current rule) -- returns <paramref name="batch"/> itself, unchanged, rather
    /// than paying for two empty repository calls.</summary>
    Task<PayrollBatchContext> SeedContributionsForBatchAsync(
        IReadOnlyCollection<Employee> employees, DateOnly start, DateOnly end,
        PayrollBatchContext batch, CancellationToken cancellationToken);

    /// <summary>ComputeOneAsync's own attendance-reuse counterpart -- for a caller that just
    /// wrote a PayrollAdjustment (add/edit/delete) or flipped the undertime waiver for the
    /// employee/period ComputeOneAsync most recently ran, and needs a fresh PayrollResult
    /// reflecting that write without paying for a second
    /// IAttendanceRunner.RunAsync round trip -- an adjustment write never changes attendance
    /// (schedule/punches), so re-running it is pure waste on this path. See
    /// PayrollComputationService.ComputeOneAsync's own doc comment for why reusing the
    /// cached result here is worth doing regardless of the fetch's own TargetPins scope.
    ///
    /// Reuses ComputeOneAsync's own last cached (Employee.Pin, start, end) -&gt;
    /// (AttendanceSummary[], HolidayDates) result when it's still an exact match for this
    /// call's own employee/period; adjustments and the undertime-waived flag are always
    /// re-fetched fresh (they're single-employee-scoped queries, cheap, and are exactly what
    /// the caller just wrote). On a cache miss -- no prior ComputeOneAsync call, or one for a
    /// different employee/period -- this falls back to ComputeOneAsync itself, so a caller
    /// can never get a stale or wrong result from calling this out of order; it only ever
    /// saves work when there's something valid to save it from.
    ///
    /// NOT a substitute for ComputeOneAsync on an employee- or period-selection change --
    /// those change what attendance actually is, so they need the real
    /// IAttendanceRunner.RunAsync round trip ComputeOneAsync itself runs.</summary>
    Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeOneAdjustmentsOnlyAsync(
        Employee employee, DateOnly start, DateOnly end, CancellationToken cancellationToken);

    /// <summary>The (Type, employee-level default, this-period target) triples
    /// SeedOrReseedContributionsAsync checks against -- exposed on the interface (rather than
    /// kept private to PayrollComputationService) so any future caller that needs this list
    /// for an Employee/period pair can build the exact same one ComputeOneAsync's own
    /// single-employee seeding uses internally, rather than keeping a copy that could drift
    /// apart. Six entries (SSS/PhilHealth/Pag-IBIG/PremiumHoliday/Allowance/CashAdvance) for
    /// an employee with Employee.QualifiesForPremiumPay, five (no PremiumHoliday) otherwise.
    /// See PayrollComputationService's own doc comment on this method for the
    /// SSS-vs-PhilHealth/Pag-IBIG cutoff rule, the QualifiesForPremiumPay gate, and for why
    /// Allowance/Cash Advance need neither.
    ///
    /// <paramref name="summaries"/>/<paramref name="holidayDates"/> (Holiday Pay plan, Phase 3)
    /// exist purely so PremiumHoliday's own TargetAmount can be the actual computed Holiday Pay
    /// figure for a period with a listed Holiday on it -- see
    /// PayrollComputationService.PremiumHolidayTargetAmount's own doc comment. Same "hand the
    /// whole thing through, this does its own filtering" shape PayrollCalculator.Calculate's own
    /// summaries/holidayDates parameters already have -- not pre-filtered to this employee/
    /// period by the caller.</summary>
    (PayrollAdjustmentType Type, decimal EmployeeDefault, decimal TargetAmount)[] ContributionDefaultsFor(
        Employee employee, DateOnly start, DateOnly end,
        IReadOnlyList<AttendanceSummary> summaries, IReadOnlyCollection<DateOnly>? holidayDates);

    /// <summary>The actual per-type seed-or-correct-or-leave-alone loop behind
    /// ComputeOneAsync's own single-employee seeding -- exposed on the interface for the same
    /// reason as ContributionDefaultsFor above. See PayrollComputationService's own doc
    /// comment on this method for the skip/create/correct/leave-alone rules.</summary>
    Task<(List<PayrollAdjustment> Added, List<PayrollAdjustment> Updated)> SeedOrReseedContributionsAsync(
        int pin, DateOnly start, DateOnly end,
        (PayrollAdjustmentType Type, decimal EmployeeDefault, decimal TargetAmount)[] contributionDefaults,
        IReadOnlyList<PayrollAdjustment> existingAdjustments, CancellationToken cancellationToken);
}
