namespace ScheduleApp.Core.Payroll;

/// <summary>
/// Tunable rules PayrollCalculator applies on top of an attendance result --
/// the Payroll equivalent of AttendancePolicy's role for punch matching.
/// Loaded from ScheduleApp.Desktop/appsettings.json ("Payroll:Policy") with
/// these values as defaults, the same way AttendancePolicy is loaded from
/// "Attendance:Policy" -- see PayrollSettings. StandardHoursPerDay/
/// OvertimeRatePercentage/NightDiffRatePercentage are not yet editable from
/// the Settings dialog (AttendancePolicy's own dialog section came well after
/// AttendancePolicy itself did); until a fuller section exists for those, a
/// person who needs different values for them edits appsettings.json
/// directly. NetPayRoundingMultiple below is the one exception -- it does
/// have a Settings dialog field (a global, machine-wide setting, same as
/// AttendancePolicy's own fields) -- see SettingsDialog/SharedConfigWriter.
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
}