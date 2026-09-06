using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Payroll;

/// <summary>
/// Turns one employee's attendance summaries and adjustment rows for one period
/// into the itemized Gross Pay/Deductions/Net Pay breakdown described in the
/// Payroll Feature plan (section 3). Deliberately pure: every input arrives as a
/// plain in-memory value (no repository, no IAttendanceRunner, no async) and the
/// same inputs always produce the same PayrollResult -- which is what makes it
/// possible to verify by hand-tracing a sample employee/period, per this class's
/// own build-order step, without a database or any UI standing between the
/// numbers and the code.
///
/// This is a deliberate one-step narrowing of the plan's section 3 sketch, which
/// described PayrollCalculator as itself calling IAttendanceRunner.RunAsync (and
/// putting it in a new project referencing ScheduleApp.Attendance for that
/// reason). Fetching the attendance run and the IPayrollAdjustmentRepository rows
/// is I/O that belongs on the caller's side of this boundary -- naturally
/// PayrollViewModel, once it exists (build-order step 4) -- which then hands the
/// results in here. ScheduleApp.Payroll's own project reference is Core-only for
/// exactly that reason (see its .csproj comment); nothing here needed to change
/// when that reference was dropped.
/// </summary>
public static class PayrollCalculator
{
    /// <summary>
    /// Computes one employee's full breakdown for one period.
    /// </summary>
    /// <param name="employee">Supplies DailyRate (Basic Pay is a flat DailyRate
    /// per workday -- Complete/Partial/OfficialBusiness/paid Leave -- and it's
    /// also the HourlyRate numerator for Overtime/Night Diff/Undertime) and
    /// DisplayName. Must have a Pin set -- see the ArgumentException below for
    /// why.</param>
    /// <param name="policy">Supplies StandardHoursPerDay/OvertimeRatePercentage/
    /// NightDiffRatePercentage/NetPayRoundingMultiple.</param>
    /// <param name="summaries">Every AttendanceSummary available for the run this
    /// came from -- e.g. straight off AttendanceRunResult.Summaries. Filtered
    /// internally down to this employee/period (see the Where below), so a
    /// caller doesn't need to pre-filter; passing a run's full multi-employee
    /// result straight through is fine.</param>
    /// <param name="adjustments">Every PayrollAdjustment available -- e.g.
    /// straight off IPayrollAdjustmentRepository.GetForEmployeePeriodAsync.
    /// Filtered internally the same way as summaries, for the same reason.</param>
    /// <param name="periodStart">Inclusive.</param>
    /// <param name="periodEnd">Inclusive.</param>
    /// <param name="undertimeWaived">True if a person has checked "disregard" for
    /// this employee/period's Undertime (see IPayrollUndertimeWaiverRepository) --
    /// the Undertime line is still computed and shown exactly the same either way
    /// (see PayrollLineItem.Waived's own doc comment), this only flows through to
    /// that flag so PayrollResult.TotalDeductions knows whether to count it.
    /// Defaults to false so every existing caller/test that predates waiving
    /// doesn't need to change.</param>
    /// <param name="holidayDates">Every date on file in the Holidays table (see
    /// Holiday/IHolidayRepository) -- not pre-filtered to this period, the same
    /// "hand the whole thing through, this method does its own filtering"
    /// convention <paramref name="summaries"/>/<paramref name="adjustments"/>
    /// already follow. Feeds the Holiday Pay day-component-plus-OT/ND-copies
    /// computation in the per-day loop below (Holiday Pay plan, Phase 2) --
    /// see <see cref="CalculateHolidayPay"/>, which this delegates to. Defaults
    /// to null (treated as empty, i.e. no listed holidays) so every existing
    /// caller/test that predates the Holiday Pay feature doesn't need to
    /// change, same reasoning as <paramref name="undertimeWaived"/> above.</param>
    public static PayrollResult Calculate(
        Employee employee,
        PayrollPolicy policy,
        IReadOnlyList<AttendanceSummary> summaries,
        IReadOnlyList<PayrollAdjustment> adjustments,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool undertimeWaived = false,
        IReadOnlyCollection<DateOnly>? holidayDates = null)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(adjustments);

        // Every AttendanceSummary/PayrollAdjustment row is keyed by Employee.Pin
        // (the punch clock's own employee code), never Employee.Id -- same
        // convention as AttendanceWorkflowService. Every employee has one now
        // (see Employee.Pin's own doc comment), so there's nothing left to guard
        // against here.
        var employeeId = employee.Pin;

        if (periodEnd < periodStart)
        {
            throw new ArgumentException(
                $"periodEnd ({periodEnd}) is before periodStart ({periodStart}).",
                nameof(periodEnd));
        }

        var periodSummaries = summaries.Where(s =>
            s.EmployeeId == employeeId &&
            s.ShiftDate >= periodStart &&
            s.ShiftDate <= periodEnd).ToList();

        // --- Divisor, Rest Day count, & unscheduled-day count (Employee Pay
        // Types & Rest Day plan, assumption 9 / §7 item 1) ---------------------
        // Company policy: the Monthly-rated divisor always assumes a nominal
        // 15-day period, never the period's real calendar length -- only Rest
        // Days within the period's first 15 calendar days count (e.g. day 31 of
        // a 31-day month's second half sits outside the nominal span entirely
        // and never itself reduces the divisor). This does NOT mean day 31 is
        // irrelevant to pay, though -- a real period longer than 15 days still
        // earns a prorated extra for each excess day actually worked (see the
        // excessCreditedDays handling in the Monthly per-day loop and the
        // Basic Pay computation below); the divisor itself just never counts
        // that day as a Rest Day one way or the other. Computed once, ahead of
        // the Daily/Monthly branch below, since both branches' downstream lines
        // read from the same periodSummaries pass. Collapsed by day the same
        // way AttendanceDayStatus does -- RestDay never mixes with another
        // ScheduleType on the same day today, so grouping by ShiftDate and
        // requiring every row that day to be RestDay is equivalent.
        const int nominalPeriodDays = 15;
        DateOnly nominalPeriodEnd = periodStart.AddDays(nominalPeriodDays - 1);

        int restDays = periodSummaries
            .Where(s => s.ShiftDate <= nominalPeriodEnd)
            .GroupBy(s => s.ShiftDate)
            .Count(g => g.All(s => s.ScheduleType == ScheduleType.RestDay));

        int divisor = nominalPeriodDays - restDays;

        // Unscheduled-day count: calendar days in the *real* period with no
        // AttendanceSummary row at all for this employee -- distinct from an
        // Absent day (which has a row; a schedule entry exists, no punch was
        // found) and from a Rest Day (also has a row). Correct for both
        // employee types, but only actionable for Monthly-rated: a gap day
        // for a Daily-rated employee is already harmless (simply not
        // credited, same as today), whereas for Monthly-rated it silently
        // neither reduces the divisor above nor counts toward uncreditedDays
        // below -- a real overpayment risk if a genuine day off is left
        // unscheduled instead of marked Rest Day. Only the count itself is
        // added here; surfacing it as a Payroll tab caution is a later phase.
        int periodDays = periodEnd.DayNumber - periodStart.DayNumber + 1;
        int scheduledDayCount = periodSummaries.Select(s => s.ShiftDate).Distinct().Count();
        int unscheduledDayCount = periodDays - scheduledDayCount;

        // --- Branch on EmployeeType -------------------------------------------
        // Daily-rated: unchanged from before this feature -- HourlyRate =
        // Employee.DailyRate / PayrollPolicy.StandardHoursPerDay. Overtime,
        // Night Diff, and Undertime derive from this figure (hours beyond or
        // short of a workday); Basic Pay itself is flat DailyRate per workday
        // (see BasicPayForDay) and doesn't use it. A StandardHoursPerDay of
        // zero or less is a misconfigured PayrollPolicy (the default is 8;
        // nothing in PayrollPolicy itself stops someone editing appsettings.json
        // to something invalid) -- falling back to a 0 HourlyRate rather than
        // throwing keeps one bad config value from taking down the whole
        // Payroll tab, same spirit as Employee.DailyRate defaulting to 0 rather
        // than erroring for an employee whose rate isn't set yet.
        //
        // Monthly-rated: a fixed semi-monthly salary whose *daily* value
        // fluctuates per period with the Rest Day count above --
        // effectiveDailyRate = SemiMonthlyRate / divisor -- and that same
        // effectiveDailyRate (and its derived HourlyRate) feeds Overtime/Night
        // Diff/Undertime below exactly like Daily-rated's constant HourlyRate
        // does, so nothing downstream of this needs its own Monthly-specific
        // code path (confirmed assumption 7). Guarded against divisor <= 0
        // (every day in the period somehow marked Rest Day) the same way
        // StandardHoursPerDay <= 0 is guarded above.
        var (hourlyRate, semiMonthlyRate, effectiveDailyRate) = ResolveRates(employee, policy, divisor);

        decimal basicPay = 0m;
        decimal overtimePay = 0m;
        decimal nightDiffPay = 0m;
        decimal restDayPay = 0m;
        int basicPayDays = 0;
        int uncreditedDays = 0;
        int excessCreditedDays = 0;
        decimal undertimeHours = 0m;
        decimal overtimeHours = 0m;
        decimal nightDiffHours = 0m;
        decimal restDayHours = 0m;

        // Overtime/Night Diff pay used to be "sum every day's hours, multiply
        // once at the end by a single period-wide rate" -- valid only because
        // the rate was constant across the whole period. Now that the rate
        // percentage can vary per day (ScheduleEntry.OvertimeRatePercentageOverride/
        // NightDiffRatePercentageOverride -- see the Attendance & Payroll
        // Calculation Refactor Plan, section 8), a period mixing e.g. a 25% day
        // and a 30% day can no longer be collapsed that way -- each day's peso
        // contribution has to be computed individually and summed, with a
        // single rounding pass across the summed pesos (not the summed hours)
        // taking the place of the old single rounding pass at the end.
        //
        // Each day's *hours* are rounded to 2 dp (RoundHours) before they're
        // used for anything -- both the pay math right below and the period
        // totals (undertimeHours/overtimeHours/nightDiffHours) that feed the
        // "(x.xxH)" labels. AttendanceSummary.OvertimeHours/NightDiffHours/RemainHours
        // arrive as raw doubles (full TimeSpan precision, e.g. 2.61666...
        // for 2h37m), and multiplying pay off that raw figure while only
        // *displaying* a 2dp-rounded version of it (the old behavior) meant
        // the displayed hours could never be multiplied back out by hand to
        // reproduce the shown peso amount -- correct, but confusing. Rounding
        // per day rather than rounding the period's summed total once, keeps
        // this correct even when a period mixes rate percentages: each day's
        // contribution is still computed and rounded independently before
        // summing, so a mixed-rate period still adds up correctly, it just
        // does so from 2dp-clean hours instead of raw ones now.
        // Grouped by ShiftDate first: a SplitShift day produces one
        // AttendanceSummary row per segment (see
        // SplitShiftCalculationStrategy), not one row for the whole day, so
        // iterating periodSummaries directly and crediting Basic Pay per row
        // double- (or triple-) pays a split shift and inflates the "Basic Pay
        // (XD)" count to match -- the day count and the peso figure agreeing
        // with each other doesn't catch this, since both are wrong the same
        // way. AttendanceDayStatus.Resolve already solves the identical
        // problem for the Excel exporter's totals and the day-status
        // dashboard tile (see its own doc comment) -- reused here so Payroll
        // agrees with what those already show for the same day, instead of
        // re-deriving its own notion of "how many segments make a day."
        foreach (var dayGroup in periodSummaries.GroupBy(s => s.ShiftDate))
        {
            var dayRows = dayGroup.ToList();

            // Resolved once per calendar day: all-Complete/all-Absent/etc.
            // collapse to that status, any genuine mix (e.g. one segment
            // worked, the other missed) resolves to Partial -- see
            // AttendanceDayStatus.Resolve. IsPaidLeave is read off the first
            // row since Leave/OfficialBusiness never produce more than one
            // row for a day (only SplitShift segments do, and a day is never
            // both SplitShift and Leave).
            // All-RestDay resolves to PunchStatus.RestDay the same way (see
            // Phase 2) -- and since RestDayShiftCalculationStrategy never mixes
            // RestDay with any other ScheduleType on the same day, dayStatus ==
            // RestDay here is equivalent to (and used below in place of)
            // checking ScheduleType directly.
            var dayStatus = AttendanceDayStatus.Resolve(dayRows.Select(r => r.Status));

            if (employee.EmployeeType == EmployeeType.Monthly)
            {
                // Monthly-rated Basic Pay isn't built additively per day (see
                // below, after the loop) -- this only needs to know which days
                // weren't credited (within the nominal 15-day window -- to
                // deduct effectiveDailyRate for each) and which days WERE
                // credited beyond that window (to add effectiveDailyRate for
                // each). The addition side is company policy: a real period
                // longer than the nominal 15 days -- e.g. Aug 16-31's 16 real
                // days -- pays a prorated extra for each excess day actually
                // worked, rather than treating day 16 as a free day the flat
                // semiMonthlyRate happens to also cover. Mirrors exactly which
                // statuses BasicPayForDay credits/doesn't credit (Complete/
                // OfficialBusiness/paid Leave credited; Absent/Partial/unpaid
                // Leave not) via IsCreditedDay, rather than BasicPayForDay's
                // own decimal return -- that return is keyed off
                // Employee.DailyRate, which is 0 (unused) for a Monthly-rated
                // employee, so comparing it to 0m can't tell a credited day
                // from an uncredited one the way it can for Daily-rated. A
                // Rest Day is excluded entirely, in both windows: it's not an
                // absence, so it neither deducts nor gets credited/added.
                if (dayStatus != PunchStatus.RestDay)
                {
                    bool creditedDay = IsCreditedDay(dayStatus, dayRows[0].IsPaidLeave, employee);

                    if (dayGroup.Key <= nominalPeriodEnd)
                    {
                        // Within the nominal window: only an uncredited day
                        // deducts. A credited day here is already paid for by
                        // the flat semiMonthlyRate below -- no addition.
                        if (!creditedDay) uncreditedDays++;
                    }
                    else
                    {
                        // Beyond the nominal window (the "excess" days a
                        // longer-than-15-real-day period adds): a credited day
                        // here isn't already covered by the flat rate, so it
                        // adds effectiveDailyRate on top. An uncredited day
                        // here is neutral -- confirmed policy: it simply
                        // forfeits that day's bonus, it does NOT additionally
                        // deduct on top of whatever the nominal window already
                        // covers.
                        if (creditedDay) excessCreditedDays++;
                    }
                }
            }
            else
            {
                decimal dayBasicPay = BasicPayForDay(dayStatus, dayRows[0].IsPaidLeave, employee);
                basicPay += dayBasicPay;

                // Counts the same days BasicPayForDay itself credits (Complete/
                // OfficialBusiness/paid Leave) rather than every periodSummaries row
                // (which would also include Absent/Partial/unpaid Leave days) --
                // matches Amount above so "X day(s)" and the peso figure next to it
                // never disagree, including the DailyRate == 0 misconfigured-employee
                // edge case BasicPayForDay's own doc comment mentions, where both
                // land on zero together instead of the day count alone claiming a
                // day was paid.
                if (dayBasicPay > 0m) basicPayDays++;
            }

            // Overtime/Night Diff/Undertime/Rest Day Pay, unlike Basic Pay
            // above, genuinely do accumulate across a day's segments (e.g. one
            // segment ran late and a separate segment logged overtime), so
            // these still sum per row -- only Basic Pay/the day count needed
            // to stop double-counting per segment. All four apply identically
            // to both employee types -- hourlyRate above already carries
            // whichever figure this employee/period resolved to, fluctuating
            // or not (confirmed assumption 7).
            foreach (var day in dayRows)
            {
                decimal dayUndertimeHours = RoundHours(day.RemainHours);
                decimal dayOvertimeHours = RoundHours(day.OvertimeHours);
                decimal dayNightDiffHours = RoundHours(day.NightDiffHours);
                decimal dayRestDayHours = day.ScheduleType == ScheduleType.RestDay
                    ? RoundHours(day.WorkedHours)
                    : 0m;

                undertimeHours += dayUndertimeHours;
                overtimeHours += dayOvertimeHours;
                nightDiffHours += dayNightDiffHours;

                if (dayOvertimeHours > 0)
                {
                    decimal pay = hourlyRate * dayOvertimeHours;
                    if (day.ApplyOvertimeRatePercentage)
                    {
                        decimal otRatePercentage = day.OvertimeRatePercentageOverride ?? policy.OvertimeRatePercentage;
                        pay *= 1 + otRatePercentage;
                    }
                    overtimePay += pay;
                }

                if (dayNightDiffHours > 0)
                {
                    decimal ndRatePercentage = day.NightDiffRatePercentageOverride ?? policy.NightDiffRatePercentage;
                    nightDiffPay += hourlyRate * dayNightDiffHours * ndRatePercentage;
                }

                // Rest Day Pay: only when the day resolves to an unambiguous
                // duty (WorkedHours > 0 -- RestDayShiftCalculationStrategy only
                // populates it for a clean punch match or both ends of a
                // scheduled window, per the design doc's §5). Applies to both
                // employee types -- a Daily-rated employee can just as easily
                // be called in on a scheduled Rest Day -- so this isn't gated
                // behind the EmployeeType branch above. Straight time plus
                // premium in one multiplication, the same shape Overtime
                // already uses, since unlike Overtime/Night Diff hours, Rest
                // Day hours aren't otherwise covered by Basic Pay at all (the
                // day isn't a scheduled workday to begin with). At the default
                // 0% RestDayWorkPremiumPercentage this still pays straight
                // time -- only the premium portion is zero.
                if (dayRestDayHours > 0)
                {
                    restDayHours += dayRestDayHours;
                    restDayPay += hourlyRate * dayRestDayHours * (1 + employee.RestDayWorkPremiumPercentage);
                }
            }
        }

        // Monthly-rated Basic Pay/WorkDays: computed once here, after the
        // loop, rather than accumulated per day above -- SemiMonthlyRate paid
        // in full by default, minus effectiveDailyRate for each uncredited
        // day within the nominal window, plus effectiveDailyRate for each
        // credited day beyond it (see the per-day loop's comment above for
        // why the excess side is additive rather than folded into the same
        // deduction). WorkDays here keeps the same "days credited as paid"
        // meaning Daily-rated's basicPayDays already carries above -- divisor
        // minus however many of the nominal 15 weren't credited, plus
        // however many excess days beyond it were.
        if (employee.EmployeeType == EmployeeType.Monthly)
        {
            basicPay = semiMonthlyRate
                - (effectiveDailyRate * uncreditedDays)
                + (effectiveDailyRate * excessCreditedDays);
            basicPayDays = divisor - uncreditedDays + excessCreditedDays;
        }

        decimal basicPayAmount = Round(basicPay);
        decimal overtimePayAmount = Round(overtimePay);
        decimal nightDiffPayAmount = Round(nightDiffPay);
        decimal restDayPayAmount = Round(restDayPay);
        decimal undertimePayAmount = Round(hourlyRate * undertimeHours);

        // Holiday Pay (Holiday Pay plan, Phase 2): delegates to
        // CalculateHolidayPay rather than folding its own day loop in here --
        // that method needs to be independently callable (Phase 3's
        // PayrollComputationService.ContributionDefaultsFor calls it directly,
        // ahead of this method ever running -- see CalculateHolidayPay's own
        // doc comment), so Calculate reuses it here instead of keeping two
        // copies of the same per-day OT/ND-copy math in sync by hand. Passes
        // the original, unfiltered `summaries`/`holidayDates` straight
        // through -- CalculateHolidayPay does its own employee/period
        // filtering identically to periodSummaries above, so there's nothing
        // to pre-narrow here.
        var (holidayPayAmount, holidayWorkedDays) =
            CalculateHolidayPay(employee, policy, summaries, periodStart, periodEnd, holidayDates);

        // The rate shown in each label -- "x <rate>" -- is derived from the
        // period's own rounded Amount and Hours (Amount / Hours) rather than
        // recomputed separately from policy/day fields, so it's guaranteed to
        // be the exact figure that reproduces Amount when multiplied by the
        // Hours shown right next to it, even for a period that mixes more
        // than one day-level rate override (see the per-day loop above) --
        // Amount/Hours is, by construction, the weighted-average rate across
        // however many different day rates actually contributed. Falls back
        // to the nominal policy-resolved rate (rather than dividing by zero)
        // when the period has no hours of that kind at all, so the label
        // still shows a meaningful rate instead of "x 0.00".
        //
        // Displayed with up to 4 dp ("0.00##" below, not "N2"/2dp like every
        // other money figure in this app) -- unlike Amount, a rate isn't
        // itself a centavo-denominated currency value, and rounding it to
        // 2dp for display would silently reopen the exact mismatch fixing
        // Hours above was meant to close: e.g. a P450/8 = P56.25 hourly rate
        // at 25% OT premium is really P70.3125/hr, and 2.62H * the 2dp-rounded
        // P70.31 reproduces P184.21, not the P184.22 Amount actually shown.
        decimal overtimeRate = overtimeHours > 0
            ? overtimePayAmount / overtimeHours
            : hourlyRate * (1 + policy.OvertimeRatePercentage);

        decimal nightDiffRate = nightDiffHours > 0
            ? nightDiffPayAmount / nightDiffHours
            : hourlyRate * policy.NightDiffRatePercentage;

        // Undertime has no premium/override concept at all (unlike Overtime/
        // Night Diff above) -- its Amount is always plain hourlyRate * hours
        // -- so there's nothing to derive here; the rate is just hourlyRate,
        // hours or no hours.

        // Basic Pay line label: Monthly-rated's Basic Pay isn't built
        // additively per day the way Daily-rated's "(XD)" count is, so
        // reusing that count would read oddly -- shows "Semi-Monthly"
        // instead, with the uncredited-day and excess-day counts each folded
        // in only when something actually happened this period (either or
        // both can apply at once, e.g. a 16-real-day period with both an
        // absence in the nominal window and a credited excess day). Flagged
        // as a first proposal, not final -- easy to revisit once it's next
        // to the other lines in the UI (Phase 4).
        string basicPayLabel;
        if (employee.EmployeeType == EmployeeType.Monthly)
        {
            var labelParts = new List<string>();
            if (uncreditedDays > 0) labelParts.Add($"{uncreditedDays}D deducted");
            if (excessCreditedDays > 0) labelParts.Add($"{excessCreditedDays}D excess");

            basicPayLabel = labelParts.Count > 0
                ? $"Basic Pay (Semi-Monthly, {string.Join(", ", labelParts)})"
                : "Basic Pay (Semi-Monthly)";
        }
        else
        {
            basicPayLabel = $"Basic Pay ({basicPayDays}D)";
        }

        var computedGrossPay = new List<PayrollLineItem>
        {
            // "0.00" hours formatting matches AttendanceSummaryRow.FormatHours'
            // own convention (same reasoning as the Undertime line below); day
            // count has no decimal since BasicPayForDay only ever credits a day
            // as a whole one, never a fraction -- true of Daily-rated's "(XD)"
            // label; Monthly-rated's label above is a different shape entirely
            // (see basicPayLabel). The "x <rate>" suffix on each line -- Basic
            // Pay's own flat DailyRate, Overtime/Night Diff's derived effective
            // rate above -- is what actually multiplies out to Amount, so a
            // Payroll Summary card or printed payslip can be hand-verified line
            // by line instead of taking the peso figure on faith.
            new() { Label = basicPayLabel, Amount = basicPayAmount },
            new() { Label = $"Overtime ({overtimeHours:0.00}H)", Amount = overtimePayAmount },
            new() { Label = $"Night Diff ({nightDiffHours:0.00}H)", Amount = nightDiffPayAmount },
        };

        // Fourth computed Gross Pay line -- unlike the three above, this one is
        // NOT always present. It's a deliberate, documented exception to this
        // list's own "every computed line always shows, even at zero" convention:
        // an employee without QualifiesForRestDayPay never sees a "Rest Day Pay
        // (0.00H) -- ₱0.00" line at all, rather than seeing the pay type exist
        // for them at zero. restDayHours/restDayPay accumulation above is left
        // untouched either way -- PayrollResult.RestDayHours is still populated
        // for an ineligible employee, since other readers (e.g.
        // PayrollExcelExporter) may still want the raw hours figure -- only the
        // line item shown on the Payroll Summary/payslip is gated here.
        // RestDayPayAmount below is different: unlike the hours figure, it's
        // gated the same way this line is (0 when ineligible), since it stands
        // in for "the peso amount this employee was actually credited," and an
        // ineligible employee is credited nothing.
        if (employee.QualifiesForRestDayPay)
        {
            computedGrossPay.Add(new() { Label = $"Rest Day Pay ({restDayHours:0.00}H)", Amount = restDayPayAmount });
        }

        var computedDeductions = new List<PayrollLineItem>
        {
            new()
            {
                // Undertime's Amount (below) is already hourlyRate * undertimeHours
                // combined, not split by Late-In vs. Early-Out -- so the label just
                // states the same combined hours figure back, plus the current
                // Excluded/Included state (undertimeWaived, the parameter this
                // method was called with) so a look at the Payroll Summary card
                // explains itself instead of just showing a bare peso figure. This
                // is the state, not the action -- opposite of DeductionLineTemplate's
                // own toggle button label (see WaivedToExcludeIncludeLabelConverter's
                // doc comment), which shows what clicking it will do rather than
                // what's currently true. "0.00" matches AttendanceSummaryRow.
                // FormatHours' own hours formatting, for consistency with how hours
                // read on the Attendance tab. "x <hourlyRate>" matches the "x <rate>"
                // suffix the three ComputedGrossPay lines above carry, for the same
                // hand-verification reason -- Undertime has no premium of its own, so
                // this is always just the plain hourlyRate, not a derived figure. Same
                // "0.00##" (up to 4 dp, trimmed) formatting as those -- see the comment
                // above overtimeRate/nightDiffRate for why 2 dp isn't enough here.
                Label = $"Undertime ({undertimeHours:0.00}H)" +
                        (undertimeWaived ? ", Excluded" : string.Empty),
                Amount = undertimePayAmount,
                Waived = undertimeWaived,
                SupportsWaiver = true,
            },
        };

        var periodAdjustments = adjustments.Where(a =>
            a.EmployeeId == employeeId &&
            a.PeriodStart == periodStart &&
            a.PeriodEnd == periodEnd);

        var grossPayGroups = new List<PayrollAdjustmentGroup>();
        var deductionGroups = new List<PayrollAdjustmentGroup>();

        // Enum.GetValues returns PayrollAdjustmentType in declaration order --
        // Allowance, Incentive, PremiumHoliday, then SSS, PhilHealth, PagIbig,
        // CashAdvance, OtherCharge -- which is already Gross Pay-then-Deductions
        // and already matches the display order the Payroll Feature plan's UI
        // flow section lists, so splitting on IsDeduction() below preserves it
        // in both resulting lists without a separate sort.
        var adjustmentsByType = periodAdjustments.ToLookup(a => a.Type);
        foreach (var type in Enum.GetValues<PayrollAdjustmentType>())
        {
            // Premium Pay (PayrollAdjustmentType.PremiumHoliday) is the one type
            // in this loop that's gated by an employee-level eligibility flag --
            // an employee without QualifiesForPremiumPay gets no group for it at
            // all, the same "omit the line entirely" treatment the Rest Day Pay
            // line above gets. Non-destructive: any PremiumHoliday adjustment row
            // already sitting in the DB for this employee/period (typed in
            // before the flag existed, or before it was toggled off) is simply
            // skipped over here, not deleted -- it reappears the moment the flag
            // is re-enabled, since this only affects what's grouped for display,
            // not what's stored.
            if (type == PayrollAdjustmentType.PremiumHoliday && !employee.QualifiesForPremiumPay)
            {
                continue;
            }

            var group = new PayrollAdjustmentGroup
            {
                Type = type,
                Adjustments = [.. adjustmentsByType[type]],
            };

            (type.IsDeduction() ? deductionGroups : grossPayGroups).Add(group);
        }

        return new PayrollResult
        {
            EmployeeId = employeeId,
            EmployeeName = employee.DisplayName,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            ComputedGrossPay = computedGrossPay,
            ComputedDeductions = computedDeductions,
            WorkDays = basicPayDays,
            UnscheduledDayCount = unscheduledDayCount,
            OvertimeHours = overtimeHours,
            NightDiffHours = nightDiffHours,
            RestDayHours = restDayHours,
            RestDayPayAmount = employee.QualifiesForRestDayPay ? restDayPayAmount : 0m,
            HolidayPayAmount = holidayPayAmount,
            HolidayWorkedDays = holidayWorkedDays,
            UndertimeHours = undertimeHours,
            GrossPayAdjustmentGroups = grossPayGroups,
            DeductionAdjustmentGroups = deductionGroups,
            NetPayRoundingMultiple = policy.NetPayRoundingMultiple,
        };
    }

    /// <summary>The Daily-rated/Monthly-rated HourlyRate/SemiMonthlyRate/
    /// EffectiveDailyRate branch that used to live inline in <see
    /// cref="Calculate"/>, pulled out so <see cref="CalculateHolidayPay"/> (Holiday
    /// Pay plan, Phase 2/3) can resolve the exact same rates a second time --
    /// e.g. from <c>PayrollComputationService.ContributionDefaultsFor</c>, ahead of
    /// (and independently of) a full <see cref="Calculate"/> call -- without a
    /// second, potentially-drifting copy of this formula. Pure cut of the
    /// pre-existing block; every comment on the branch itself still applies
    /// unchanged, just now read from here instead of inline. <paramref
    /// name="divisor"/> is passed in rather than recomputed here since it depends
    /// on a Rest Day count over <paramref name="periodStart"/>'s nominal 15-day
    /// window (see <see cref="Calculate"/>'s own restDays/divisor comment) that
    /// both callers need for other reasons anyway (Calculate's own
    /// basicPayDays/WorkDays; CalculateHolidayPay's own periodSummaries scoping) --
    /// duplicating that one small count-and-subtract at each call site, rather than
    /// bundling it into this method too, keeps this method a pure rates-only
    /// computation with a single obvious purpose.</summary>
    private static (decimal HourlyRate, decimal SemiMonthlyRate, decimal EffectiveDailyRate) ResolveRates(
        Employee employee, PayrollPolicy policy, int divisor)
    {
        decimal hourlyRate;
        decimal semiMonthlyRate = 0m;
        decimal effectiveDailyRate = 0m;

        if (employee.EmployeeType == EmployeeType.Monthly)
        {
            semiMonthlyRate = employee.MonthlyRate / 2m;
            effectiveDailyRate = divisor > 0 ? semiMonthlyRate / divisor : 0m;
            hourlyRate = policy.StandardHoursPerDay > 0
                ? effectiveDailyRate / policy.StandardHoursPerDay
                : 0m;
        }
        else
        {
            hourlyRate = policy.StandardHoursPerDay > 0
                ? employee.DailyRate / policy.StandardHoursPerDay
                : 0m;
        }

        return (hourlyRate, semiMonthlyRate, effectiveDailyRate);
    }

    /// <summary>
    /// One employee/period's Holiday Pay (Holiday Pay plan, Phase 2) -- the
    /// day component plus matching Overtime/Night Diff "copies" earned for
    /// working on a date listed in <paramref name="holidayDates"/>, alongside
    /// (not instead of) that same day's ordinary Basic Pay/Overtime/Night Diff
    /// lines in <see cref="Calculate"/>. Called both by <see cref="Calculate"/>
    /// itself (to populate <see cref="PayrollResult.HolidayPayAmount"/>/
    /// <see cref="PayrollResult.HolidayWorkedDays"/>) and directly by
    /// <c>PayrollComputationService.ContributionDefaultsFor</c> (Phase 3), which
    /// needs this same figure to seed the <see
    /// cref="PayrollAdjustmentType.PremiumHoliday"/> row's TargetAmount before
    /// that period's adjustments -- and therefore <see cref="Calculate"/> itself
    /// -- have run yet. A standalone method rather than a private helper
    /// <see cref="Calculate"/> alone can reach, for exactly that reason: Phase 3's
    /// caller has no PayrollResult to pull the figure off of at the point it
    /// needs it.
    ///
    /// No Regular-vs-Special distinction (see Holiday's own doc comment) -- every
    /// listed date is treated identically, at a flat "one extra day's pay" premium
    /// (dayRate once more, not a configurable percentage the way
    /// Employee.RestDayWorkPremiumPercentage is for Rest Day Pay): PH labor law's
    /// worked-regular-holiday rate is 200% of the daily wage, i.e. Basic Pay's own
    /// 100% plus exactly one more 100% here, so this simplification lines up with
    /// the one case this feature currently models even though it isn't
    /// parameterized to say so explicitly.
    ///
    /// Nonzero for a day that resolves to <see cref="PunchStatus.Complete"/> or an
    /// actually-worked <see cref="PunchStatus.RestDay"/> (WorkedHours > 0 -- see
    /// RestDayShiftCalculationStrategy's own doc comment for why Status alone can't
    /// tell a worked Rest Day from an unworked one) -- this now stacks with whatever
    /// Rest Day Pay that same duty separately earns in <see cref="Calculate"/>'s own
    /// per-day loop, for both employee types (company policy update; there used to
    /// be a blanket "no stacking" exclusion for any Holiday date landing on a
    /// scheduled Rest Day, worked or not).
    ///
    /// Also nonzero -- Monthly-rated only -- for a listed Holiday the employee
    /// simply didn't work at all (Absent, Partial, unpaid Leave, or an unworked
    /// Rest Day): company policy update, a Monthly-rated Premium Pay-eligible
    /// employee still earns this day's flat one-day component even without
    /// working it, gated on <see cref="IsCreditedDay"/> answering false (i.e. Basic
    /// Pay's own uncreditedDays deduction would otherwise dock this exact day) so a
    /// day Basic Pay already pays in full -- OfficialBusiness or paid Leave -- isn't
    /// also paid a second time here. No OT/ND "copies" for this case; those only
    /// apply when the day was actually worked. Daily-rated is unchanged: still
    /// strictly no work, no Holiday Pay, for anything short of Complete/a worked
    /// Rest Day.
    /// </summary>
    public static (decimal HolidayPayAmount, int HolidayWorkedDays) CalculateHolidayPay(
        Employee employee,
        PayrollPolicy policy,
        IReadOnlyList<AttendanceSummary> summaries,
        DateOnly periodStart,
        DateOnly periodEnd,
        IReadOnlyCollection<DateOnly>? holidayDates)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(summaries);

        var employeeId = employee.Pin;

        if (holidayDates is not { Count: > 0 })
        {
            // No listed holidays at all -- nothing to compute, so this returns the
            // same "nothing happened" shape Calculate itself would eventually throw
            // for a genuinely invalid input, rather than throwing here too:
            // ContributionDefaultsFor (Phase 3) calls this ahead of Calculate, and
            // would rather fall back to Employee.DefaultPremiumPay for a
            // not-yet-fully-configured employee than blow up the whole payroll
            // period seed over it.
            return (0m, 0);
        }

        var periodSummaries = summaries.Where(s =>
            s.EmployeeId == employeeId &&
            s.ShiftDate >= periodStart &&
            s.ShiftDate <= periodEnd).ToList();

        // Same nominal-15-day-window Rest Day count/divisor Calculate computes
        // for itself (see that method's own comment on this exact block) --
        // duplicated here rather than threaded through as a parameter so this
        // method stays a single self-contained round trip from
        // (employee, summaries, period) straight to a Holiday Pay figure, the
        // same "hand the whole thing through" shape Calculate's own parameters
        // already have.
        const int nominalPeriodDays = 15;
        DateOnly nominalPeriodEnd = periodStart.AddDays(nominalPeriodDays - 1);

        int restDays = periodSummaries
            .Where(s => s.ShiftDate <= nominalPeriodEnd)
            .GroupBy(s => s.ShiftDate)
            .Count(g => g.All(s => s.ScheduleType == ScheduleType.RestDay));

        int divisor = nominalPeriodDays - restDays;

        var (hourlyRate, _, effectiveDailyRate) = ResolveRates(employee, policy, divisor);
        decimal dayRate = employee.EmployeeType == EmployeeType.Monthly ? effectiveDailyRate : employee.DailyRate;

        decimal holidayPay = 0m;
        int holidayWorkedDays = 0;

        foreach (var dayGroup in periodSummaries.GroupBy(s => s.ShiftDate))
        {
            if (!holidayDates.Contains(dayGroup.Key)) continue;

            var dayRows = dayGroup.ToList();
            var dayStatus = AttendanceDayStatus.Resolve(dayRows.Select(r => r.Status));

            // A Rest Day's Status is always PunchStatus.RestDay whether or not a
            // duty was actually recognized that day (see
            // RestDayShiftCalculationStrategy's own doc comment) -- WorkedHours > 0
            // is what actually tells a worked Rest Day apart from an unworked
            // one, the same signal Calculate's own Rest Day Pay line uses.
            bool restDayDutyWorked = dayStatus == PunchStatus.RestDay
                && dayRows.Any(r => RoundHours(r.WorkedHours) > 0);

            // "Worked the holiday" now covers both an ordinary Complete day and
            // a Rest Day actually worked -- company policy update: a listed
            // Holiday landing on someone's scheduled Rest Day is no longer a
            // "no stacking" case that earns nothing here, it earns Holiday Pay
            // right alongside whatever Rest Day Pay that duty separately earns
            // in Calculate's own per-day loop (both employee types, since
            // there's nothing Monthly-specific about actually being called in
            // to work).
            bool worked = dayStatus == PunchStatus.Complete || restDayDutyWorked;

            // Monthly-rated + Premium Pay's whole point (company policy update):
            // a Monthly-rated employee still earns this day's Holiday Pay even
            // when they didn't actually work it -- Absent, Partial, unpaid
            // Leave, or an unworked Rest Day. IsCreditedDay is reused here
            // rather than re-deriving the same rule, since it's exactly the
            // "does Basic Pay already pay this day in full" question Calculate's
            // own Monthly-rated uncreditedDays deduction asks -- and it already
            // answers false for RestDay (see its own doc comment), so an
            // unworked Rest Day falls into this branch too without a separate
            // check. A Leave(paid)/OfficialBusiness day is deliberately left
            // out: Basic Pay already pays that day in full, so adding a second
            // full day's Holiday Pay on top would double-pay it rather than
            // simply "still get paid" -- the same reasoning that already
            // excluded OfficialBusiness/paid Leave before this change, just no
            // longer extended to Absent/Partial/unpaid-Leave/Rest Day too.
            // Daily-rated is untouched -- still strictly "no work, no Holiday
            // Pay" for an unworked day, same as before this change.
            bool unworkedButStillPaid = !worked
                && employee.EmployeeType == EmployeeType.Monthly
                && !IsCreditedDay(dayStatus, dayRows[0].IsPaidLeave, employee);

            if (!worked && !unworkedButStillPaid) continue;

            holidayPay += dayRate;
            holidayWorkedDays++;

            // OT/ND "copies" only apply to an actually-worked day -- there's no
            // Overtime/Night Diff to double when nobody clocked in at all, so
            // unworkedButStillPaid stops here at the flat day component above.
            if (!worked) continue;

            // The same per-segment Overtime/Night Diff pay Calculate's own
            // per-day loop computes for the ordinary Overtime/Night Diff lines,
            // added again here so a holiday's OT/ND hours earn double what an
            // ordinary day's would -- matching the flat one-extra-day premium
            // the day component above already applies to Basic Pay. Reads the
            // exact same
            // ApplyOvertimeRatePercentage/OvertimeRatePercentageOverride/
            // NightDiffRatePercentageOverride fields, resolved against `policy`
            // the same way, so a day with a per-day rate override earns the same
            // override rate twice rather than the plain default rate the second
            // time.
            foreach (var day in dayRows)
            {
                decimal dayOvertimeHours = RoundHours(day.OvertimeHours);
                decimal dayNightDiffHours = RoundHours(day.NightDiffHours);

                if (dayOvertimeHours > 0)
                {
                    decimal otCopy = hourlyRate * dayOvertimeHours;
                    if (day.ApplyOvertimeRatePercentage)
                    {
                        decimal otRatePercentage = day.OvertimeRatePercentageOverride ?? policy.OvertimeRatePercentage;
                        otCopy *= 1 + otRatePercentage;
                    }
                    holidayPay += otCopy;
                }

                if (dayNightDiffHours > 0)
                {
                    decimal ndRatePercentage = day.NightDiffRatePercentageOverride ?? policy.NightDiffRatePercentage;
                    holidayPay += hourlyRate * dayNightDiffHours * ndRatePercentage;
                }
            }
        }

        return (Round(holidayPay), holidayWorkedDays);
    }

    /// <summary>
    /// Basic Pay for one day, per the Payroll Feature plan's table -- keyed off
    /// AttendanceSummary.Status, which is where Complete/Partial/Absent/Leave/
    /// OfficialBusiness already live (see PunchStatus) regardless of whether the
    /// underlying ScheduleType was Normal or Flexible; both report through the
    /// same five Status values, so no separate Flexible case is needed here.
    ///
    /// Flat Employee.DailyRate per workday, not hourlyRate * hours. Span is
    /// AttendanceSummary's scheduled shift length (ScheduleEntry.WorkTimeHours,
    /// set once when the schedule is created), not actual hours worked, and
    /// it's independently exported as its own column in the Attendance Excel
    /// report -- so it stays untouched as a reporting field, and just isn't
    /// used here. A day scheduled for a 9-hour shift and a day scheduled for a
    /// 10-hour shift, same DailyRate, now pay the same Basic Pay; the hours
    /// beyond/short of a workday are what Overtime/Night Diff/Undertime are for
    /// (see hourlyRate above), not Basic Pay.
    ///
    /// Partial falls through to the same 0m as Absent rather than joining
    /// Complete above: an odd-one-out unpaired punch (see PunchStatus.Partial)
    /// means the day's actual hours worked can't be reliably established, so
    /// it doesn't get credited as "1 work day" the way Complete/OfficialBusiness/
    /// paid Leave do. RestDay falls through the same way -- see IsCreditedDay's
    /// own explicit RestDay arm for why it's spelled out there rather than left
    /// to the catch-all.
    /// </summary>
    private static decimal BasicPayForDay(PunchStatus status, bool? isPaidLeave, Employee employee) =>
        IsCreditedDay(status, isPaidLeave, employee) ? employee.DailyRate : 0m;

    /// <summary>The credited-or-not classification BasicPayForDay above makes
    /// (Complete/OfficialBusiness/paid Leave credited; Absent/Partial/unpaid
    /// Leave/anything else -- including RestDay -- not), pulled out on its own
    /// so PayrollCalculator.Calculate's Monthly-rated uncreditedDays count can
    /// reuse the same day-statuses-credit-or-not logic BasicPayForDay already
    /// encodes, without going through BasicPayForDay's own decimal return --
    /// that return is keyed off Employee.DailyRate, which is 0 (unused,
    /// defaults to 0) for a Monthly-rated employee, so comparing it to 0m
    /// can't tell a credited day from an uncredited one the way it reliably
    /// can for Daily-rated.
    ///
    /// IsPaidLeave is only ever null for a Leave row saved before this field
    /// existed; every Leave day saved since always has it explicitly set (see
    /// ApplyScheduleDialog.OkButton_Click). For that legacy null case, fall
    /// back to the employee's own DefaultLeaveIsPaid (itself false/Unpaid
    /// unless the person has explicitly opted this employee into paid leave
    /// by default via the Add/Edit Employee dialog) rather than a hardcoded
    /// true, so a legacy Leave day resolves the same way this employee's
    /// other Leave days do instead of always defaulting to Paid regardless of
    /// who they are.</summary>
    private static bool IsCreditedDay(PunchStatus status, bool? isPaidLeave, Employee employee) =>
        status switch
        {
            PunchStatus.Complete => true,
            PunchStatus.OfficialBusiness => true,
            PunchStatus.Leave => isPaidLeave ?? employee.DefaultLeaveIsPaid,

            // Explicit rather than falling through to the catch-all below --
            // a Rest Day is deliberately never credited here (Basic Pay isn't
            // what prices a worked Rest Day; Rest Day Pay is -- see the
            // per-day loop in Calculate), but it's also not an absence, so
            // Calculate's Monthly-rated branch excludes it from
            // uncreditedDays entirely rather than counting it as uncredited
            // via this false. Spelling it out here avoids relying on
            // Absent/Partial/RestDay/anything added later all quietly
            // agreeing via the same fallback.
            PunchStatus.RestDay => false,

            _ => false,
        };

    /// <summary>Standard currency rounding (2 dp, away-from-zero on a tie) --
    /// applied once, to a final summed line amount, never to an intermediate
    /// per-day figure. See the "Multiplying once" comment above for why that
    /// order matters.</summary>
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Rounds one day's OvertimeHours/NightDiffHours/RemainHours (raw
    /// TimeSpan-derived doubles, e.g. 2.61666... for 2h37m) to 2 dp, same
    /// away-from-zero-on-a-tie convention as <see cref="Round"/> above --
    /// the opposite rule from Round itself: this one is deliberately applied
    /// per day, before the value is used for anything, precisely so a
    /// period's summed hours (undertimeHours/overtimeHours/nightDiffHours in
    /// <see cref="Calculate"/>) and the peso figure computed from those same
    /// per-day values always reconcile by hand -- multiplying the 2dp hours
    /// shown on a line by its rate reproduces the peso amount shown next to
    /// it. The trade-off: a period's true total worked time is very slightly
    /// obscured (each day loses up to 0.005h, ~18 seconds, to rounding) --
    /// accepted here in favor of a Payroll Summary/payslip that always foots
    /// exactly.</summary>
    private static decimal RoundHours(double hours) => Math.Round((decimal)hours, 2, MidpointRounding.AwayFromZero);
}