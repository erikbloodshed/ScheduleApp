using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>A schedule as the Set Schedule dialog settles it -- what
/// IScheduleRepository.SetScheduleForDatesAsync writes for each selected day. Fields that don't
/// apply to <see cref="ScheduleType"/> are null (empty for the segments); null overrides
/// inherit the employee's or the company's default. See the matching ScheduleEntry
/// properties for what each means.</summary>
public sealed record ScheduleChoice(
    ScheduleType ScheduleType,
    decimal? WorkTimeHours,
    TimeOnly? TimeIn,
    IReadOnlyList<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)> FlexibleSegments,
    TimeOnly? RestrictedTimeIn,
    TimeOnly? RestrictedTimeOut,
    bool? IsPaidLeave,
    double? ClockInBufferBeforeHours,
    double? ClockInBufferAfterHours,
    double? ClockOutBufferBeforeHours,
    double? ClockOutBufferAfterHours,
    bool? OvertimeEligibleOverride,
    bool? NightDiffEligibleOverride,
    bool? ApplyOvertimeRatePercentageOverride,
    decimal? OvertimeRatePercentageOverride,
    decimal? NightDiffRatePercentageOverride);

/// <summary>
/// The Set/Edit Schedule dialog: one schedule for every selected day of one employee, or of
/// every checked employee. Which fields show follows the type:
/// <list type="bullet">
/// <item>Normal and Official Business -- work time and time in (Official Business skips punch
/// matching, so it has no buffers or overtime/night diff).</item>
/// <item>Flexible -- required hours and a required punching window.</item>
/// <item>Split Shift -- required hours and any number of non-overlapping punching windows
/// (zero is legal), each with optional buffer overrides.</item>
/// <item>Rest Day -- a plain day off unless "scheduled duty" is checked, which gives it a
/// Normal-like window; only that windowed day can earn Rest Day Duty pay, so it's offered only
/// when every employee qualifies for Rest Day Pay.</item>
/// <item>Leave -- just paid or unpaid.</item>
/// </list>
/// Every override box shows the default it inherits until typed over (DefaultedText) -- for
/// several employees whose defaults differ, "(varies)"; left untouched, each keeps inheriting
/// their own.
/// </summary>
public partial class ApplyScheduleViewModel : ReactiveViewModel
{
    private const string Invariant2 = "0.##";

    private readonly string _segmentClockInBufferDefault;
    private readonly string _segmentClockOutBufferDefault;

    /// <summary>Set once Split Shift has its starting window (or the day being edited came with
    /// its own), so removing every window doesn't bring one back.</summary>
    private bool _defaultSegmentAdded;

    /// <param name="employees">Whom the schedule is for -- one, or several checked in the tree
    /// (then without edit framing: "what's set" has no single answer across them).</param>
    /// <param name="ranges">The selected days, as contiguous runs, for the summary.</param>
    /// <param name="isEditing">Whether any selected day already has a schedule.</param>
    /// <param name="prefill">The schedule every selected day shares, if they all share one.</param>
    /// <param name="presetType">The calendar's "Set Schedule As" pick, which wins over the
    /// prefill's type (the rest still comes from the prefill).</param>
    public ApplyScheduleViewModel(
        IReadOnlyList<Employee> employees,
        IReadOnlyList<(DateOnly Start, DateOnly End)> ranges,
        bool isEditing,
        ScheduleEntry? prefill,
        ScheduleType? presetType,
        AttendanceSettings attendanceSettings,
        PayrollPolicy payrollPolicy)
    {
        var policy = attendanceSettings.Policy;
        _segmentClockInBufferDefault = Format(policy.FlexibleSegmentClockInBuffer);
        _segmentClockOutBufferDefault = Format(policy.FlexibleSegmentClockOutBuffer);

        if (employees.Count == 1)
        {
            Title = isEditing ? "Edit Schedule for Selected Days" : "Set Schedule for Selected Days";
            EmployeeHeader = employees[0].DisplayName;
        }
        else
        {
            Title = $"Set Schedule for {employees.Count} Employees";

            const int maxNamesShown = 5;
            var shown = string.Join(", ", employees.Take(maxNamesShown).Select(e => e.DisplayName));
            var suffix = employees.Count > maxNamesShown ? $", and {employees.Count - maxNamesShown} more" : string.Empty;
            EmployeeHeader = $"{employees.Count} employees selected: {shown}{suffix}";
        }

        var totalDays = ranges.Sum(r => r.End.DayNumber - r.Start.DayNumber + 1);
        SelectedDaysSummary = string.Join("\n", ranges.Select(r => r.Start == r.End
                ? r.Start.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{r.Start:MMM d} - {r.End:MMM d, yyyy}")))
            + $"\n\nTotal: {totalDays} day(s) across {ranges.Count} range(s)";

        // Days with differing schedules: something to overwrite, but no single answer to show.
        ShowsMixedScheduleNote = isEditing && prefill is null;

        CanScheduleRestDayDuty = employees.All(e => e.QualifiesForRestDayPay);
        RestDayDutyToolTip = CanScheduleRestDayDuty
            ? "Unchecked (default): plain rest day, punches never honored. Checked: matches punches like a Normal shift " +
              "within a fixed Work time/Time in window to earn Rest Day Duty pay."
            : employees.All(e => !e.QualifiesForRestDayPay)
                ? "None of the selected employees are eligible for Rest Day Pay."
                : "Not all selected employees are eligible for Rest Day Pay.";

        OvertimeEligibleOptions = TriStateOptions(employees, e => e.QualifiesForOvertime, "Eligible", "Not eligible");
        NightDiffEligibleOptions = TriStateOptions(employees, e => e.QualifiesForNightDiff, "Eligible", "Not eligible");
        ApplyOvertimeRateOptions = TriStateOptions(employees, e => e.ApplyOvertimeRatePercentageByDefault,
            "Apply premium", "Straight time only");

        if (prefill is not null)
        {
            _defaultSegmentAdded = true;
            SelectedType = presetType ?? prefill.ScheduleType;
            WorkTimeText = prefill.WorkTimeHours is { } hours ? hours.ToString(Invariant2, CultureInfo.InvariantCulture) : string.Empty;
            TimeIn = prefill.TimeIn;

            // A Flexible day saved before the window was required has none -- start it where a
            // new one starts rather than reproduce a state OK won't accept.
            RestrictedTimeIn = prefill.RestrictedTimeIn ?? new TimeOnly(5, 0);
            RestrictedTimeOut = prefill.RestrictedTimeOut ?? new TimeOnly(21, 0);
            IsPaidLeave = prefill.IsPaidLeave != false;

            foreach (var segment in prefill.FlexibleSegments.OrderBy(s => s.TimeIn))
                Segments.Add(NewSegment(segment.TimeIn, segment.TimeOut, segment.ClockInBufferHours, segment.ClockOutBufferHours,
                    isSaved: true));
        }
        else
        {
            SelectedType = presetType ?? ScheduleType.Normal;
            WorkTimeText = ResolveWorkTimeDefault(employees, attendanceSettings.DefaultWorkTimeHours).ToString(Invariant2, CultureInfo.InvariantCulture);
            TimeIn = new TimeOnly(5, 0);
            RestrictedTimeIn = new TimeOnly(5, 0);
            RestrictedTimeOut = new TimeOnly(21, 0);

            // One employee's own usual choice; several keep Paid, having no single default.
            IsPaidLeave = employees.Count != 1 || employees[0].DefaultLeaveIsPaid;
        }

        // Only a saved Rest Day that had its window filled in was a scheduled duty.
        IsRestDayDuty = CanScheduleRestDayDuty
            && prefill is { ScheduleType: ScheduleType.RestDay }
            && (prefill.WorkTimeHours is not null || prefill.TimeIn is not null);

        // A saved value is shown as such; a saved day that inherited shows the default in
        // normal color -- it *is* what's in effect -- where a new one grays it as a hint.
        var savedNormal = prefill is { ScheduleType: ScheduleType.Normal };
        ClockInBufferBefore = Override(prefill?.ClockInBufferBeforeHours,
            DescribeBufferDefault(employees, e => e.ClockInBufferBeforeHours, policy.ClockInBufferBefore), savedNormal);
        ClockInBufferAfter = Override(prefill?.ClockInBufferAfterHours,
            DescribeBufferDefault(employees, e => e.ClockInBufferAfterHours, policy.ClockInBufferAfter), savedNormal);
        ClockOutBufferBefore = Override(prefill?.ClockOutBufferBeforeHours,
            DescribeBufferDefault(employees, e => e.ClockOutBufferBeforeHours, policy.ClockOutBufferBefore), savedNormal);
        ClockOutBufferAfter = Override(prefill?.ClockOutBufferAfterHours,
            DescribeBufferDefault(employees, e => e.ClockOutBufferAfterHours, policy.ClockOutBufferAfter), savedNormal);

        var savedWithOvertime = prefill is { ScheduleType: ScheduleType.Normal or ScheduleType.Flexible or ScheduleType.SplitShift };
        OvertimeEligibleIndex = TriStateIndex(prefill?.OvertimeEligibleOverride);
        NightDiffEligibleIndex = TriStateIndex(prefill?.NightDiffEligibleOverride);
        ApplyOvertimeRateIndex = TriStateIndex(prefill?.ApplyOvertimeRatePercentageOverride);
        OvertimeRatePercentage = new DefaultedText(() => Format(payrollPolicy.OvertimeRatePercentage),
            prefill?.OvertimeRatePercentageOverride?.ToString("0.####", CultureInfo.InvariantCulture))
        { GraysDefault = !savedWithOvertime };
        NightDiffRatePercentage = new DefaultedText(() => Format(payrollPolicy.NightDiffRatePercentage),
            prefill?.NightDiffRatePercentageOverride?.ToString("0.####", CultureInfo.InvariantCulture))
        { GraysDefault = !savedWithOvertime };

        var layout = this.WhenAnyValue(x => x.SelectedType, x => x.IsRestDayDuty, FieldLayout.For);
        _showsRestDayDutyHelper = layout.Select(l => l.RestDayDuty).ToProperty(this, x => x.ShowsRestDayDuty);
        _showsWorkTimeHelper = layout.Select(l => l.WorkTime).ToProperty(this, x => x.ShowsWorkTime);
        _showsTimeInHelper = layout.Select(l => l.TimeIn).ToProperty(this, x => x.ShowsTimeIn);
        _showsLeavePayHelper = layout.Select(l => l.LeavePay).ToProperty(this, x => x.ShowsLeavePay);
        _showsRestrictedWindowHelper = layout.Select(l => l.RestrictedWindow).ToProperty(this, x => x.ShowsRestrictedWindow);
        _showsSegmentsHelper = layout.Select(l => l.Segments).ToProperty(this, x => x.ShowsSegments);
        _showsNormalBuffersHelper = layout.Select(l => l.NormalBuffers).ToProperty(this, x => x.ShowsNormalBuffers);
        _showsOvertimeNightDiffHelper = layout.Select(l => l.OvertimeNightDiff).ToProperty(this, x => x.ShowsOvertimeNightDiff);

        // The first switch to Split Shift starts it with one 5:00 AM - 9:00 PM window -- just a
        // starting point.
        this.WhenAnyValue(x => x.SelectedType)
            .Where(type => type == ScheduleType.SplitShift && !_defaultSegmentAdded && Segments.Count == 0)
            .Subscribe(type =>
            {
                _defaultSegmentAdded = true;
                Segments.Add(NewSegment(new TimeOnly(5, 0), new TimeOnly(21, 0), null, null, isSaved: false));
            });
    }

    public string Title { get; }

    public string EmployeeHeader { get; }

    public string SelectedDaysSummary { get; }

    public bool ShowsMixedScheduleNote { get; }

    public IReadOnlyList<ScheduleType> ScheduleTypes { get; } = Enum.GetValues<ScheduleType>();

    [Reactive]
    public partial ScheduleType SelectedType { get; set; }

    /// <summary>Whether every employee qualifies for Rest Day Pay -- otherwise a scheduled duty
    /// could never pay out, so it can't be chosen.</summary>
    public bool CanScheduleRestDayDuty { get; }

    public string RestDayDutyToolTip { get; }

    /// <summary>A Rest Day with a fixed window to match punches against.</summary>
    [Reactive]
    public partial bool IsRestDayDuty { get; set; }

    /// <summary>Hours, as typed -- a shift length, or Flexible/Split Shift's required total.</summary>
    [Reactive]
    public partial string WorkTimeText { get; set; } = string.Empty;

    [Reactive]
    public partial TimeOnly? TimeIn { get; set; }

    [Reactive]
    public partial bool IsPaidLeave { get; set; } = true;

    [Reactive]
    public partial TimeOnly? RestrictedTimeIn { get; set; }

    [Reactive]
    public partial TimeOnly? RestrictedTimeOut { get; set; }

    /// <summary>Split Shift's punching windows.</summary>
    public ObservableCollection<ScheduleSegmentViewModel> Segments { get; } = [];

    public DefaultedText ClockInBufferBefore { get; }
    public DefaultedText ClockInBufferAfter { get; }
    public DefaultedText ClockOutBufferBefore { get; }
    public DefaultedText ClockOutBufferAfter { get; }

    /// <summary>"Use employee default (…)" / the two explicit choices -- index 0 is no
    /// override.</summary>
    public IReadOnlyList<string> OvertimeEligibleOptions { get; }
    public IReadOnlyList<string> NightDiffEligibleOptions { get; }
    public IReadOnlyList<string> ApplyOvertimeRateOptions { get; }

    [Reactive]
    public partial int OvertimeEligibleIndex { get; set; }

    [Reactive]
    public partial int NightDiffEligibleIndex { get; set; }

    [Reactive]
    public partial int ApplyOvertimeRateIndex { get; set; }

    /// <summary>As a decimal fraction (0.25 for 25%).</summary>
    public DefaultedText OvertimeRatePercentage { get; }

    public DefaultedText NightDiffRatePercentage { get; }

    [ObservableAsProperty]
    public partial bool ShowsRestDayDuty { get; }

    [ObservableAsProperty]
    public partial bool ShowsWorkTime { get; }

    [ObservableAsProperty]
    public partial bool ShowsTimeIn { get; }

    [ObservableAsProperty]
    public partial bool ShowsLeavePay { get; }

    [ObservableAsProperty]
    public partial bool ShowsRestrictedWindow { get; }

    [ObservableAsProperty]
    public partial bool ShowsSegments { get; }

    [ObservableAsProperty]
    public partial bool ShowsNormalBuffers { get; }

    [ObservableAsProperty]
    public partial bool ShowsOvertimeNightDiff { get; }

    /// <summary>What OK settled on -- null until it succeeds.</summary>
    public ScheduleChoice? AcceptedChoice { get; private set; }

    [ReactiveCommand]
    private void AddSegment() => Segments.Add(NewSegment(null, null, null, null, isSaved: false));

    [ReactiveCommand]
    private void RemoveSegment(ScheduleSegmentViewModel segment) => Segments.Remove(segment);

    /// <summary>OK: checks what the type needs, then settles <see cref="AcceptedChoice"/>.</summary>
    [ReactiveCommand]
    private async Task<bool> AcceptAsync()
    {
        var (choice, problem) = Validate();
        if (choice is null)
        {
            await NotifyAsync(problem!, "Check your entry", NoticeKind.Warning);
            return false;
        }

        AcceptedChoice = choice;
        return true;
    }

    private (ScheduleChoice? Choice, string? Problem) Validate()
    {
        var type = SelectedType;
        var none = new ScheduleChoice(type, null, null, [], null, null, null, null, null, null, null, null, null, null, null, null);

        // Leave skips punch matching entirely -- paid or unpaid is all there is to it.
        if (type == ScheduleType.Leave)
            return (none with { IsPaidLeave = IsPaidLeave }, null);

        var choice = none;
        var isUnscheduledRestDay = type == ScheduleType.RestDay && !IsRestDayDuty;

        decimal hours = 0;
        if (!isUnscheduledRestDay)
        {
            if (!decimal.TryParse(WorkTimeText, NumberStyles.Number, CultureInfo.InvariantCulture, out hours) || hours <= 0)
                return (null, type is ScheduleType.Flexible or ScheduleType.SplitShift
                    ? "Enter a valid required hours total, e.g. 8 or 8.5."
                    : "Enter a valid work time in hours, e.g. 9 or 9.5.");

            choice = choice with { WorkTimeHours = hours };
        }

        if (type != ScheduleType.OfficialBusiness)
        {
            if (!TryReadOverride(OvertimeRatePercentage, "overtime rate percentage", out var overtimeRate, "as a decimal, e.g. 0.25 for 25%", out var problem)
                || !TryReadOverride(NightDiffRatePercentage, "night diff rate percentage", out var nightDiffRate, "as a decimal, e.g. 0.10 for 10%", out problem))
                return (null, problem);

            choice = choice with
            {
                OvertimeEligibleOverride = TriStateValue(OvertimeEligibleIndex),
                NightDiffEligibleOverride = TriStateValue(NightDiffEligibleIndex),
                ApplyOvertimeRatePercentageOverride = TriStateValue(ApplyOvertimeRateIndex),
                OvertimeRatePercentageOverride = (decimal?)overtimeRate,
                NightDiffRatePercentageOverride = (decimal?)nightDiffRate,
            };
        }

        if (type == ScheduleType.Flexible)
        {
            // A window narrower than the required hours is still legal: the window's end is a
            // search boundary, not a ceiling on the day's total.
            if (RestrictedTimeIn is not { } windowIn) return (null, "Select a time in for the punching window.");
            if (RestrictedTimeOut is not { } windowOut) return (null, "Select a time out for the punching window.");
            if (windowIn == windowOut) return (null, "Time in and time out can't be the same.");

            return (choice with { RestrictedTimeIn = windowIn, RestrictedTimeOut = windowOut }, null);
        }

        if (type == ScheduleType.SplitShift)
        {
            var segments = new List<(TimeOnly TimeIn, TimeOnly TimeOut, double? ClockInBufferHours, double? ClockOutBufferHours)>();
            foreach (var segment in Segments)
            {
                if (segment.TimeIn is not { } segmentIn) return (null, "Select a start time for each punching window.");
                if (segment.TimeOut is not { } segmentOut) return (null, "Select an end time for each punching window.");
                if (segmentIn == segmentOut)
                    return (null, "Each punching window's start and end time can't be the same -- remove the window instead, or use different times.");

                if (!TryReadOverride(segment.ClockInBuffer, "clock-in buffer", out var clockInBuffer, "hours", out var problem)
                    || !TryReadOverride(segment.ClockOutBuffer, "clock-out buffer", out var clockOutBuffer, "hours", out problem))
                    return (null, problem);

                segments.Add((segmentIn, segmentOut, clockInBuffer, clockOutBuffer));
            }

            // Ordered by start, so each window only needs checking against the next. One that
            // crosses midnight (end <= start) really ends a day later.
            segments.Sort((a, b) => a.TimeIn.CompareTo(b.TimeIn));
            for (var i = 1; i < segments.Count; i++)
            {
                if (segments[i].TimeIn.ToTimeSpan() < End(segments[i - 1].TimeIn, segments[i - 1].TimeOut))
                    return (null, "Punching windows can't overlap -- adjust the times so each window's start is at or after the previous window's end.");
            }

            if (segments.Count > 0)
            {
                var totalWindowHours = segments.Sum(s => (End(s.TimeIn, s.TimeOut) - s.TimeIn.ToTimeSpan()).TotalHours);
                if ((double)hours > totalWindowHours)
                    return (null, $"The punching windows add up to only {totalWindowHours:0.##} hour(s), which is shorter than the " +
                        $"{hours} required hour(s) -- widen the windows or lower the required hours.");
            }

            return (choice with { FlexibleSegments = segments }, null);
        }

        // A plain day off: no window, so no punches to match and nothing more to read.
        if (isUnscheduledRestDay)
            return (choice, null);

        // Normal, Official Business, and a Rest Day duty: a shift start, its end following
        // from the hours.
        if (TimeIn is not { } timeIn) return (null, "Select a time in.");
        choice = choice with { TimeIn = timeIn };

        // Buffers only matter where punches are matched against the window -- not Official
        // Business.
        if (type is ScheduleType.Normal or ScheduleType.RestDay)
        {
            if (!TryReadOverride(ClockInBufferBefore, "clock-in buffer, before scheduled time", out var inBefore, "hours", out var problem)
                || !TryReadOverride(ClockInBufferAfter, "clock-in buffer, after scheduled time", out var inAfter, "hours", out problem)
                || !TryReadOverride(ClockOutBufferBefore, "clock-out buffer, before scheduled time", out var outBefore, "hours", out problem)
                || !TryReadOverride(ClockOutBufferAfter, "clock-out buffer, after scheduled time", out var outAfter, "hours", out problem))
                return (null, problem);

            choice = choice with
            {
                ClockInBufferBeforeHours = inBefore,
                ClockInBufferAfterHours = inAfter,
                ClockOutBufferBeforeHours = outBefore,
                ClockOutBufferAfterHours = outAfter,
            };
        }

        return (choice, null);
    }

    private static TimeSpan End(TimeOnly start, TimeOnly end) =>
        end <= start ? end.ToTimeSpan() + TimeSpan.FromDays(1) : end.ToTimeSpan();

    /// <summary>An override box read back: null when it still shows the default or is empty
    /// ("inherit"), otherwise a number of 0 or more.</summary>
    private static bool TryReadOverride(DefaultedText box, string fieldLabel, out double? value, string unit, out string? problem)
    {
        value = null;
        problem = null;
        if (box.IsDefault || string.IsNullOrWhiteSpace(box.Text))
            return true;

        if (!double.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            problem = $"Enter a valid {fieldLabel} in {unit} (0 or more), or leave it at the grayed-out policy default.";
            return false;
        }

        value = parsed;
        return true;
    }

    private ScheduleSegmentViewModel NewSegment(TimeOnly? timeIn, TimeOnly? timeOut, double? clockInBuffer, double? clockOutBuffer, bool isSaved) =>
        new(timeIn, timeOut,
            Override(clockInBuffer, _segmentClockInBufferDefault, isSaved),
            Override(clockOutBuffer, _segmentClockOutBufferDefault, isSaved));

    private static DefaultedText Override(double? value, string defaultText, bool isSaved) =>
        new(() => defaultText, value?.ToString(Invariant2, CultureInfo.InvariantCulture)) { GraysDefault = !isSaved };

    private static string Format(double value) => value.ToString(Invariant2, CultureInfo.InvariantCulture);

    private static string Format(decimal value) => value.ToString(Invariant2, CultureInfo.InvariantCulture);

    private static int TriStateIndex(bool? value) => value switch
    {
        null => 0,
        true => 1,
        false => 2,
    };

    private static bool? TriStateValue(int index) => index switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    /// <summary>A tri-state combo's options, the first spelling out what "the employee's
    /// default" is -- when every employee agrees on it; a blend would mislead.</summary>
    private static string[] TriStateOptions(IReadOnlyList<Employee> employees, Func<Employee, bool> flag, string trueLabel, string falseLabel)
    {
        var first = flag(employees[0]);
        var noOverride = employees.All(e => flag(e) == first)
            ? $"Use employee default ({(first ? trueLabel : falseLabel)})"
            : "Use employee default (varies)";
        return [noOverride, trueLabel, falseLabel];
    }

    /// <summary>A Normal buffer's inherited value: each employee's own default, else the
    /// policy's -- the cascade NormalBufferResolver applies -- or "(varies)" when the
    /// employees differ.</summary>
    private static string DescribeBufferDefault(IReadOnlyList<Employee> employees, Func<Employee, double?> employeeDefault, double policyDefault)
    {
        double Resolve(Employee e) => employeeDefault(e) ?? policyDefault;
        var first = Resolve(employees[0]);
        return employees.All(e => Resolve(e) == first) ? Format(first) : "(varies)";
    }

    /// <summary>A new day's starting work time: the employees' shared default, or the
    /// company's when they differ -- unlike a buffer, it has to be a real number.</summary>
    private static double ResolveWorkTimeDefault(IReadOnlyList<Employee> employees, double policyDefault)
    {
        double Resolve(Employee e) => (double?)e.DefaultWorkTimeHours ?? policyDefault;
        var first = Resolve(employees[0]);
        return employees.All(e => Resolve(e) == first) ? first : policyDefault;
    }

    /// <summary>Which fields a schedule type (and, for a Rest Day, a scheduled duty) uses.</summary>
    private sealed record FieldLayout(
        bool RestDayDuty, bool WorkTime, bool TimeIn, bool LeavePay, bool RestrictedWindow, bool Segments,
        bool NormalBuffers, bool OvertimeNightDiff)
    {
        public static FieldLayout For(ScheduleType type, bool isRestDayDuty)
        {
            var isRestDay = type == ScheduleType.RestDay;
            var noWindow = type == ScheduleType.Leave || (isRestDay && !isRestDayDuty);
            var isFlexibleOrSplit = type is ScheduleType.Flexible or ScheduleType.SplitShift;

            return new FieldLayout(
                RestDayDuty: isRestDay,
                WorkTime: !noWindow,
                TimeIn: !noWindow && !isFlexibleOrSplit,
                LeavePay: type == ScheduleType.Leave,
                RestrictedWindow: type == ScheduleType.Flexible,
                Segments: type == ScheduleType.SplitShift,
                NormalBuffers: !noWindow && type is ScheduleType.Normal or ScheduleType.RestDay,
                OvertimeNightDiff: type is not (ScheduleType.Leave or ScheduleType.OfficialBusiness));
        }
    }
}

/// <summary>One Split Shift punching window, with optional overrides of the company's
/// per-window buffers.</summary>
public partial class ScheduleSegmentViewModel(
    TimeOnly? timeIn, TimeOnly? timeOut, DefaultedText clockInBuffer, DefaultedText clockOutBuffer) : ReactiveObject
{
    [Reactive]
    public partial TimeOnly? TimeIn { get; set; } = timeIn;

    [Reactive]
    public partial TimeOnly? TimeOut { get; set; } = timeOut;

    public DefaultedText ClockInBuffer { get; } = clockInBuffer;

    public DefaultedText ClockOutBuffer { get; } = clockOutBuffer;
}
