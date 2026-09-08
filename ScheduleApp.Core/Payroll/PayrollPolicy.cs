namespace ScheduleApp.Core.Payroll;

/// <summary>
/// Tunable rules PayrollCalculator applies on top of an attendance result --
/// the Payroll equivalent of AttendancePolicy's role for punch matching.
/// Loaded from ScheduleApp.Desktop/appsettings.json ("Payroll:Policy") with
/// these values as defaults, the same way AttendancePolicy is loaded from
/// "Attendance:Policy" -- see PayrollSettings. Every field here is global and
/// machine-wide (same as AttendancePolicy's own fields) and every one of them is
/// editable from the Settings dialog's Payroll tab, which writes them to the
/// shared config file -- see SettingsDialog/SharedConfigWriter. That wasn't
/// always true: NetPayRoundingMultiple below had the only dialog field for a
/// long stretch, and the rest were appsettings.json-only, until the rest-day
/// tier work added two more rates and made an invisible rate table hard to
/// justify.
/// </summary>
public class PayrollPolicy
{
    /// <summary>Divides into Employee.DailyRate to get HourlyRate, which
    /// PayrollCalculator uses for Basic Pay (Complete/Partial/Official
    /// Business days), Overtime, Night Diff, and Undertime alike.</summary>
    public decimal StandardHoursPerDay { get; set; } = 8m;

    /// <summary>
    /// The global default premium percentage applied on top of straight
    /// overtime pay (hours * hourly rate) when overtime eligibility AND the
    /// separate "apply rate %" toggle both resolve true for the day -- see
    /// AttendanceSummary.OvertimeRatePercentage/ApplyOvertimeRatePercentage and
    /// PayrollCalculator.Calculate. 0.25 means time-and-a-quarter (payment =
    /// hours * hourlyRate * 1.25); this field itself only ever holds the *premium*
    /// (0.25), never the full multiplier -- PayrollCalculator adds the "+1" at the
    /// point it applies it. Was OvertimePayMultiplier (a full 1.25 multiplier)
    /// before this revision; renaming to a premium percentage brings it in line
    /// with NightDiffRatePercentage's shape below, which was always a premium-only
    /// figure. IMPORTANT: appsettings.json's old
    /// "Payroll:Policy:OvertimePayMultiplier": 1.25 must be updated to
    /// "OvertimeRatePercentage": 0.25 at the same time this ships -- a value of
    /// 1.25 carried over unchanged would 5x every overtime premium.
    /// Overridable per day (ScheduleEntry.OvertimeRatePercentageOverride).
    /// </summary>
    public decimal OvertimeRatePercentage { get; set; } = 0.25m;

    /// <summary>
    /// The global default premium percentage applied on top of Basic Pay for
    /// Night Diff hours (payment = hours * hourlyRate * this value) when night
    /// diff eligibility resolves true for the day -- see
    /// AttendanceSummary.NightDiffRatePercentage and PayrollCalculator.Calculate.
    /// Unlike Overtime, there's no separate "apply rate %" toggle here -- once
    /// eligible, the rate percentage always applies. Was NightDiffPayMultiplier
    /// (name only; value/meaning unchanged). Overridable per day
    /// (ScheduleEntry.NightDiffRatePercentageOverride).
    /// </summary>
    public decimal NightDiffRatePercentage { get; set; } = 0.10m;

    /// <summary>
    /// The company-wide default premium paid on top of straight time for the first
    /// StandardHoursPerDay hours of an actually-worked Rest Day -- PH labor law's
    /// 130% rest day rate, expressed the same premium-only way
    /// OvertimeRatePercentage/NightDiffRatePercentage above are (0.30 here means a
    /// 1.30 multiplier; PayrollCalculator adds the "+1" where it applies it).
    ///
    /// Overridable per employee: Employee.RestDayWorkPremiumPercentage wins whenever
    /// it's non-null, and null -- the normal state -- means "inherit this". That
    /// nullable-means-inherit shape is why this default can be a real 0.30 rather
    /// than the 0 the per-employee field used to sit at: a rest day worked by an
    /// employee nobody has configured now pays the statutory 130% instead of
    /// straight time.
    /// </summary>
    public decimal RestDayPremiumPercentage { get; set; } = 0.30m;

    /// <summary>
    /// The additional premium for Rest Day hours beyond StandardHoursPerDay, applied
    /// **multiplicatively on top of the rest day rate**, not additively on the base
    /// hourly rate -- i.e. hourlyRate * (1 + RestDayPremiumPercentage) * (1 + this).
    /// At both defaults that's 1.30 * 1.30 = 1.69, PH labor law's 169% rest day
    /// overtime rate. Adding the two premiums instead would give 160%, which is the
    /// easiest way to get this wrong, so it's worth reading that formula twice.
    ///
    /// Global only -- no per-employee or per-day override, unlike
    /// RestDayPremiumPercentage above and OvertimeRatePercentage before it. Ordinary
    /// Overtime's own per-day overrides deliberately don't apply here either: a Rest
    /// Day never populates AttendanceSummary.OvertimeHours in the first place (see
    /// RestDayShiftCalculationStrategy), so its "overtime" is a split of WorkedHours
    /// made here in Payroll, not something the Attendance layer ever labelled as
    /// overtime for a per-day override to attach to.
    /// </summary>
    public decimal RestDayOvertimeRatePercentage { get; set; } = 0.30m;

    /// <summary>
    /// The company-wide default premium a worked Holiday's day component pays on top
    /// of Basic Pay's own 100% -- see PayrollCalculator.CalculateHolidayPay. 1.00
    /// means one full extra day (200% total when worked), PH labor law's
    /// worked-regular-holiday rate -- this was a hardcoded "+1 day" before this field
    /// existed, so 1.00 reproduces that exact behavior for anyone who doesn't
    /// deliberately change it. Same shape as RestDayPremiumPercentage above: a plain
    /// premium (not the full multiplier), and also applies to the Monthly-rated-only
    /// "unworked Holiday, still paid" case, not just the worked one.
    ///
    /// Overridable per employee: Employee.HolidayPremiumPercentage wins whenever it's
    /// non-null, and null -- the normal state -- means "inherit this".
    /// </summary>
    public decimal HolidayPremiumPercentage { get; set; } = 1.00m;

    /// <summary>
    /// Rounds each employee's PayrollResult.NetPay to the nearest multiple of this
    /// value, the same way Excel's MROUND(number, multiple) works -- e.g. 1 rounds
    /// Net Pay to the nearest whole unit, 5 to the nearest 5, 0.25 to the nearest
    /// quarter. Applied once, to the final TotalGrossPay − TotalDeductions figure
    /// -- see PayrollResult.NetPay -- never to any of the individual Gross Pay/
    /// Deductions lines that feed into it, so a Payroll Summary or Excel roster's
    /// own line items always foot exactly to TotalGrossPay/TotalDeductions even
    /// when the Net Pay figure shown next to them has been rounded beyond that.
    ///
    /// Default 0.01 leaves Net Pay at its normal 2-decimal precision -- every
    /// PayrollLineItem.Amount and PayrollAdjustment.Amount that feed TotalGrossPay/
    /// TotalDeductions is already a whole-2-decimal value, so their difference is
    /// already a multiple of 0.01, making 0.01 an effective no-op rather than a
    /// real rounding step. A value of 0 or less is treated the same way (no
    /// rounding applied) rather than propagating Excel's own #NUM! error for a
    /// zero/negative multiple into PayrollResult -- see PayrollResult.NetPay's
    /// MRound helper.
    ///
    /// Global and machine-wide, same as every AttendancePolicy field -- editable
    /// from the Settings dialog (a textbox under "Use live formulas in exported
    /// Excel summaries") and saved to the same shared config file, rather than
    /// per-employee or per-period.
    /// </summary>
    public decimal NetPayRoundingMultiple { get; set; } = 0.01m;

    /// <summary>
    /// A complete copy, for a caller that needs to change one or two fields and
    /// carry the rest through untouched -- SettingsDialog.SaveButton_Click being
    /// the one that matters (it edits only what its own boxes cover, and every
    /// other field has to survive the round trip to the shared config file).
    ///
    /// Exists because that caller used to rebuild this class with an object
    /// initializer listing each field by hand, which silently dropped any field
    /// added to this class afterwards back to its default -- the bug that shipped
    /// with RestDayPremiumPercentage/RestDayOvertimeRatePercentage and went
    /// unnoticed precisely because their defaults matched what the initializer
    /// left behind. Cloning can't develop that gap: a field added below is copied
    /// whether or not anyone remembers this method exists.
    ///
    /// MemberwiseClone is a full copy here rather than a shallow one that aliases
    /// something, since every property on this class is a value type (decimal). Keep
    /// it that way, or this needs revisiting.
    /// </summary>
    public PayrollPolicy Clone() => (PayrollPolicy)MemberwiseClone();
}