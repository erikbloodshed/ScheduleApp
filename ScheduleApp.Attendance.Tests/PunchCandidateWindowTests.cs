using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Covers <see cref="PunchCandidateWindows"/> -- "which punches could this
/// schedule entry possibly match", the definition the shift strategies now build
/// their own search windows from and the Day Punch Pairing editor greys punches
/// out by. The point of these tests is the agreement between those two: every
/// case below asserts the window contents, and the ones that can also asserts
/// that AttendanceCalculator claims/ignores exactly the same punches.
/// </summary>
public class PunchCandidateWindowTests
{
    private static readonly DateOnly Date = new(2026, 8, 9);

    private static Employee Employee() => new()
    {
        Pin = 1001,
        LastName = "Cruz",
        FirstName = "Juan",
    };

    private static ScheduleEntry Schedule(
        ScheduleType type,
        TimeOnly? timeIn = null,
        decimal? workTimeHours = null,
        TimeOnly? restrictedTimeIn = null,
        TimeOnly? restrictedTimeOut = null) => new()
    {
        EmployeeId = 1001,
        Employee = Employee(),
        ScheduleType = type,
        Date = Date,
        TimeIn = timeIn,
        WorkTimeHours = workTimeHours,
        RestrictedTimeIn = restrictedTimeIn,
        RestrictedTimeOut = restrictedTimeOut,
    };

    private static AttendanceLog Punch(DateTime timestamp) => new()
    {
        EmployeeId = 1001,
        Timestamp = timestamp,
        Source = AttendanceLogSource.File,
    };

    private static AttendanceLog Punch(TimeOnly timeOfDay) => Punch(Date.ToDateTime(timeOfDay));

    private static bool IsCandidate(ScheduleEntry schedule, AttendancePolicy policy, AttendanceLog punch) =>
        PunchCandidateWindows.IsCandidate(punch, PunchCandidateWindows.For(schedule, policy));

    // ---- Normal -------------------------------------------------------------

    [Fact]
    public void Normal_day_has_one_window_around_each_scheduled_end()
    {
        // 08:00 + 8h, policy defaults: clock-in 2h either side of 08:00,
        // clock-out 6h either side of 16:00.
        var schedule = Schedule(ScheduleType.Normal, new TimeOnly(8, 0), 8m);

        var windows = PunchCandidateWindows.For(schedule, new AttendancePolicy());

        Assert.Equal(2, windows.Count);
        Assert.Equal(new PunchWindow(Date.ToDateTime(new TimeOnly(6, 0)), Date.ToDateTime(new TimeOnly(10, 0))), windows[0]);
        Assert.Equal(new PunchWindow(Date.ToDateTime(new TimeOnly(10, 0)), Date.ToDateTime(new TimeOnly(22, 0))), windows[1]);
    }

    [Theory]
    [InlineData(5, 59, false)] // a minute before the clock-in buffer opens
    [InlineData(6, 0, true)]   // the buffer's own edge is inclusive
    [InlineData(7, 58, true)]
    [InlineData(22, 0, true)]  // the far edge of the clock-out buffer
    [InlineData(22, 1, false)]
    [InlineData(2, 14, false)] // the stray small-hours tap this feature exists to grey out
    public void Normal_day_admits_exactly_the_buffered_punches(int hour, int minute, bool expected)
    {
        var schedule = Schedule(ScheduleType.Normal, new TimeOnly(8, 0), 8m);
        var punch = Punch(new TimeOnly(hour, minute));

        Assert.Equal(expected, IsCandidate(schedule, new AttendancePolicy(), punch));
    }

    [Fact]
    public void Normal_day_windows_follow_the_entrys_own_buffer_overrides()
    {
        // The three-tier cascade: the entry's own override beats the policy, so a
        // punch the 2h policy default would have excluded is admitted here.
        var schedule = Schedule(ScheduleType.Normal, new TimeOnly(8, 0), 8m);
        schedule.ClockInBufferBeforeHours = 4;

        var punch = Punch(new TimeOnly(4, 30));

        Assert.True(IsCandidate(schedule, new AttendancePolicy(), punch));
        Assert.False(IsCandidate(Schedule(ScheduleType.Normal, new TimeOnly(8, 0), 8m), new AttendancePolicy(), punch));
    }

    [Fact]
    public void Overnight_normal_shift_reaches_into_the_next_morning()
    {
        // 22:00 + 8h ends 06:00 the *next* day -- the case the editor used to miss
        // entirely, since it only ever fetched the calendar day itself.
        var schedule = Schedule(ScheduleType.Normal, new TimeOnly(22, 0), 8m);
        var nextMorning = Punch(Date.AddDays(1).ToDateTime(new TimeOnly(6, 2)));

        Assert.True(IsCandidate(schedule, new AttendancePolicy(), nextMorning));

        var bounds = PunchCandidateWindows.Bounds(PunchCandidateWindows.For(schedule, new AttendancePolicy()));
        Assert.Equal(Date.AddDays(1).ToDateTime(new TimeOnly(12, 0)), bounds!.Value.End);
    }

    [Fact]
    public void Malformed_normal_entry_without_a_window_admits_nothing()
    {
        // No TimeIn/WorkTimeHours means no window to compare against -- the same
        // case SingleWindowShiftCalculationStrategy reports Absent for with both
        // punch lists empty.
        var schedule = Schedule(ScheduleType.Normal);

        Assert.Empty(PunchCandidateWindows.For(schedule, new AttendancePolicy()));
        Assert.False(IsCandidate(schedule, new AttendancePolicy(), Punch(new TimeOnly(8, 0))));
    }

    // ---- The types that never look at punches -------------------------------

    [Theory]
    [InlineData(ScheduleType.Leave)]
    [InlineData(ScheduleType.OfficialBusiness)]
    public void Types_that_never_match_punches_have_no_windows(ScheduleType type)
    {
        // Even fully scheduled, these two are credited from the schedule itself and
        // never consult a punch -- so every punch on such a day is "not counted".
        var schedule = Schedule(type, new TimeOnly(8, 0), 8m);

        Assert.Empty(PunchCandidateWindows.For(schedule, new AttendancePolicy()));
        Assert.False(IsCandidate(schedule, new AttendancePolicy(), Punch(new TimeOnly(8, 1))));
    }

    [Fact]
    public void Unscheduled_rest_day_has_no_windows_but_a_windowed_one_does()
    {
        var unscheduled = Schedule(ScheduleType.RestDay);
        Assert.Empty(PunchCandidateWindows.For(unscheduled, new AttendancePolicy()));

        var windowed = Schedule(ScheduleType.RestDay, new TimeOnly(8, 0), 8m);
        Assert.Equal(2, PunchCandidateWindows.For(windowed, new AttendancePolicy()).Count);
        Assert.True(IsCandidate(windowed, new AttendancePolicy(), Punch(new TimeOnly(7, 55))));
    }

    // ---- SplitShift ---------------------------------------------------------

    [Fact]
    public void Split_shift_has_two_windows_per_segment()
    {
        var schedule = Schedule(ScheduleType.SplitShift, workTimeHours: 8m);
        schedule.FlexibleSegments.Add(new FlexibleSegment { TimeIn = new TimeOnly(5, 0), TimeOut = new TimeOnly(9, 0) });
        schedule.FlexibleSegments.Add(new FlexibleSegment { TimeIn = new TimeOnly(13, 0), TimeOut = new TimeOnly(17, 0) });

        var policy = new AttendancePolicy(); // +/-1h around each segment edge
        var windows = PunchCandidateWindows.For(schedule, policy);

        Assert.Equal(4, windows.Count);
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(4, 30))));  // before segment 1's start
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(9, 45))));  // after segment 1's end
        Assert.False(IsCandidate(schedule, policy, Punch(new TimeOnly(11, 30)))); // mid-gap: neither segment's
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(17, 30))));  // after segment 2's end
    }

    [Fact]
    public void Split_shift_segment_buffer_override_widens_only_that_segment()
    {
        var schedule = Schedule(ScheduleType.SplitShift, workTimeHours: 8m);
        schedule.FlexibleSegments.Add(new FlexibleSegment
        {
            TimeIn = new TimeOnly(5, 0),
            TimeOut = new TimeOnly(9, 0),
            ClockInBufferHours = 3,
        });
        schedule.FlexibleSegments.Add(new FlexibleSegment { TimeIn = new TimeOnly(13, 0), TimeOut = new TimeOnly(17, 0) });

        var policy = new AttendancePolicy();

        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(2, 30))));   // inside the widened in-window
        Assert.False(IsCandidate(schedule, policy, Punch(new TimeOnly(11, 45)))); // segment 2 keeps the 1h default
    }

    [Fact]
    public void Split_shift_with_no_segments_admits_nothing()
    {
        var schedule = Schedule(ScheduleType.SplitShift, workTimeHours: 8m);

        Assert.Empty(PunchCandidateWindows.For(schedule, new AttendancePolicy()));
    }

    // ---- Flexible -----------------------------------------------------------

    [Fact]
    public void Unrestricted_flexible_day_admits_the_whole_calendar_day()
    {
        var schedule = Schedule(ScheduleType.Flexible, workTimeHours: 8m);
        var policy = new AttendancePolicy();

        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(0, 1))));
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(23, 58))));
        Assert.False(IsCandidate(schedule, policy, Punch(Date.AddDays(1).ToDateTime(new TimeOnly(0, 30)))));
    }

    [Fact]
    public void Restricted_flexible_window_bounds_both_sides()
    {
        // RestrictedTimeIn is a hard bound now, matching RestrictedTimeOut -- a
        // punch before the window opens is excluded from the day outright rather
        // than paired and then left uncredited.
        var schedule = Schedule(
            ScheduleType.Flexible, workTimeHours: 8m,
            restrictedTimeIn: new TimeOnly(9, 0), restrictedTimeOut: new TimeOnly(18, 0));
        var policy = new AttendancePolicy();

        Assert.False(IsCandidate(schedule, policy, Punch(new TimeOnly(7, 30))));
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(9, 0))));
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(17, 45))));
        Assert.False(IsCandidate(schedule, policy, Punch(new TimeOnly(18, 30))));
    }

    [Fact]
    public void Flexible_punch_before_the_restricted_window_no_longer_counts()
    {
        // The behaviour change this rule brings, asserted end-to-end through the
        // real calculation rather than just the window: a 07:30/17:00 pair on a
        // 09:00-18:00 restricted day now has its early punch dropped from the
        // search entirely, leaving 17:00 as an unpaired punch -- so the day reads
        // Partial rather than being silently credited from 09:00.
        var schedule = Schedule(
            ScheduleType.Flexible, workTimeHours: 8m,
            restrictedTimeIn: new TimeOnly(9, 0), restrictedTimeOut: new TimeOnly(18, 0));

        var result = AttendanceCalculator.CalculateShift(
            schedule,
            [Punch(new TimeOnly(7, 30)), Punch(new TimeOnly(17, 0))],
            new AttendancePolicy());

        var summary = Assert.Single(result.Summaries);
        Assert.Equal(PunchStatus.Partial, summary.Status);
        Assert.Equal(0, summary.WorkedHours);
    }

    [Fact]
    public void Flexible_day_within_the_restricted_window_is_unaffected()
    {
        var schedule = Schedule(
            ScheduleType.Flexible, workTimeHours: 8m,
            restrictedTimeIn: new TimeOnly(9, 0), restrictedTimeOut: new TimeOnly(18, 0));

        var result = AttendanceCalculator.CalculateShift(
            schedule,
            [Punch(new TimeOnly(9, 0)), Punch(new TimeOnly(17, 0))],
            new AttendancePolicy());

        var summary = Assert.Single(result.Summaries);
        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(8, summary.WorkedHours);
    }

    [Fact]
    public void Restricted_flexible_window_crossing_midnight_reaches_the_next_day()
    {
        // RestrictedTimeOut at or before RestrictedTimeIn is the crosses-midnight
        // convention, so the window runs 20:00 -> 04:00 the following morning.
        var schedule = Schedule(
            ScheduleType.Flexible, workTimeHours: 8m,
            restrictedTimeIn: new TimeOnly(20, 0), restrictedTimeOut: new TimeOnly(4, 0));
        var policy = new AttendancePolicy();

        Assert.False(IsCandidate(schedule, policy, Punch(new TimeOnly(19, 30))));
        Assert.True(IsCandidate(schedule, policy, Punch(new TimeOnly(21, 0))));
        Assert.True(IsCandidate(schedule, policy, Punch(Date.AddDays(1).ToDateTime(new TimeOnly(3, 30)))));
        Assert.False(IsCandidate(schedule, policy, Punch(Date.AddDays(1).ToDateTime(new TimeOnly(4, 30)))));
    }

    // ---- Agreement with the calculation -------------------------------------

    [Fact]
    public void Windows_cover_every_punch_the_calculation_looks_at()
    {
        // The invariant the editor's greying rests on: anything the strategy
        // claims or even considers must fall inside these windows, and anything
        // outside them must be touched by neither.
        var schedule = Schedule(ScheduleType.Normal, new TimeOnly(8, 0), 8m);
        var policy = new AttendancePolicy();

        var punches = new List<AttendanceLog>
        {
            Punch(new TimeOnly(2, 14)),  // outside: small-hours stray
            Punch(new TimeOnly(7, 58)),  // in-window: the real clock-in
            Punch(new TimeOnly(8, 3)),   // in-window: a duplicate tap
            Punch(new TimeOnly(17, 2)),  // in-window: the real clock-out
            Punch(new TimeOnly(23, 30)), // outside: past the clock-out buffer
        };

        var result = AttendanceCalculator.CalculateShift(schedule, punches, policy);
        var windows = PunchCandidateWindows.For(schedule, policy);

        foreach (var punch in result.ClaimedPunches.Concat(result.UnclaimedPunches))
            Assert.True(PunchCandidateWindows.IsCandidate(punch, windows));

        foreach (var punch in punches.Where(p => !PunchCandidateWindows.IsCandidate(p, windows)))
        {
            Assert.DoesNotContain(punch, result.ClaimedPunches);
            Assert.DoesNotContain(punch, result.UnclaimedPunches);
        }

        Assert.Equal(2, punches.Count(p => !PunchCandidateWindows.IsCandidate(p, windows)));
    }
}
