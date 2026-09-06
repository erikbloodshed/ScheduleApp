using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Payroll;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Services;

/// <inheritdoc cref="IPayrollComputationService" />
public class PayrollComputationService : IPayrollComputationService
{
    /// <summary>EnteredBy stamp used for SSS/PhilHealth/Pag-IBIG rows written by
    /// SeedOrReseedContributionsAsync below, and the exact marker that method's own
    /// "safe to overwrite" check looks for on a later visit -- see that method's doc
    /// comment. PayrollViewModel.SetSingleValueAsync re-stamps EnteredBy to
    /// Environment.UserName the moment a person actually commits a value (including an
    /// explicit 0) into one of these rows by hand, which is what keeps a hand-touched row's
    /// EnteredBy from still reading "Employee default" and being mistaken for one nobody's
    /// touched.</summary>
    private const string SeededEnteredBy = "Employee default";

    /// <summary>The (Employee.Pin, PeriodStart, PeriodEnd) ComputeOneAsync most recently ran
    /// its own company-wide IAttendanceRunner.RunAsync for, alongside the AttendanceSummary
    /// rows and holiday dates that run produced -- what ComputeOneAdjustmentsOnlyAsync below
    /// reuses instead of re-running that same fetch for an adjustment write that can't have
    /// changed attendance in the first place. A single slot, not a per-employee dictionary:
    /// this service is registered Scoped for the app's one session-lifetime IServiceScope
    /// (see this class's own class-level remarks and App.xaml.cs's registration comment), and
    /// PayrollSummaryViewModel -- the only caller ComputeOneAdjustmentsOnlyAsync exists for --
    /// only ever has one employee/period "in view" at a time, so there's never more than one
    /// entry worth keeping warm. Unconditionally overwritten at the end of every ComputeOneAsync
    /// call (see that method's own last few lines), which is what keeps this from ever serving
    /// a stale employee/period once the person picks a different one -- the very next
    /// full ComputeOneAsync load replaces it before any adjustment-only call could see the old
    /// value. PrepareBatchAsync/ComputeOneFromBatchAsync's own batch runs deliberately don't
    /// read or write this slot -- a batch's AllSummaries covers many employees at once, which
    /// isn't the same shape this single-employee slot is for, and there's no adjustment-only
    /// batch caller that would ever need it.</summary>
    private (int Pin, DateOnly Start, DateOnly End, IReadOnlyList<AttendanceSummary> Summaries,
        IReadOnlyCollection<DateOnly>? HolidayDates, int HolidayVersion)? _lastAttendanceRun;

    private readonly IAttendanceRunner _attendanceRunner;
    private readonly IPayrollAdjustmentRepository _adjustmentRepository;
    private readonly IPayrollUndertimeWaiverRepository _undertimeWaiverRepository;
    private readonly IHolidayRepository _holidayRepository;

    /// <summary>Only used to stamp _lastAttendanceRun with the HolidayVersion its cached
    /// HolidayDates were fetched at, so ComputeOneAdjustmentsOnlyAsync can tell a still-good
    /// cache entry from one whose holiday dates a Mark/Remove Holiday (calendar or
    /// ManageHolidaysDialog) has since invalidated -- see AttendanceDataVersion.HolidayVersion's
    /// own doc comment. This service never bumps any version itself.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    private readonly AttendancePolicy _attendancePolicy;
    private readonly PayrollPolicy _payrollPolicy;

    public PayrollComputationService(
        IAttendanceRunner attendanceRunner,
        IPayrollAdjustmentRepository adjustmentRepository,
        IPayrollUndertimeWaiverRepository undertimeWaiverRepository,
        IHolidayRepository holidayRepository,
        AttendanceDataVersion dataVersion,
        AttendanceSettings attendanceSettings,
        PayrollSettings payrollSettings)
    {
        _attendanceRunner = attendanceRunner;
        _adjustmentRepository = adjustmentRepository;
        _undertimeWaiverRepository = undertimeWaiverRepository;
        _holidayRepository = holidayRepository;
        _dataVersion = dataVersion;
        _attendancePolicy = attendanceSettings.Policy;
        _payrollPolicy = payrollSettings.Policy;
    }

    /// <summary>One employee's IAttendanceRunner + IPayrollAdjustmentRepository +
    /// PayrollCalculator round trip -- extracted out of PayrollViewModel so every
    /// single-employee caller (PayrollSummaryViewModel's own selection-driven load,
    /// AddEmployeeToGroupAsync's one-employee add) shares the exact same computation without
    /// duplicating it. Returns the AttendanceSummary rows alongside the PayrollResult since
    /// PayrollSummaryViewModel also needs them for AttendanceRows.
    ///
    /// NOT what a multi-employee caller should loop -- see PrepareBatchAsync/
    /// ComputeOneFromBatchAsync below for the batch counterpart. This method's own
    /// IAttendanceRunner.RunAsync call is scoped to just this one pin (see
    /// AttendanceWorkflowService.RunAsync's TargetPins handling), but it's still its own
    /// round trip -- calling this once per employee in a loop repeats that round trip once per
    /// employee instead of fetching every requested employee's schedule/punch data in one call
    /// the way PrepareBatchAsync's own multi-pin TargetPins does -- PayrollGroupViewModel/
    /// PayrollPrintExportViewModel/PayrollWizardViewModel all used to loop this and have since
    /// switched to the batch path instead.</summary>
    public async Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeOneAsync(
        Employee employee, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        int pin = employee.Pin;

        var request = new AttendanceRunRequest
        {
            Policy = _attendancePolicy,
            PeriodStart = start,
            PeriodEnd = end,
            TargetPins = [pin],
        };

        // No IProgress<string> feedback wired up (unlike ReportViewModel's own status-bar
        // progress messages) -- a single-employee run is fast enough that a step-by-step
        // "Matching punches…" pulse would just flicker past, and the plain IsBusy progress
        // bar already tells the person something's happening.
        var runResult = await _attendanceRunner.RunAsync(request, new Progress<string>(), cancellationToken);
        IReadOnlyList<PayrollAdjustment> adjustments =
            await _adjustmentRepository.GetForEmployeePeriodAsync(pin, start, end, cancellationToken);
        bool undertimeWaived = await _undertimeWaiverRepository.IsWaivedAsync(pin, start, end, cancellationToken);

        // Holidays are company-wide (see Holiday's own doc comment), not employee-specific,
        // so there's nothing to scope this fetch to beyond the period itself -- same "hand the
        // whole thing through" shape the other three round trips above already have. One extra
        // round trip per single-employee call, same reasoning PrepareBatchAsync below applies
        // once per batch instead of once per employee in it.
        var holidayDates = await _holidayRepository.ListDatesForPeriodAsync(start, end, cancellationToken);

        // Records this employee/period's just-fetched attendance for
        // ComputeOneAdjustmentsOnlyAsync to reuse -- see _lastAttendanceRun's own doc comment
        // for why a single slot is enough and why this unconditional overwrite is what keeps
        // it from ever serving a stale employee/period. Set here, before ComputeCoreAsync
        // runs, so it's already in place for a caller that turns around and calls
        // ComputeOneAdjustmentsOnlyAsync immediately after this same ComputeOneAsync call
        // returns.
        _lastAttendanceRun = (pin, start, end, runResult.Summaries, holidayDates, _dataVersion.HolidayVersion);

        // runResult.Summaries is already scoped to just this one pin (TargetPins above is a
        // single-element set), so it's already the exact same shape ComputeCoreAsync's own
        // per-employee filter would produce from a wider batch -- see that method's doc
        // comment. skipSeeding: false -- this is a single-employee call, never part of a batch
        // that SeedContributionsForBatchAsync has already seeded.
        return await ComputeCoreAsync(
            employee, start, end, runResult.Summaries, adjustments, undertimeWaived, holidayDates,
            skipSeeding: false, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>See the interface's own doc comment for the full reasoning. On a cache hit,
    /// this skips straight to the same three single-employee-scoped reads ComputeOneAsync
    /// itself does for adjustments/undertimeWaived (everything except the
    /// IAttendanceRunner.RunAsync/holidayDates round trips, which _lastAttendanceRun already
    /// covers), then the same ComputeCoreAsync tail every other Compute* method here
    /// shares.</remarks>
    public async Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeOneAdjustmentsOnlyAsync(
        Employee employee, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        int pin = employee.Pin;

        // Cache miss (no prior ComputeOneAsync call at all, one for a different
        // employee/period than this call's own, or one whose cached holiday dates a
        // Mark/Remove Holiday has since invalidated -- HolidayVersion moved) -- fall back to
        // the real, full path rather than guess at attendance data that isn't actually on
        // hand. Also what (re-)populates _lastAttendanceRun for next time, so this is
        // self-healing: at most one call per employee/period/holiday-version ever takes the
        // slow path.
        if (_lastAttendanceRun is not { } cached || cached.Pin != pin || cached.Start != start || cached.End != end
            || cached.HolidayVersion != _dataVersion.HolidayVersion)
            return await ComputeOneAsync(employee, start, end, cancellationToken);

        IReadOnlyList<PayrollAdjustment> adjustments =
            await _adjustmentRepository.GetForEmployeePeriodAsync(pin, start, end, cancellationToken);
        bool undertimeWaived = await _undertimeWaiverRepository.IsWaivedAsync(pin, start, end, cancellationToken);

        return await ComputeCoreAsync(
            employee, start, end, cached.Summaries, adjustments, undertimeWaived, cached.HolidayDates,
            skipSeeding: false, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Runs the same three round trips ComputeOneAsync does, plus a fourth
    /// (holidayDates -- see PayrollBatchContext.HolidayDates' own doc comment for why it
    /// joined the other three), just once for the whole batch instead of once per pin -- see
    /// PayrollBatchContext's own doc comment for why that's a real reduction (one
    /// IAttendanceRunner.RunAsync call scoped to every pin in the batch via TargetPins'
    /// multi-pin HashSet, instead of N separate round trips each scoped to a single pin;
    /// ComputeOneAsync just never passed TargetPins more than one).
    ///
    /// Adjustments come back from GetForEmployeesPeriodAsync as one flat list across every
    /// employee in the batch (ordered by EmployeeId per that method's own doc comment) --
    /// grouped into a per-pin dictionary here, once, so ComputeOneFromBatchAsync can hand
    /// SeedOrReseedContributionsAsync a single employee's own rows the way it requires (see
    /// PayrollBatchContext.AdjustmentsByPin's own doc comment for why a mixed list would be
    /// wrong there), rather than re-filtering the flat list per employee on every
    /// call.</remarks>
    public async Task<PayrollBatchContext> PrepareBatchAsync(
        IReadOnlyCollection<int> pins, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var request = new AttendanceRunRequest
        {
            Policy = _attendancePolicy,
            PeriodStart = start,
            PeriodEnd = end,
            TargetPins = [.. pins],
        };

        var runResult = await _attendanceRunner.RunAsync(request, new Progress<string>(), cancellationToken);
        var adjustments = await _adjustmentRepository.GetForEmployeesPeriodAsync(pins, start, end, cancellationToken);
        var waivedPins = await _undertimeWaiverRepository.GetWaivedPinsAsync(pins, start, end, cancellationToken);

        // Holidays are company-wide (see Holiday's own doc comment), not employee-specific --
        // unlike AllSummaries/AdjustmentsByPin/WaivedPins above, there's no per-pin dimension
        // to this fetch at all, so it's the same single call regardless of how many pins are
        // in the batch. Still one fetch per batch rather than one per employee in it, same
        // "don't repeat a company-wide fetch N times" reasoning this method's own doc comment
        // gives for the other three round trips.
        var holidayDates = await _holidayRepository.ListDatesForPeriodAsync(start, end, cancellationToken);

        // Each group is cast to IReadOnlyList<PayrollAdjustment> right here in the selector --
        // unlike IReadOnlyList<T>/IReadOnlyCollection<T>, IReadOnlyDictionary<TKey, TValue> is
        // NOT covariant in TValue (no "out" on TValue in its own declaration), so a
        // Dictionary<int, List<PayrollAdjustment>> can't be implicitly assigned into
        // PayrollBatchContext.AdjustmentsByPin's IReadOnlyDictionary<int,
        // IReadOnlyList<PayrollAdjustment>> below -- building the dictionary with the target
        // value type from the start avoids that mismatch without a second copy.
        Dictionary<int, IReadOnlyList<PayrollAdjustment>> adjustmentsByPin = adjustments
            .GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, IReadOnlyList<PayrollAdjustment> (g) => g.ToList());

        return new PayrollBatchContext
        {
            AllSummaries = runResult.Summaries,
            AdjustmentsByPin = adjustmentsByPin,
            WaivedPins = waivedPins,
            HolidayDates = holidayDates,
        };
    }

    /// <inheritdoc />
    public Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeOneFromBatchAsync(
        Employee employee, DateOnly start, DateOnly end, PayrollBatchContext batch, CancellationToken cancellationToken)
    {
        int pin = employee.Pin;
        var adjustments = batch.AdjustmentsByPin.GetValueOrDefault(pin, []);
        bool undertimeWaived = batch.WaivedPins.Contains(pin);

        // batch.ContributionsSeeded true (SeedContributionsForBatchAsync already ran for this
        // batch) means adjustments above is already final -- nothing left for ComputeCoreAsync
        // to seed or correct, see that parameter's own doc comment.
        return ComputeCoreAsync(
            employee, start, end, batch.AllSummaries, adjustments, undertimeWaived, batch.HolidayDates,
            skipSeeding: batch.ContributionsSeeded, cancellationToken);
    }

    /// <summary>The seed-then-calculate tail shared by ComputeOneAsync and
    /// ComputeOneFromBatchAsync, once each has its own employee's attendance/adjustments/
    /// waiver state in hand (fetched fresh for one employee in the first case, sliced out of
    /// a PayrollBatchContext in the second) -- see either caller's own doc comment for where
    /// its inputs come from. <paramref name="allSummaries"/> may cover more than just this
    /// employee (a full batch run's worth); PayrollCalculator.Calculate filters it down to
    /// this employee/period internally (see that method's own doc comment), so there's
    /// nothing to pre-filter here for correctness. The returned Summaries tuple member IS
    /// filtered down to this employee, though -- callers (e.g. PayrollViewModel's own
    /// AttendanceRows) expect just their one employee's rows, the same shape ComputeOneAsync
    /// always returned back when its own runResult.Summaries was already single-employee by
    /// construction.</summary>
    private async Task<(PayrollResult Result, IReadOnlyList<AttendanceSummary> Summaries)> ComputeCoreAsync(
        Employee employee, DateOnly start, DateOnly end,
        IReadOnlyList<AttendanceSummary> allSummaries, IReadOnlyList<PayrollAdjustment> adjustments,
        bool undertimeWaived, IReadOnlyCollection<DateOnly>? holidayDates, bool skipSeeding,
        CancellationToken cancellationToken)
    {
        int pin = employee.Pin;

        // skipSeeding is true only when a batch caller already ran SeedContributionsForBatchAsync
        // for the whole batch this employee is part of -- adjustments is already final in that
        // case (see ComputeOneFromBatchAsync's own comment on why), so re-running the
        // single-employee seed pass here would be pure waste: same decisions, same "nothing
        // needs touching" outcome, just paid for a second time. ComputeOneAsync/
        // ComputeOneAdjustmentsOnlyAsync always pass false -- neither has a batch-wide seed
        // pass to have already run.
        if (!skipSeeding)
        {
            adjustments = await SeedDefaultContributionsAsync(
                employee, pin, start, end, allSummaries, holidayDates, adjustments, cancellationToken);
        }

        var result = PayrollCalculator.Calculate(
            employee, _payrollPolicy, allSummaries, adjustments, start, end, undertimeWaived, holidayDates);

        IReadOnlyList<AttendanceSummary> employeeSummaries = [.. allSummaries.Where(s => s.EmployeeId == pin)];
        return (result, employeeSummaries);
    }

    /// <summary>Fills in, or corrects, this employee/period's SSS/PhilHealth/Pag-IBIG/Premium
    /// Pay/Allowance/Cash Advance single-value rows from Employee.DefaultSss/DefaultPhilHealth/
    /// DefaultPagIbig/DefaultPremiumPay/DefaultAllowance/DefaultCashAdvance every time this
    /// employee/period combination is computed -- same "pre-fill, then it's a completely
    /// ordinary editable/persisted row from then on" spirit as Employee.DefaultLeaveIsPaid
    /// pre-selecting a brand-new Leave day's Paid/Unpaid radio (see that property's own doc
    /// comment), just committed here at period-load time instead of day-save time, since a
    /// payroll period has no separate "create" step the way a scheduled day does. Runs on
    /// every load now, not just the first -- see SeedOrReseedContributionsAsync below for the
    /// actual create-vs-correct-vs-leave-alone rule, which is what keeps this from clobbering
    /// a row a person has actually typed a number into.
    ///
    /// Called from ComputeOneAsync, right after the period's adjustments are fetched and
    /// before PayrollCalculator.Calculate runs, so a freshly-seeded or freshly-corrected row
    /// is reflected in the very same PayrollResult the person is about to see rather than only
    /// appearing after a second reload. Returns the possibly-updated adjustments list: any row
    /// SeedOrReseedContributionsAsync corrected is swapped in by Id, and any brand-new row is
    /// appended; the original list is returned unchanged (not copied) when nothing needed
    /// touching, which is the overwhelming majority of calls once an employee/period
    /// combination's contributions already match the current rule.
    ///
    /// <paramref name="allSummaries"/>/<paramref name="holidayDates"/> (Holiday Pay plan,
    /// Phase 3) exist purely to hand through to ContributionDefaultsFor below, which needs
    /// them to compute the PremiumHoliday row's TargetAmount -- see that method's own doc
    /// comment. Same "may cover more than just this employee, ContributionDefaultsFor doesn't
    /// need it pre-filtered" shape ComputeCoreAsync's own allSummaries parameter already
    /// has.</summary>
    private async Task<IReadOnlyList<PayrollAdjustment>> SeedDefaultContributionsAsync(
        Employee employee, int pin, DateOnly start, DateOnly end,
        IReadOnlyList<AttendanceSummary> allSummaries, IReadOnlyCollection<DateOnly>? holidayDates,
        IReadOnlyList<PayrollAdjustment> adjustments, CancellationToken cancellationToken)
    {
        var (added, updated) = await SeedOrReseedContributionsAsync(
            pin, start, end, ContributionDefaultsFor(employee, start, end, allSummaries, holidayDates),
            adjustments, cancellationToken);

        if (added.Count == 0 && updated.Count == 0) return adjustments;

        var updatedById = updated.ToDictionary(a => a.Id);
        List<PayrollAdjustment> merged =
            [.. adjustments.Select(a => updatedById.GetValueOrDefault(a.Id, a)), .. added];
        return merged;
    }

    /// <inheritdoc />
    /// <remarks>For SSS/PhilHealth/Pag-IBIG, TargetAmount is EmployeeDefault on this type's
    /// own withholding cutoff and 0.00 on the other -- SSS/PhilHealth/Pag-IBIG are each
    /// withheld once a month, but not on the same cutoff: SSS falls on whichever half covers
    /// month-end (see IncludesEndOfMonth), while PhilHealth and Pag-IBIG fall on whichever
    /// half covers the 1st (see IncludesStartOfMonth) -- so for a normal semi-monthly
    /// 1-15/16-end split, SSS is withheld on the 16-end cutoff and PhilHealth/Pag-IBIG on the
    /// 1-15 cutoff. EmployeeDefault is carried through separately (rather than only exposing
    /// TargetAmount) because it's also what decides whether this employee has this
    /// contribution configured at all -- see the "EmployeeDefault &lt;= 0" skip in
    /// SeedOrReseedContributionsAsync below, which stays keyed on the actual default, not on
    /// TargetAmount: a genuinely SSS-enrolled employee's TargetAmount is legitimately 0.00 on
    /// its non-withholding period, and that's not the same thing as "not enrolled".
    ///
    /// Premium Pay/Allowance/Cash Advance have no withholding cutoff at all -- they're every-
    /// period pay lines, not a once-a-month deduction -- so TargetAmount is just EmployeeDefault,
    /// unconditionally. SeedOrReseedContributionsAsync's own BlankAmountMeansZero fallback (see
    /// that method's doc comment) is what actually gives these three an explicit 0.00 row even
    /// when EmployeeDefault itself is 0, so nothing period-dependent needs to happen here for
    /// them the way it does for the statutory three.
    ///
    /// Premium Pay is additionally gated on Employee.QualifiesForPremiumPay: an ineligible
    /// employee gets no PremiumHoliday entry in the returned array at all (not a 0.00-amount
    /// one), so it never reaches SeedOrReseedContributionsAsync's loop below. Allowance/Cash
    /// Advance carry no such gate.
    ///
    /// PremiumHoliday's own TargetAmount (Holiday Pay plan, Phase 3) is the one exception to
    /// "Premium Pay/Allowance/Cash Advance have no withholding cutoff... TargetAmount is just
    /// EmployeeDefault, unconditionally" above -- see PremiumHolidayTargetAmount below for why
    /// and how it's actually computed. EmployeeDefault itself is untouched either way: still
    /// employee.DefaultPremiumPay, unconditionally, same as Allowance/CashAdvance -- so the
    /// "EmployeeDefault &lt;= 0" skip and the BlankAmountMeansZero 0.00-row fallback in
    /// SeedOrReseedContributionsAsync both still key off exactly the value they always
    /// have.</remarks>
    public (PayrollAdjustmentType Type, decimal EmployeeDefault, decimal TargetAmount)[] ContributionDefaultsFor(
        Employee employee, DateOnly start, DateOnly end,
        IReadOnlyList<AttendanceSummary> summaries, IReadOnlyCollection<DateOnly>? holidayDates)
    {
        bool includesEndOfMonth = IncludesEndOfMonth(start, end);
        bool includesStartOfMonth = IncludesStartOfMonth(start, end);

        (PayrollAdjustmentType, decimal, decimal) Row(PayrollAdjustmentType type, decimal employeeDefault, bool isWithholdingPeriod) =>
            (type, employeeDefault, isWithholdingPeriod ? employeeDefault : 0m);

        (PayrollAdjustmentType, decimal, decimal) EveryPeriodRow(PayrollAdjustmentType type, decimal employeeDefault) =>
            (type, employeeDefault, employeeDefault);

        // PremiumHoliday is the one row here gated on an eligibility flag rather than
        // period math (see Row/IncludesEndOfMonth/IncludesStartOfMonth above) -- an
        // ineligible employee gets no entry at all, not a zero-amount one, so
        // SeedOrReseedContributionsAsync never adds or reseeds a PremiumHoliday row for
        // them going forward. Mirrors PayrollCalculator.Calculate's own
        // QualifiesForPremiumPay gate on the display side (Phase 4) -- neither one
        // deletes a PremiumHoliday row that already exists from before the flag was
        // turned off; this one just stops creating new ones.
        List<(PayrollAdjustmentType, decimal, decimal)> defaults =
        [
            Row(PayrollAdjustmentType.SSS, employee.DefaultSss, includesEndOfMonth),
            Row(PayrollAdjustmentType.PhilHealth, employee.DefaultPhilHealth, includesStartOfMonth),
            Row(PayrollAdjustmentType.PagIbig, employee.DefaultPagIbig, includesStartOfMonth),
        ];

        if (employee.QualifiesForPremiumPay)
        {
            defaults.Add((
                PayrollAdjustmentType.PremiumHoliday,
                employee.DefaultPremiumPay,
                PremiumHolidayTargetAmount(employee, start, end, summaries, holidayDates)));
        }

        defaults.Add(EveryPeriodRow(PayrollAdjustmentType.Allowance, employee.DefaultAllowance));
        defaults.Add(EveryPeriodRow(PayrollAdjustmentType.CashAdvance, employee.DefaultCashAdvance));

        return [.. defaults];
    }

    /// <summary>The PremiumHoliday row's TargetAmount (Holiday Pay plan, Phase 3) -- the
    /// actual computed Holiday Pay figure (day component plus matching Overtime/Night Diff
    /// "copies"; see PayrollCalculator.CalculateHolidayPay, which this delegates to) for any
    /// period that genuinely has a listed Holiday date somewhere in it, falling back to the
    /// plain employee.DefaultPremiumPay -- this row's pre-Phase-3 TargetAmount, unconditionally
    /// -- for a period with no Holiday date at all. That fallback isn't just "the general
    /// case's TargetAmount happens to equal EmployeeDefault too" the way Allowance/CashAdvance's
    /// EveryPeriodRow shape works -- CalculateHolidayPay would itself already answer 0.00 for a
    /// no-holidays period, so this is a deliberate override back to the pre-existing figure
    /// instead, which is what keeps a plain, no-holiday-on-file period's Premium Pay row reading
    /// exactly as it always has (e.g. a flat manually-configured allowance-like amount that
    /// predates this feature and has nothing to do with an actual holiday) rather than silently
    /// zeroing out the moment this feature shipped.
    ///
    /// holidayDates is what decides which of the two applies -- Count: > 0 rather than
    /// re-checking CalculateHolidayPay's own answer, since a genuinely-listed Holiday date this
    /// employee simply didn't work (HolidayWorkedDays == 0) is still "something to compute", just
    /// one that happens to compute to 0.00 -- an honest answer, not a reason to fall back.
    /// employee/start/end/summaries pass straight through to CalculateHolidayPay, which does its
    /// own employee/period filtering of summaries exactly like PayrollCalculator.Calculate itself
    /// does -- nothing to pre-narrow here.</summary>
    private decimal PremiumHolidayTargetAmount(
        Employee employee, DateOnly start, DateOnly end,
        IReadOnlyList<AttendanceSummary> summaries, IReadOnlyCollection<DateOnly>? holidayDates)
    {
        if (holidayDates is not { Count: > 0 })
        {
            return employee.DefaultPremiumPay;
        }

        var (holidayPayAmount, _) =
            PayrollCalculator.CalculateHolidayPay(employee, _payrollPolicy, summaries, start, end, holidayDates);
        return holidayPayAmount;
    }

    /// <summary>True if [start, end] covers the last calendar day of any month it touches.
    /// A normal ScheduleApp payroll period never spans more than two different months (a
    /// semi-monthly cutoff crossing from one month into the next, at most) -- so checking
    /// start's month-end and end's month-end between them covers every real period; a period
    /// that starts and ends within the same month just checks the same date twice. Used by
    /// ContributionDefaultsFor above to decide whether this period is the one SSS gets
    /// withheld on.</summary>
    private static bool IncludesEndOfMonth(DateOnly start, DateOnly end) =>
        IsMonthEndWithin(start, end, start) || IsMonthEndWithin(start, end, end);

    private static bool IsMonthEndWithin(DateOnly start, DateOnly end, DateOnly monthOf)
    {
        var lastDayOfMonth = new DateOnly(monthOf.Year, monthOf.Month, DateTime.DaysInMonth(monthOf.Year, monthOf.Month));
        return lastDayOfMonth >= start && lastDayOfMonth <= end;
    }

    /// <summary>True if [start, end] covers the 1st calendar day of any month it touches --
    /// IncludesEndOfMonth's mirror image, same two-month reasoning. Used by
    /// ContributionDefaultsFor above to decide whether this period is the one
    /// PhilHealth/Pag-IBIG get withheld on.</summary>
    private static bool IncludesStartOfMonth(DateOnly start, DateOnly end) =>
        IsMonthStartWithin(start, end, start) || IsMonthStartWithin(start, end, end);

    private static bool IsMonthStartWithin(DateOnly start, DateOnly end, DateOnly monthOf)
    {
        var firstDayOfMonth = new DateOnly(monthOf.Year, monthOf.Month, 1);
        return firstDayOfMonth >= start && firstDayOfMonth <= end;
    }

    /// <inheritdoc />
    /// <remarks>For each (Type, EmployeeDefault, TargetAmount) triple from
    /// ContributionDefaultsFor:
    /// - EmployeeDefault &lt;= 0 and !Type.BlankAmountMeansZero(): skip entirely -- this
    ///   employee has no default configured for this contribution type at all, regardless of
    ///   the period. Doesn't apply to Premium Pay/Allowance/Cash Advance (the three
    ///   BlankAmountMeansZero types -- see that method's own doc comment) -- those three fall
    ///   through and get seeded even at EmployeeDefault 0, since TargetAmount for them is just
    ///   EmployeeDefault unconditionally (see ContributionDefaultsFor), so a genuine 0 default
    ///   still deserves the same explicit, persisted row as any other amount rather than being
    ///   treated as "not configured".
    /// - No existing row for this type: add one at TargetAmount, EnteredBy = SeededEnteredBy.
    ///   This happens even when TargetAmount is 0.00 (this type's non-withholding period), so
    ///   the period ends up with an explicit, persisted "no contribution withheld this cutoff"
    ///   row instead of looking indistinguishable from a period nobody's ever opened.
    /// - An existing row whose EnteredBy is still SeededEnteredBy: nobody's hand-edited it
    ///   (see SeededEnteredBy's own doc comment for how PayrollViewModel.SetSingleValueAsync
    ///   re-stamps EnteredBy to the current Windows user the moment a person actually
    ///   commits into one of these rows) -- update its Amount to TargetAmount if
    ///   it doesn't already match. This is the actual "reseed": an SSS row that was correctly
    ///   0.00 on its non-withholding period gets corrected up to the employee default the
    ///   moment the period picker moves onto SSS's own withholding cutoff (month-end), and
    ///   back down again if it moves off -- same for PhilHealth/Pag-IBIG against their own
    ///   cutoff (month-start). Premium Pay/Allowance/Cash Advance reseed the same way but
    ///   aren't cutoff-driven -- since TargetAmount for them is just EmployeeDefault every
    ///   period, a still-seeded row only gets corrected here when someone changes that
    ///   employee's default on the Employee dialog after this period was already seeded.
    ///   All of this happens without ever touching a row a person has actually typed a
    ///   number into.
    /// - An existing row with any other EnteredBy: a person's own value -- left alone,
    ///   regardless of whether it happens to already match TargetAmount or not.
    ///
    /// try/catch(InvalidOperationException) race guard on both the Add and Update paths: even
    /// for a single employee, another writer (e.g. this same employee/period open in a second
    /// window, or a concurrent batch seed run) can still land a row -- or edit this exact one --
    /// between existingAdjustments being fetched and this call's own Add/Update. Returns the
    /// rows actually Added and the rows actually Updated separately (both empty if nothing
    /// needed touching) -- callers that need the merged list build it themselves from that,
    /// same as SeedDefaultContributionsAsync does above.</remarks>
    public async Task<(List<PayrollAdjustment> Added, List<PayrollAdjustment> Updated)> SeedOrReseedContributionsAsync(
        int pin, DateOnly start, DateOnly end,
        (PayrollAdjustmentType Type, decimal EmployeeDefault, decimal TargetAmount)[] contributionDefaults,
        IReadOnlyList<PayrollAdjustment> existingAdjustments, CancellationToken cancellationToken)
    {
        List<PayrollAdjustment> added = [];
        List<PayrollAdjustment> updated = [];

        // The actual skip/create/correct/leave-alone rule lives in DetermineContributionChanges
        // below now (shared with SeedContributionsForBatchAsync's own whole-batch version of
        // this same loop) -- this method's own job is just what it always was: write each
        // decision through the guarded single-row repository calls as it's made, one employee
        // at a time.
        foreach (var (toAdd, toUpdate) in DetermineContributionChanges(pin, start, end, contributionDefaults, existingAdjustments))
        {
            if (toAdd is not null)
            {
                try
                {
                    added.Add(await _adjustmentRepository.AddAsync(toAdd, cancellationToken));
                }
                catch (InvalidOperationException)
                {
                    // Another writer (e.g. this same employee/period opened in a second
                    // window, or a concurrent PrintPayslipsAsync/batch-seed run) already
                    // seeded or hand-entered this type between the existingAdjustments fetch
                    // and this Add -- same check-then-act race AddEmployeeAsync/
                    // UpdateEmployeeAsync already accept elsewhere in this app, just without a
                    // friendly exception to translate it into here since there's no
                    // user-facing action to retry: whichever row actually won is already
                    // correct and will show up on the next load either way, so this seed
                    // attempt simply yields to it rather than surfacing an error for something
                    // that isn't actually a problem.
                }

                continue;
            }

            try
            {
                await _adjustmentRepository.UpdateAsync(toUpdate!, cancellationToken);
                updated.Add(toUpdate!);
            }
            catch (InvalidOperationException)
            {
                // Same race as the Add path above, just landing on the Update side instead --
                // another writer already changed this exact row (e.g. retyped its Type)
                // between the existingAdjustments fetch and this Update. Yields to whichever
                // write actually won, same reasoning as the Add path's own catch.
            }
        }

        return (added, updated);
    }

    /// <summary>The pure "what needs to change" half of SeedOrReseedContributionsAsync's own
    /// skip/create/correct/leave-alone rule (see that method's own doc comment for the full
    /// rule) -- no repository calls, no CancellationToken, just contributionDefaults/
    /// existingAdjustments in, a stream of decisions out. Shared by SeedOrReseedContributionsAsync
    /// (one employee, writes each decision immediately as it's made) and
    /// SeedContributionsForBatchAsync below (every employee in a batch, collects every
    /// decision first and writes the whole batch in bulk) so the two paths can never drift
    /// apart on which rows actually get touched -- only on how the result gets persisted.
    ///
    /// Yields at most one tuple per contributionDefaults entry that isn't skipped outright,
    /// and never both members of a yielded tuple at once: (a brand-new row, null) for a type
    /// with no existing row yet -- Id left at its default 0, the only field a caller still
    /// needs to fill in before this is a real row -- or (null, the existing row's corrected
    /// copy, Id carried over) for a still-seeded row whose Amount no longer matches
    /// TargetAmount. A type this rule says to leave alone (not configured for this employee,
    /// hand-edited, or already correct) yields nothing at all.</summary>
    private static IEnumerable<(PayrollAdjustment? ToAdd, PayrollAdjustment? ToUpdate)> DetermineContributionChanges(
        int pin, DateOnly start, DateOnly end,
        (PayrollAdjustmentType Type, decimal EmployeeDefault, decimal TargetAmount)[] contributionDefaults,
        IReadOnlyList<PayrollAdjustment> existingAdjustments)
    {
        foreach (var (type, employeeDefault, targetAmount) in contributionDefaults)
        {
            if (employeeDefault <= 0 && !type.BlankAmountMeansZero()) continue;

            var existing = existingAdjustments.FirstOrDefault(a => a.Type == type);

            if (existing is null)
            {
                yield return (new PayrollAdjustment
                {
                    EmployeeId = pin,
                    PeriodStart = start,
                    PeriodEnd = end,
                    Type = type,
                    Amount = targetAmount,
                    Description = type.ToText(),
                    EnteredBy = SeededEnteredBy,
                }, null);

                continue;
            }

            if (existing.EnteredBy != SeededEnteredBy) continue; // hand-edited -- leave alone
            if (existing.Amount == targetAmount) continue; // already correct

            yield return (null, new PayrollAdjustment
            {
                Id = existing.Id,
                EmployeeId = existing.EmployeeId,
                PeriodStart = existing.PeriodStart,
                PeriodEnd = existing.PeriodEnd,
                Type = type,
                Amount = targetAmount,
                Description = existing.Description,
                EnteredBy = SeededEnteredBy,
            });
        }
    }

    /// <inheritdoc />
    /// <remarks>Runs DetermineContributionChanges above for every employee in
    /// <paramref name="employees"/> against <paramref name="batch"/>.AdjustmentsByPin (falling
    /// back to an empty list for a pin with no rows this period yet, same convention
    /// ComputeOneFromBatchAsync's own AdjustmentsByPin lookup already follows) before writing
    /// anything at all -- unlike SeedOrReseedContributionsAsync's own immediate-write loop,
    /// every employee's decisions are collected into two flat lists first. That's what lets the
    /// actual writes below happen in IPayrollAdjustmentRepository.AddRangeAsync/UpdateRangeAsync
    /// -- one round trip for every add in the batch, and (in the common case -- see
    /// UpdateRangeAsync's own doc comment for why) one more for every correction, instead of one
    /// guarded AddAsync/UpdateAsync round trip per row per employee -- rather than
    /// SeedOrReseedContributionsAsync's own per-row EnsureNoExistingRowAsync-guarded calls, which
    /// this batch path has no need for in the first place: every decision here is already made
    /// against this same
    /// <paramref name="batch"/>.AdjustmentsByPin, fetched once for the whole batch, so there's
    /// nothing left for a per-row guard query to catch that this method's own in-memory diff
    /// hasn't already accounted for.
    ///
    /// Employees with no Pin are skipped -- nothing to seed for them, same "excluded from the
    /// pins PrepareBatchAsync fetches for" convention RefreshPayrollGroupRowsAsync's own caller
    /// already applies before this is ever reached.
    ///
    /// No InvalidOperationException race guard the way SeedOrReseedContributionsAsync's own
    /// Add/Update try/catch has -- AddRangeAsync has no guard query to race against in the
    /// first place (see its own doc comment), and UpdateRangeAsync's DbUpdateConcurrencyException
    /// catch handles the one race that's actually possible here (a row deleted out from under
    /// this batch) without this method needing to know about it.
    ///
    /// Returns <paramref name="batch"/> itself, unchanged, when nothing needs seeding or
    /// correcting -- the common case once a batch's contributions already match the current
    /// rule -- rather than paying for two no-op repository calls and a dictionary rebuild for
    /// nothing.</remarks>
    public async Task<PayrollBatchContext> SeedContributionsForBatchAsync(
        IReadOnlyCollection<Employee> employees, DateOnly start, DateOnly end,
        PayrollBatchContext batch, CancellationToken cancellationToken)
    {
        List<PayrollAdjustment> toAdd = [];
        List<PayrollAdjustment> toUpdate = [];

        foreach (var employee in employees)
        {
            var pin = employee.Pin;

            var existing = batch.AdjustmentsByPin.GetValueOrDefault(pin, []);
            var contributionDefaults =
                ContributionDefaultsFor(employee, start, end, batch.AllSummaries, batch.HolidayDates);

            foreach (var (add, update) in DetermineContributionChanges(pin, start, end, contributionDefaults, existing))
            {
                if (add is not null) toAdd.Add(add);
                if (update is not null) toUpdate.Add(update);
            }
        }

        if (toAdd.Count == 0 && toUpdate.Count == 0) return batch;

        IReadOnlyList<PayrollAdjustment> added = toAdd.Count > 0
            ? await _adjustmentRepository.AddRangeAsync(toAdd, cancellationToken)
            : [];

        if (toUpdate.Count > 0)
            await _adjustmentRepository.UpdateRangeAsync(toUpdate, cancellationToken);

        // Swap each corrected row in by Id, append each pin's brand-new rows -- same merge
        // shape SeedDefaultContributionsAsync's own single-employee merge already uses, just
        // grouped by pin here since this is rebuilding a whole AdjustmentsByPin dictionary
        // instead of one employee's own adjustments list.
        var updatedById = toUpdate.ToDictionary(a => a.Id);
        var addedByPin = added.GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, IReadOnlyList<PayrollAdjustment> (g) => g.ToList());

        // GroupBy-then-ToDictionary rather than a plain ToDictionary keyed on e.Pin --
        // collapses safely if employees ever contains more than one Employee for the same pin
        // (ToDictionary itself would throw ArgumentException on a duplicate key), same
        // defensive shape as PrepareBatchAsync's own callers already building pins as a
        // HashSet<int> rather than trusting the input list to be pre-deduplicated.
        Dictionary<int, IReadOnlyList<PayrollAdjustment>> adjustmentsByPin = employees
            .GroupBy(e => e.Pin)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<PayrollAdjustment> (g) =>
                [
                    .. batch.AdjustmentsByPin.GetValueOrDefault(g.Key, [])
                        .Select(a => updatedById.GetValueOrDefault(a.Id, a)),
                    .. addedByPin.GetValueOrDefault(g.Key, []),
                ]);

        return new PayrollBatchContext
        {
            AllSummaries = batch.AllSummaries,
            AdjustmentsByPin = adjustmentsByPin,
            WaivedPins = batch.WaivedPins,
            HolidayDates = batch.HolidayDates,
            ContributionsSeeded = true,
        };
    }
}