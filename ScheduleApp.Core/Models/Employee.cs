using ScheduleApp.Core.Enums;

namespace ScheduleApp.Core.Models;

public class Employee
{
    public int Id { get; set; }

    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// The employee's ID on the ZKTeco biometric device -- assigned there (by
    /// fingerprint/badge enrollment) before this employee can be added to ScheduleApp at
    /// all, not something ScheduleApp itself generates. Required at Add Employee time (see
    /// IScheduleRepository.AddEmployeeAsync's own doc comment) -- there's no "add now, assign
    /// a Pin later" state for an Employee row to be in; that's the whole point of requiring
    /// it up front rather than leaving it optional the way it briefly was. The one and only
    /// key ScheduleEntry.EmployeeId/AttendanceLog.EmployeeId/ManualAttendanceLog.EmployeeId/
    /// PayrollAdjustment.EmployeeId all match, and (as of restoring the real FK -- see
    /// ScheduleDbContext's own remarks on ScheduleEntry) the alternate key
    /// ScheduleEntry.Employee's own relationship targets directly.
    /// </summary>
    public int Pin { get; set; }

    /// <summary>Null when the employee hasn't been assigned to a department yet.</summary>
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>
    /// Whether this employee is blacklisted -- e.g. someone let go or no longer
    /// employed, but kept in the database rather than deleted so their historical
    /// schedule/attendance/payroll records stay intact. Defaults to false (the
    /// common case). A blacklisted employee is excluded from the Schedule and
    /// Attendance trees (see EmployeeTreeSearchFilter) and from active-employee
    /// pickers elsewhere in the app (report scope, payroll rosters/wizard), but
    /// still shows up (grayed out) on the Employees page so they can be
    /// unblacklisted later if needed.
    /// </summary>
    public bool IsBlacklisted { get; set; }

    /// <summary>
    /// Whether this employee's computed hours should ever include overtime pay --
    /// e.g. false for managerial/supervisory staff, who under PH Labor Code Art. 82
    /// aren't entitled to overtime pay even though they still clock in and out
    /// normally. Defaults to true (the common case). Read by
    /// SingleWindowShiftCalculationStrategy/FlexibleShiftCalculationStrategy at the
    /// point they'd otherwise set AttendanceSummary.OvertimeHours/OvertimeDuration -- worked
    /// hours, lateness, etc. are still computed as normal; only the overtime figure
    /// is suppressed to zero when this is false. Independent of
    /// QualifiesForNightDiff -- an employee can be exempt from one and not the other.
    /// </summary>
    public bool QualifiesForOvertime { get; set; } = true;

    /// <summary>
    /// Whether this employee's computed hours should ever include night
    /// differential pay -- see QualifiesForOvertime for the same PH Labor Code
    /// Art. 82 exemption and how the two flags are applied independently.
    /// Defaults to true (the common case). Read by
    /// SingleWindowShiftCalculationStrategy/FlexibleShiftCalculationStrategy at the
    /// point they'd otherwise set AttendanceSummary.NightDiffHours/NightDiffDuration.
    /// </summary>
    public bool QualifiesForNightDiff { get; set; } = true;

    /// <summary>
    /// The employee's per-employee default answer to "does the overtime rate
    /// percentage premium actually apply, on top of straight overtime pay?" --
    /// a third state independent of QualifiesForOvertime above (see
    /// ScheduleApp.Core.Payroll.PayrollPolicy.OvertimeRatePercentage and
    /// ScheduleEntry.ApplyOvertimeRatePercentageOverride for the full picture).
    /// An employee can be eligible for overtime (QualifiesForOvertime == true)
    /// while this is false -- e.g. paid overtime at straight hourly rate under a
    /// temporary company policy, with no premium, pending a transition to the
    /// labor-law-compliant premium -- which is different from not being eligible
    /// for overtime at all (e.g. a manager exempt under PH Labor Code Art. 82,
    /// where QualifiesForOvertime itself is false and this flag is moot). Defaults
    /// to true (the common, labor-law-compliant case) so an employee newly opted
    /// into overtime eligibility gets the real premium unless explicitly set
    /// otherwise. Night Diff has no equivalent second toggle -- once eligible for
    /// night diff, its rate percentage always applies.
    /// </summary>
    public bool ApplyOvertimeRatePercentageByDefault { get; set; } = true;

    /// <summary>
    /// Which of DailyRate/MonthlyRate below ScheduleApp.Payroll.PayrollCalculator
    /// actually reads for this employee, and which Basic Pay computation path
    /// applies (see PayrollCalculator.WorkDays/BasicPayForDay) -- a Daily-rated
    /// employee is paid per completed/credited day the same way every employee
    /// works today, while a Monthly-rated employee is paid a semi-monthly share
    /// of MonthlyRate regardless of how many days fall in the period, with
    /// uncredited days (Absent/Partial) deducted from that share rather than
    /// zeroing individual days out. Defaults to Daily -- every employee already
    /// in the database keeps computing exactly as before until someone opts them
    /// into Monthly by hand via the Add/Edit Employee dialog.
    /// </summary>
    public EmployeeType EmployeeType { get; set; } = EmployeeType.Daily;

    /// <summary>
    /// The employee's daily rate in pesos -- what ScheduleApp.Core.Payroll.
    /// PayrollCalculator divides by PayrollPolicy.StandardHoursPerDay to get
    /// HourlyRate (Basic Pay/Overtime/Night Diff/Undertime all derive from that),
    /// and pays outright for a paid Leave day (see ScheduleEntry.IsPaidLeave).
    /// Defaults to 0, same spirit as QualifiesForOvertime/QualifiesForNightDiff
    /// defaulting to the safe common case -- an employee whose DailyRate is still
    /// 0 just computes to ₱0 pay everywhere rather than erroring, until someone
    /// sets it via the Add/Edit Employee dialog. Only meaningful when
    /// EmployeeType == Daily -- ignored for a Monthly-rated employee, who is
    /// paid from MonthlyRate below instead.
    /// </summary>
    public decimal DailyRate { get; set; }

    /// <summary>
    /// The employee's monthly rate in pesos -- what PayrollCalculator divides by
    /// its nominal-15 semi-monthly divisor to get a period's Basic Pay share for
    /// a Monthly-rated employee, the MonthlyRate equivalent of DailyRate above.
    /// Only meaningful when EmployeeType == Monthly -- ignored (and left at its
    /// default) for a Daily-rated employee, who is paid from DailyRate instead.
    /// Defaults to 0, same "safe, never-blocking, computes to ₱0 rather than
    /// erroring" spirit as DailyRate -- a newly-created Monthly employee just
    /// pays ₱0 until someone sets this via the Add/Edit Employee dialog.
    /// </summary>
    public decimal MonthlyRate { get; set; }

    /// <summary>
    /// This employee's own default search-window buffer (in hours) for how far
    /// before the scheduled clock-in time a punch still counts as clocking in
    /// for a Normal shift -- read by SingleWindowShiftCalculationStrategy, and
    /// by RestDayShiftCalculationStrategy for a RestDay entry that has a
    /// schedule set on it too (both reuse the exact same buffer-window
    /// matching). Sits between ScheduleEntry.ClockInBufferBeforeHours and
    /// AttendancePolicy.ClockInBufferBefore in priority: a specific day's own
    /// override (when set) always wins; failing that, this employee-level
    /// default (when set) applies to every day of theirs that doesn't
    /// override it; failing that too, the app-wide policy default applies --
    /// see AttendancePolicy.ClockInBufferBefore's own doc comment for the
    /// first tier, and NormalBufferResolver for where all three are actually
    /// resolved together. Defaults to null (the common case), same
    /// "safe, never-blocking" spirit as every other nullable default on this
    /// class -- an employee with this unset just inherits the policy default
    /// exactly as before this feature existed.
    ///
    /// This is also what ApplyScheduleDialog shows as the grayed-out starting
    /// value in ClockInBufferBeforeBox when scheduling a day for this
    /// employee (falling back to the policy default when this is null) --
    /// leaving that box untouched still saves a plain per-day null, so this
    /// value keeps applying automatically even if it's changed later; typing
    /// over it there sets a one-day exception without touching this
    /// employee-level default at all.
    /// </summary>
    public double? ClockInBufferBeforeHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferBeforeHours"/>, but for how far
    /// after the scheduled clock-in time a punch still counts -- the
    /// employee-level default for AttendancePolicy.ClockInBufferAfter/
    /// ScheduleEntry.ClockInBufferAfterHours.</summary>
    public double? ClockInBufferAfterHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferBeforeHours"/>, but for how far
    /// before the scheduled clock-out time a punch still counts -- the
    /// employee-level default for AttendancePolicy.ClockOutBufferBefore/
    /// ScheduleEntry.ClockOutBufferBeforeHours.</summary>
    public double? ClockOutBufferBeforeHours { get; set; }

    /// <summary>Same as <see cref="ClockInBufferBeforeHours"/>, but for how far
    /// after the scheduled clock-out time a punch still counts -- the
    /// employee-level default for AttendancePolicy.ClockOutBufferAfter/
    /// ScheduleEntry.ClockOutBufferAfterHours.</summary>
    public double? ClockOutBufferAfterHours { get; set; }

    /// <summary>
    /// This employee's own default shift length, in hours -- pre-filled into
    /// ApplyScheduleDialog's Work Time field (ScheduleEntry.WorkTimeHours) for a
    /// brand-new entry, in place of AttendanceSettings.DefaultWorkTimeHours, whenever
    /// every employee selected for that entry shares the same resolved value. Unlike
    /// ClockInBufferBeforeHours/etc. above, this is a one-time starting suggestion,
    /// not a genuine three-tier runtime resolution -- ScheduleEntry.WorkTimeHours is
    /// a required, concrete value once a day is saved (TimeOut is derived from
    /// TimeIn + WorkTimeHours), so there's no per-day "blank, inheriting" state for
    /// this to sit above the way the buffers do; it only ever shapes what a person
    /// sees before they've typed anything of their own.
    ///
    /// Null (the default) means "no employee-level suggestion, start from
    /// AttendanceSettings.DefaultWorkTimeHours instead," same nullable-means-inherit
    /// convention as the buffer fields. Meant for an employee whose normal day is
    /// genuinely shorter (or longer) than the company's own default -- e.g. paired
    /// with <see cref="ExemptFromUndertimeDeduction"/> below for someone whose pay
    /// doesn't depend on reaching it, so scheduling them doesn't mean re-typing a
    /// shorter number by hand every time.
    /// </summary>
    public decimal? DefaultWorkTimeHours { get; set; }

    /// <summary>
    /// When true, this employee's Undertime deduction (hourlyRate * shortfall hours,
    /// see ScheduleApp.Payroll.PayrollCalculator) never reduces Total Deductions/Net
    /// Pay, no matter how far short of the day's scheduled Work Time they ran --
    /// they're always paid their full DailyRate/effectiveDailyRate for a credited
    /// day. The Undertime figure itself still shows on the Payroll Summary/payslip
    /// for reference (same as a manually "disregarded" period -- see
    /// PayrollLineItem.Waived), it's just never subtracted, and PayrollSummaryView's
    /// own per-period Exclude/Include toggle is hidden for this employee's Undertime
    /// line rather than offered as a no-op (see PayrollLineItem.SupportsWaiver).
    ///
    /// Hours worked *beyond* the day's scheduled Work Time are unaffected -- still
    /// Overtime, exactly as for any other employee (see OvertimeHours in
    /// PayrollCalculator.Calculate, which already prices off however many hours a
    /// day was actually scheduled for, independent of this flag). This is for the
    /// employees whose day doesn't need to be *completed* to earn a full day's pay,
    /// only exceeded to earn extra -- see <see cref="DefaultWorkTimeHours"/> above
    /// for giving such an employee a shorter starting shift length to schedule
    /// against. Defaults to false: an employee has to be explicitly opted into this,
    /// the same opt-in-benefit default QualifiesForRestDayPay/QualifiesForPremiumPay
    /// above use, rather than every employee silently losing their Undertime
    /// deduction the moment this field existed.
    /// </summary>
    public bool ExemptFromUndertimeDeduction { get; set; }

    /// <summary>
    /// This employee's own override of the premium paid on top of straight pay for
    /// the first PayrollPolicy.StandardHoursPerDay hours of an actually-worked Rest
    /// Day (ScheduleType.RestDay with a completed punch/schedule, as opposed to an
    /// unworked Rest Day, which pays nothing extra) -- read by
    /// ScheduleApp.Payroll.PayrollCalculator the same way
    /// PayrollPolicy.OvertimeRatePercentage/NightDiffRatePercentage are: this field
    /// holds only the *premium* (e.g. 0.30 for a 30% Rest Day premium), never the
    /// full multiplier, with PayrollCalculator adding the "+1" at the point it
    /// applies it. Hours beyond that eighth are priced separately, and this premium
    /// compounds into that figure too -- see
    /// PayrollPolicy.RestDayOvertimeRatePercentage.
    ///
    /// Null -- the normal state -- means "inherit PayrollPolicy.RestDayPremiumPercentage"
    /// (0.30 by default, PH labor law's 130% rest day rate), the same
    /// nullable-means-inherit shape ClockInBufferBeforeHours and friends above
    /// already use for the buffer defaults, and the same tier
    /// ScheduleEntry.OvertimeRatePercentageOverride sits at one layer further down.
    /// It was a non-nullable decimal defaulting to 0 before the rest-day tier work:
    /// that meant a Rest Day worked by an employee nobody had configured silently
    /// paid straight time, which is why the migration that made this nullable also
    /// rewrote every stored 0 to null (see AddRestDayPremiumOverride) -- a 0 there
    /// meant "never configured", not "deliberately zero".
    ///
    /// Applies to both EmployeeType values -- unlike DailyRate/MonthlyRate above,
    /// Rest Day work premium isn't gated behind Pay Type, since a Daily-rated
    /// employee can just as legitimately be asked to work a Rest Day as a
    /// Monthly-rated one.
    /// </summary>
    public decimal? RestDayWorkPremiumPercentage { get; set; }

    /// <summary>
    /// Whether this employee can ever earn Rest Day Pay -- i.e. whether
    /// ApplyScheduleDialog's "Scheduled duty" checkbox (RestDayDutyCheckBox) can be
    /// checked for this employee's Rest Day entries at all, and whether
    /// ScheduleApp.Payroll.PayrollCalculator includes the "Rest Day Pay" line in
    /// ComputedGrossPay for this employee. Defaults to false -- unlike
    /// QualifiesForOvertime/QualifiesForNightDiff above (whose safe default is the
    /// common "yes" case), Rest Day Pay is an opt-in employee benefit, not something
    /// every employee is assumed to have -- so a newly-added employee needs this
    /// explicitly turned on via the Add/Edit Employee dialog before Rest Day duty
    /// becomes selectable for them at all. Independent of
    /// RestDayWorkPremiumPercentage below, the same way QualifiesForOvertime is
    /// independent of ApplyOvertimeRatePercentageByDefault -- this flag is the
    /// eligibility gate, RestDayWorkPremiumPercentage only matters once this is true.
    /// </summary>
    public bool QualifiesForRestDayPay { get; set; }

    /// <summary>
    /// This employee's default answer to "is a Leave day paid?" -- true = Paid,
    /// false = Unpaid (the default -- an employee has to be explicitly marked
    /// eligible for paid leave rather than assumed to be, the reverse of
    /// QualifiesForOvertime/QualifiesForNightDiff's safe-common-case defaults
    /// above). Two things read this:
    /// 1. ApplyScheduleDialog pre-selects its Paid/Unpaid radio from this value
    ///    for a brand-new Leave day (single-employee only -- see the dialog's own
    ///    comment for the multi-employee case), so the common per-employee choice
    ///    doesn't have to be re-picked by hand every time; the person can still
    ///    override it per day, and that per-day choice (ScheduleEntry.IsPaidLeave)
    ///    always wins once set.
    /// 2. ScheduleApp.Payroll.PayrollCalculator falls back to this for a Leave row
    ///    whose ScheduleEntry.IsPaidLeave is null -- i.e. a row saved before that
    ///    field existed -- instead of the previous hardcoded "treat as Paid",
    ///    so a legacy Leave day resolves the same way this employee's other Leave
    ///    days do rather than always defaulting to Paid regardless of who they are.
    /// </summary>
    public bool DefaultLeaveIsPaid { get; set; }

    /// <summary>
    /// This employee's usual per-period SSS contribution, in pesos -- read by
    /// IPayrollComputationService the first time a given payroll period is opened for this
    /// employee to seed that period's SSS PayrollAdjustment row (see
    /// PayrollComputationService.SeedOrReseedContributionsAsync), the same "pre-fill once,
    /// then it's a completely ordinary editable/persisted row from then on" spirit
    /// as DefaultLeaveIsPaid pre-selecting a brand-new Leave day's Paid/Unpaid
    /// radio -- just committed at period-load time instead of day-save time, since
    /// there's no separate "create" step for a payroll period the way there is for
    /// a schedule day. A period that already has its own SSS row (typed by hand,
    /// or seeded by an earlier visit) is never touched by this -- it only fills a
    /// gap, never overwrites. Defaults to 0, same "safe, never-blocking" spirit as
    /// DailyRate -- 0 means nothing to pre-fill, so a period simply starts with no
    /// SSS row until someone sets a value, exactly like today.
    /// </summary>
    public decimal DefaultSss { get; set; }

    /// <summary>Same purpose and read path as DefaultSss above, for PhilHealth.</summary>
    public decimal DefaultPhilHealth { get; set; }

    /// <summary>Same purpose and read path as DefaultSss above, for Pag-IBIG.</summary>
    public decimal DefaultPagIbig { get; set; }

    /// <summary>
    /// This employee's own override of the premium a worked Holiday's day component pays
    /// on top of Basic Pay's own 100% -- read by ScheduleApp.Payroll.PayrollCalculator's
    /// CalculateHolidayPay the same way PayrollPolicy.OvertimeRatePercentage/
    /// NightDiffRatePercentage are: this field holds only the *premium* (e.g. 1.00 for one
    /// full extra day, 200% total when worked, PH labor law's worked-regular-holiday
    /// rate), not the full multiplier. Same shape applies to the Monthly-rated-only
    /// "unworked but still paid" case -- both add dayRate * this premium, not just dayRate.
    ///
    /// Null -- the normal state -- means "inherit PayrollPolicy.HolidayPremiumPercentage"
    /// (1.00 by default), the same nullable-means-inherit shape RestDayWorkPremiumPercentage
    /// above already uses. Unlike that field's own history, this one was never a
    /// non-nullable 0-defaulting column to begin with -- it's new alongside the policy
    /// default it inherits from, so there's no pre-existing "0 means never configured"
    /// data to migrate around.
    ///
    /// Only takes effect while QualifiesForPremiumPay below is true -- same "eligibility
    /// flag gates whether the rate matters at all" relationship QualifiesForRestDayPay has
    /// with RestDayWorkPremiumPercentage. Applies to both EmployeeType values, same
    /// reasoning as that field too: a Daily-rated employee's worked holiday is priced off
    /// DailyRate, a Monthly-rated one's off effectiveDailyRate, but the premium multiplies
    /// either the same way.
    /// </summary>
    public decimal? HolidayPremiumPercentage { get; set; }

    /// <summary>
    /// Whether this employee can ever be paid the Premium Pay adjustment line below --
    /// i.e. whether ScheduleApp.Desktop.Services.IPayrollComputationService seeds a
    /// PayrollAdjustmentType.PremiumHoliday row for this employee/period at all, and
    /// whether ScheduleApp.Payroll.PayrollCalculator includes that adjustment group in
    /// GrossPayAdjustmentGroups for this employee. Defaults to false, same "opt-in
    /// benefit, not assumed" reasoning as QualifiesForRestDayPay above -- a newly-added
    /// employee needs this explicitly turned on via the Add/Edit Employee dialog before
    /// the Premium Pay card appears on the Payroll Summary view or a printed payslip for
    /// them at all. Independent of DefaultPremiumPay below, the same relationship
    /// QualifiesForRestDayPay has with RestDayWorkPremiumPercentage above.
    /// </summary>
    public bool QualifiesForPremiumPay { get; set; }

    /// <summary>
    /// This employee's usual per-period Premium Pay, in pesos -- read by
    /// IPayrollComputationService the moment a given payroll period is opened (or reopened) for
    /// this employee to seed/correct that period's Premium Pay PayrollAdjustment row (see
    /// PayrollComputationService.SeedOrReseedContributionsAsync), the same "pre-fill, then it's
    /// a completely ordinary editable/persisted row from then on" spirit as DefaultSss/
    /// DefaultPhilHealth/DefaultPagIbig above.
    ///
    /// Differs from those three in two ways: it applies to every payroll period rather than
    /// only whichever cutoff a statutory contribution happens to withhold on (Premium Pay isn't
    /// tied to a monthly cutoff), and a period is seeded an explicit Amount = 0.00 row even when
    /// this default is itself 0/unset, rather than skipping the row entirely -- there's no
    /// "not enrolled" state for Premium Pay/Allowance/Cash Advance the way there legitimately is
    /// for SSS/PhilHealth/Pag-IBIG, so an untouched box should always read as an explicit,
    /// deliberate ₱0.00 rather than looking like a period nobody's opened yet (see
    /// PayrollAdjustmentTypeLabel.BlankAmountMeansZero). Defaults to 0, same "safe,
    /// never-blocking" spirit as DailyRate/DefaultSss -- 0 just means nothing to carry forward
    /// beyond that explicit zero, until someone sets a value via the Add/Edit Employee dialog.
    /// </summary>
    public decimal DefaultPremiumPay { get; set; }

    /// <summary>Same purpose and read path as DefaultPremiumPay above, for Allowance.</summary>
    public decimal DefaultAllowance { get; set; }

    /// <summary>Same purpose and read path as DefaultPremiumPay above, for Cash Advance.</summary>
    public decimal DefaultCashAdvance { get; set; }

    /// <summary>A real EF navigation again (see ScheduleDbContext's own HasOne/
    /// HasPrincipalKey config on ScheduleEntry.Employee for the relationship this is
    /// the other side of) -- populated by Include(e => e.ScheduleEntries) wherever a
    /// query needs it, same as before Employee.Pin briefly went through a stretch of
    /// being optional. ExcelScheduleImporter still also uses this as a plain settable
    /// list on the transient, not-yet-persisted Employee objects it builds while
    /// parsing a workbook (Pin present there too now, always -- see Employee.Pin's
    /// own doc comment) for ScheduleRepository.ImportAsync to read back out; that use
    /// was never about the EF navigation itself, just a convenient place to stage
    /// parsed rows. Empty (not null) on an Employee nothing has populated it for --
    /// same "empty means nothing loaded this, not nothing exists" convention as
    /// every other collection here.</summary>
    public ICollection<ScheduleEntry> ScheduleEntries { get; set; } = new List<ScheduleEntry>();

    /// <summary>Matches the "LastName; FirstName" convention used in the source workbook.</summary>
    public string DisplayName => $"{LastName}, {FirstName}";
}