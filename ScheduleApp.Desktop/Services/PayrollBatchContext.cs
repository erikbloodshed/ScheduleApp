using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Desktop.Services;

/// <summary>Shared inputs for computing payroll across many employees in one pass -- built
/// once by IPayrollComputationService.PrepareBatchAsync, then handed to
/// ComputeOneFromBatchAsync once per employee. Exists because ComputeOneAsync's own
/// IAttendanceRunner.RunAsync/IPayrollAdjustmentRepository.GetForEmployeePeriodAsync/
/// IPayrollUndertimeWaiverRepository.IsWaivedAsync round trips were being paid once PER
/// EMPLOYEE by every batch caller (PayrollGroupViewModel.RefreshPayrollGroupRowsAsync/
/// AddEmployeesToGroupAsync, PayrollPrintExportViewModel.PrintPayslipsAsync/
/// ExportPayrollReportAsync, PayrollWizardViewModel's Step 3 review grid) -- looping
/// ComputeOneAsync N times doesn't cost N single-employee lookups the way it looks like it
/// should: AttendanceWorkflowService.RunAsync accepts TargetPins as a HashSet, so calling
/// it once with every pin in the batch scopes its roster/schedule-entries/punch-log fetches
/// to just those employees and already returns every one of their own rows in that same one
/// round trip -- no separate per-employee call needed, TargetPins just finally gets passed
/// more than one pin. See PrepareBatchAsync's own doc comment for the three round trips this
/// replaces -- a fourth, HolidayDates below, joined once IHolidayRepository.
/// ListDatesForPeriodAsync gave ComputeOneAsync a per-employee-period fetch of its own (Holiday
/// Pay plan, Phase 3); company-wide rather than per-employee, but the same "once per batch, not
/// once per employee in it" win applies to it too.</summary>
public sealed class PayrollBatchContext
{
    /// <summary>Every AttendanceSummary from the batch run, across every employee in the
    /// batch -- not pre-filtered per employee. Safe to pass straight into
    /// PayrollCalculator.Calculate for any one employee in the batch: that method already
    /// filters internally by EmployeeId + period (see its own doc comment: "passing a run's
    /// full multi-employee result straight through is fine").</summary>
    public required IReadOnlyList<AttendanceSummary> AllSummaries { get; init; }

    /// <summary>This batch's PayrollAdjustment rows, grouped by EmployeeId (Pin). Unlike
    /// AllSummaries, this MUST be pre-split before use, not handed to
    /// SeedOrReseedContributionsAsync as one mixed-employee list: that method matches an
    /// existing row by Type alone, on the assumption that the list it's given already
    /// belongs to a single employee -- a mixed batch would let it match a different
    /// employee's row of the same Type. A pin with no rows this period isn't a key in this
    /// dictionary at all -- callers should fall back to an empty list (see
    /// ComputeOneFromBatchAsync), not treat a missing key as an error.</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<PayrollAdjustment>> AdjustmentsByPin { get; init; }

    /// <summary>Employee IDs (Pins) whose Undertime deduction is waived this period -- from
    /// IPayrollUndertimeWaiverRepository.GetWaivedPinsAsync. A pin not in this set means not
    /// waived, same "absence means false" convention IsWaivedAsync's own AnyAsync already
    /// follows for a single employee.</summary>
    public required IReadOnlySet<int> WaivedPins { get; init; }

    /// <summary>Every listed Holiday date within this batch's period, from
    /// IHolidayRepository.ListDatesForPeriodAsync (Holiday Pay plan, Phase 3) -- fed straight
    /// into PayrollCalculator.Calculate's own holidayDates parameter for each employee in the
    /// batch, and into ContributionDefaultsFor's PremiumHoliday TargetAmount computation.
    /// Company-wide, unlike AllSummaries/AdjustmentsByPin/WaivedPins above -- there's no
    /// per-employee dimension to a holiday, so unlike those three this same list is handed to
    /// every employee in the batch unfiltered, with nothing to key off Pin for.</summary>
    public required IReadOnlyCollection<DateOnly> HolidayDates { get; init; }

    /// <summary>False on the context PrepareBatchAsync itself returns; true only on the
    /// context IPayrollComputationService.SeedContributionsForBatchAsync hands back once it's
    /// already seeded/corrected every employee in the batch's SSS/PhilHealth/Pag-IBIG/Premium
    /// Pay/Allowance/Cash Advance rows in bulk (see that method's own doc comment) and folded
    /// the result back into AdjustmentsByPin above. ComputeOneFromBatchAsync checks this
    /// before running its own per-employee seed-or-reseed pass (see that method's own doc
    /// comment) -- true means AdjustmentsByPin is already final for every employee in this
    /// batch, so there's nothing left to seed, correct, or write, just PayrollCalculator.
    /// Calculate to run against what's already here. Left false, never set true, on a context
    /// that skipped SeedContributionsForBatchAsync entirely -- a caller that does that still
    /// gets correct results, just paying ComputeOneFromBatchAsync's own per-employee seed pass
    /// (and its per-employee database writes) the way every batch caller used to before this
    /// existed.</summary>
    public bool ContributionsSeeded { get; init; }
}
