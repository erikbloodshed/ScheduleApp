using System.Globalization;
using System.Reactive.Linq;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>An employee's details as the Employee dialog settles them -- what
/// IScheduleRepository.AddEmployeeAsync/UpdateEmployeeAsync store. See the matching Employee
/// properties for what each feeds into.</summary>
public sealed record EmployeeDetails(
    int Pin,
    string LastName,
    string FirstName,
    int? DepartmentId,
    bool QualifiesForOvertime,
    bool QualifiesForNightDiff,
    bool QualifiesForRestDayPay,
    bool QualifiesForPremiumPay,
    bool ExemptFromUndertimeDeduction,
    bool ApplyOvertimeRatePercentageByDefault,
    bool DefaultLeaveIsPaid,
    EmployeeType EmployeeType,
    decimal DailyRate,
    decimal MonthlyRate,
    decimal? RestDayWorkPremiumPercentage,
    decimal? HolidayPremiumPercentage,
    decimal? DefaultWorkTimeHours,
    decimal DefaultSss,
    decimal DefaultPhilHealth,
    decimal DefaultPagIbig,
    decimal DefaultPremiumPay,
    decimal DefaultAllowance,
    decimal DefaultCashAdvance,
    double? ClockInBufferBeforeHours,
    double? ClockInBufferAfterHours,
    double? ClockOutBufferBeforeHours,
    double? ClockOutBufferAfterHours);

/// <summary>
/// The Add/Edit Employee dialog: every field of one employee, in tabs (Info / Attendance /
/// Payroll). Self-validating on OK -- <see cref="AcceptedDetails"/> is only settled once the
/// name, department, and a free Employee ID are in place.
///
/// Three kinds of blank: the money boxes read blank as 0 (never blocks saving); the
/// premium-percent, default-work-time and clock-buffer boxes read blank as null, meaning "no
/// employee-level override, inherit the company default" -- each shows that default as its
/// placeholder; and the Employee ID is required.
/// </summary>
public partial class EmployeeEditorViewModel : ReactiveViewModel
{
    private readonly IReadOnlySet<int> _takenPins;

    /// <summary>Kept only so OK can carry forward the rate of the pay type that *isn't*
    /// active -- see AcceptAsync.</summary>
    private readonly Employee? _existing;

    /// <param name="departments">Real departments to offer in the picker.</param>
    /// <param name="preselectedDepartmentId">Department to preselect when adding.</param>
    /// <param name="payrollPolicy">The company's current Payroll settings -- only their
    /// Rest Day and Holiday premiums, shown as the override boxes' placeholders.</param>
    /// <param name="defaultWorkTimeHours">The company's current work-time default, shown as
    /// the default-work-time box's placeholder.</param>
    /// <param name="existing">The employee to edit, or null to add one.</param>
    /// <param name="takenPins">Employee IDs used by OTHER employees -- typing one blocks OK,
    /// so nothing is lost and another ID can just be picked.</param>
    public EmployeeEditorViewModel(
        IReadOnlyList<Department> departments, int? preselectedDepartmentId, PayrollPolicy payrollPolicy,
        double defaultWorkTimeHours, Employee? existing = null, IReadOnlySet<int>? takenPins = null)
    {
        _takenPins = takenPins ?? new HashSet<int>();
        _existing = existing;
        Departments = departments;

        Title = existing is null ? "New Employee" : "Edit Employee";
        RestDayPremiumPlaceholder = PercentText(payrollPolicy.RestDayPremiumPercentage);
        HolidayPremiumPlaceholder = PercentText(payrollPolicy.HolidayPremiumPercentage);
        DefaultWorkTimePlaceholder = defaultWorkTimeHours.ToString("0.##", CultureInfo.CurrentCulture);

        if (existing is null)
        {
            Department = preselectedDepartmentId is int id
                ? departments.FirstOrDefault(d => d.Id == id)
                : departments.Count > 0 ? departments[0] : null;
            QualifiesForOvertime = true;
            ApplyOvertimeRatePercentageByDefault = true;
            QualifiesForNightDiff = true;
            DailyRate = MonthlyRate = 0m;
            DefaultSss = DefaultPhilHealth = DefaultPagIbig = 0m;
            DefaultPremiumPay = DefaultAllowance = DefaultCashAdvance = 0m;
        }
        else
        {
            Pin = existing.Pin;
            LastName = existing.LastName;
            FirstName = existing.FirstName;
            IsUnassigned = existing.DepartmentId is null;
            Department = departments.FirstOrDefault(d => d.Id == existing.DepartmentId);
            QualifiesForOvertime = existing.QualifiesForOvertime;
            QualifiesForNightDiff = existing.QualifiesForNightDiff;
            QualifiesForRestDayPay = existing.QualifiesForRestDayPay;
            QualifiesForPremiumPay = existing.QualifiesForPremiumPay;
            ApplyOvertimeRatePercentageByDefault = existing.ApplyOvertimeRatePercentageByDefault;
            ExemptFromUndertimeDeduction = existing.ExemptFromUndertimeDeduction;
            DefaultLeaveIsPaid = existing.DefaultLeaveIsPaid;
            IsMonthly = existing.EmployeeType == EmployeeType.Monthly;
            DailyRate = existing.DailyRate;
            MonthlyRate = existing.MonthlyRate;
            RestDayPremiumPercent = ToPercent(existing.RestDayWorkPremiumPercentage);
            HolidayPremiumPercent = ToPercent(existing.HolidayPremiumPercentage);
            DefaultWorkTimeHours = (double?)existing.DefaultWorkTimeHours;
            DefaultSss = existing.DefaultSss;
            DefaultPhilHealth = existing.DefaultPhilHealth;
            DefaultPagIbig = existing.DefaultPagIbig;
            DefaultPremiumPay = existing.DefaultPremiumPay;
            DefaultAllowance = existing.DefaultAllowance;
            DefaultCashAdvance = existing.DefaultCashAdvance;
            ClockInBufferBeforeHours = existing.ClockInBufferBeforeHours;
            ClockInBufferAfterHours = existing.ClockInBufferAfterHours;
            ClockOutBufferBeforeHours = existing.ClockOutBufferBeforeHours;
            ClockOutBufferAfterHours = existing.ClockOutBufferAfterHours;
        }

        _rateLabelHelper = this.WhenAnyValue(x => x.IsMonthly)
            .Select(monthly => monthly ? "Monthly rate" : "Daily rate")
            .ToProperty(this, x => x.RateLabel);
    }

    public string Title { get; }

    public IReadOnlyList<Department> Departments { get; }

    /// <summary>What a blank Rest Day premium inherits, as "30 %".</summary>
    public string RestDayPremiumPlaceholder { get; }

    public string HolidayPremiumPlaceholder { get; }

    public string DefaultWorkTimePlaceholder { get; }

    /// <summary>The Employee ID -- required (see Employee.Pin).</summary>
    [Reactive]
    public partial long? Pin { get; set; }

    [Reactive]
    public partial string LastName { get; set; } = string.Empty;

    [Reactive]
    public partial string FirstName { get; set; } = string.Empty;

    [Reactive]
    public partial Department? Department { get; set; }

    /// <summary>Leave the employee out of every department for now.</summary>
    [Reactive]
    public partial bool IsUnassigned { get; set; }

    [Reactive]
    public partial bool QualifiesForOvertime { get; set; }

    /// <summary>Whether the overtime premium applies once eligible -- separate from
    /// QualifiesForOvertime.</summary>
    [Reactive]
    public partial bool ApplyOvertimeRatePercentageByDefault { get; set; }

    [Reactive]
    public partial bool QualifiesForNightDiff { get; set; }

    [Reactive]
    public partial bool ExemptFromUndertimeDeduction { get; set; }

    /// <summary>Gates the Rest Day premium box -- a premium box for someone who can't earn it
    /// would be a dead end. A value hidden this way still saves.</summary>
    [Reactive]
    public partial bool QualifiesForRestDayPay { get; set; }

    /// <summary>Gates the Premium Pay amount and the Holiday premium box, the same way.</summary>
    [Reactive]
    public partial bool QualifiesForPremiumPay { get; set; }

    [Reactive]
    public partial bool DefaultLeaveIsPaid { get; set; }

    /// <summary>Monthly-rated rather than Daily -- decides which rate box shows and
    /// saves.</summary>
    [Reactive]
    public partial bool IsMonthly { get; set; }

    [ObservableAsProperty(InitialValue = "Daily rate")]
    public partial string RateLabel { get; }

    [Reactive]
    public partial decimal? DailyRate { get; set; }

    [Reactive]
    public partial decimal? MonthlyRate { get; set; }

    /// <summary>As a percent (30 for 30%); blank inherits the company's.</summary>
    [Reactive]
    public partial double? RestDayPremiumPercent { get; set; }

    [Reactive]
    public partial double? HolidayPremiumPercent { get; set; }

    [Reactive]
    public partial double? DefaultWorkTimeHours { get; set; }

    [Reactive]
    public partial decimal? DefaultSss { get; set; }

    [Reactive]
    public partial decimal? DefaultPhilHealth { get; set; }

    [Reactive]
    public partial decimal? DefaultPagIbig { get; set; }

    [Reactive]
    public partial decimal? DefaultPremiumPay { get; set; }

    [Reactive]
    public partial decimal? DefaultAllowance { get; set; }

    [Reactive]
    public partial decimal? DefaultCashAdvance { get; set; }

    [Reactive]
    public partial double? ClockInBufferBeforeHours { get; set; }

    [Reactive]
    public partial double? ClockInBufferAfterHours { get; set; }

    [Reactive]
    public partial double? ClockOutBufferBeforeHours { get; set; }

    [Reactive]
    public partial double? ClockOutBufferAfterHours { get; set; }

    /// <summary>What OK settled on -- null until it succeeds.</summary>
    public EmployeeDetails? AcceptedDetails { get; private set; }

    /// <summary>OK: checks the name, the department and the Employee ID, then settles
    /// <see cref="AcceptedDetails"/>.</summary>
    [ReactiveCommand]
    private async Task<bool> AcceptAsync()
    {
        if (string.IsNullOrWhiteSpace(LastName) || string.IsNullOrWhiteSpace(FirstName))
            return await RejectAsync("First and last name are required.", "Required");
        if (!IsUnassigned && Department is null)
            return await RejectAsync("Select a department, or check \"Leave unassigned for now\".", "Required");
        if (Pin is not { } typedPin)
            return await RejectAsync("Employee ID is required.", "Required");

        var pin = (int)typedPin;
        if (_takenPins.Contains(pin))
            return await RejectAsync($"Employee ID {pin} is already assigned to another employee. Choose a different ID.", "Duplicate Employee ID");

        // Only the active pay type's rate is read; the other's saved value is carried forward
        // (0 for a new employee) -- payroll ignores it for this pay type, and switching back
        // and forth before OK never discards a rate already typed.
        var type = IsMonthly ? EmployeeType.Monthly : EmployeeType.Daily;
        var dailyRate = IsMonthly ? _existing?.DailyRate ?? 0m : DailyRate ?? 0m;
        var monthlyRate = IsMonthly ? MonthlyRate ?? 0m : _existing?.MonthlyRate ?? 0m;

        AcceptedDetails = new EmployeeDetails(
            pin, LastName.Trim(), FirstName.Trim(), IsUnassigned ? null : Department?.Id,
            QualifiesForOvertime, QualifiesForNightDiff, QualifiesForRestDayPay, QualifiesForPremiumPay,
            ExemptFromUndertimeDeduction, ApplyOvertimeRatePercentageByDefault, DefaultLeaveIsPaid,
            type, dailyRate, monthlyRate,
            ToFraction(RestDayPremiumPercent), ToFraction(HolidayPremiumPercent),
            DefaultWorkTimeHours is double hours ? Math.Round((decimal)hours, 2) : null,
            DefaultSss ?? 0m, DefaultPhilHealth ?? 0m, DefaultPagIbig ?? 0m,
            DefaultPremiumPay ?? 0m, DefaultAllowance ?? 0m, DefaultCashAdvance ?? 0m,
            ClockInBufferBeforeHours, ClockInBufferAfterHours, ClockOutBufferBeforeHours, ClockOutBufferAfterHours);
        return true;
    }

    private async Task<bool> RejectAsync(string message, string title)
    {
        await NotifyAsync(message, title, NoticeKind.Warning);
        return false;
    }

    /// <summary>A premium fraction (0.30) as the percent the box shows (30).</summary>
    private static double? ToPercent(decimal? fraction) => fraction is decimal f ? (double)(f * 100m) : null;

    /// <summary>A typed percent (30) as the stored fraction (0.30), to the four places its
    /// decimal(5,4) column holds.</summary>
    private static decimal? ToFraction(double? percent) => percent is double p ? Math.Round((decimal)p / 100m, 4) : null;

    private static string PercentText(decimal fraction) =>
        $"{(fraction * 100m).ToString("0.##", CultureInfo.CurrentCulture)} %";
}
