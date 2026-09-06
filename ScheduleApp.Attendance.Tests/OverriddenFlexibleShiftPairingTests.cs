using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using Xunit;

namespace ScheduleApp.Attendance.Tests;

/// <summary>
/// Covers a Flexible day whose punch pairing was decided by hand -- i.e. a saved
/// <see cref="DayPunchPairing"/>, which
/// OverriddenFlexibleShiftCalculationStrategy uses in place of the default
/// pair-by-time-order rule. That editor exists for the case time-order pairing
/// gets wrong: a missed or duplicated tap leaves an orphan and turns a day that
/// was clearly worked into Partial.
///
/// Goes through the public AttendanceCalculator.CalculateShift entry point (its
/// pairingOverride parameter), same convention as FlexibleShiftManualPunchTests --
/// the strategy itself is internal.
/// </summary>
public class OverriddenFlexibleShiftPairingTests
{
    private static readonly DateOnly Date = new(2026, 8, 20);
    private static readonly AttendancePolicy DefaultPolicy = new(); // FlexibleMinimumBreakGap = 1.0h

    private static Employee Employee(int pin = 1001) => new()
    {
        Pin = pin,
        LastName = "Cruz",
        FirstName = "Juan",
    };

    private static ScheduleEntry FlexibleSchedule(Employee employee, decimal? workTimeHours = 8.0m) => new()
    {
        EmployeeId = employee.Pin,
        Employee = employee,
        ScheduleType = ScheduleType.Flexible,
        Date = Date,
        WorkTimeHours = workTimeHours,
    };

    // Ids matter here in a way they don't for the default-pairing tests: a saved
    // slot refers to a punch by (PunchId, IsManualPunch), so every fixture punch
    // needs a distinct, stable id.
    private static AttendanceLog DevicePunch(int employeeId, int id, TimeOnly timeOfDay) => new()
    {
        Id = id,
        EmployeeId = employeeId,
        Timestamp = Date.ToDateTime(timeOfDay),
        Source = AttendanceLogSource.File,
    };

    private static AttendanceLog ManualPunch(int employeeId, int id, TimeOnly timeOfDay) => new()
    {
        Id = id,
        EmployeeId = employeeId,
        Timestamp = Date.ToDateTime(timeOfDay),
        Source = AttendanceLogSource.Manual,
        Reason = "Forgot to badge out",
        EnteredBy = "Test Fixture",
    };

    private static DayPunchPairing Pairing(int employeePin, params (AttendanceLog Punch, int Segment, PairingRole Role)[] slots) => new()
    {
        EmployeeId = employeePin,
        Date = Date,
        EditedBy = "Test Fixture",
        EditedAt = DateTime.UtcNow,
        Slots = slots.Select(s => new DayPunchPairingSlot
        {
            PunchId = s.Punch.Id,
            IsManualPunch = s.Punch.Source == AttendanceLogSource.Manual,
            SegmentIndex = s.Segment,
            Role = s.Role,
        }).ToList(),
    };

    /// <summary>The headline case. Three punches an hour-plus apart: the default
    /// rule pairs 8:00-12:00 and leaves 13:00 dangling, so the day reads Partial
    /// with only 4h credited. Saying by hand that 8:00 pairs with 13:00 (the
    /// 12:00 tap being a stray) makes it a whole 5h interval and a Complete
    /// day.</summary>
    [Fact]
    public void HandPairedPunches_TurnAPartialDayComplete()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 5.0m);

        var first = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));
        var stray = DevicePunch(employee.Pin, 2, new TimeOnly(12, 0));
        var last = DevicePunch(employee.Pin, 3, new TimeOnly(13, 0));
        List<AttendanceLog> punches = [first, stray, last];

        // Sanity: without an override this day really is Partial.
        var withoutOverride = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        Assert.Equal(PunchStatus.Partial, Assert.Single(withoutOverride.Summaries).Status);

        // 8:00 in, 13:00 out, both in segment 0; the 12:00 stray is deliberately
        // left out of the pairing entirely, so it auto-appends (see the
        // auto-append test below for what that means on its own).
        var pairing = Pairing(employee.Pin,
            (first, 0, PairingRole.In),
            (last, 0, PairingRole.Out),
            (stray, 1, PairingRole.In));

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy, pairing);
        var summary = Assert.Single(result.Summaries);

        // Segment 0 is whole (8:00 -> 13:00 = 5h); segment 1 holds only the stray,
        // so it stays unpaired and the day is still Partial -- but the 5h is now
        // credited, which it wasn't before.
        Assert.Equal(5.0, summary.WorkedHours, precision: 3);
        Assert.Equal(PunchStatus.Partial, summary.Status);
        Assert.Equal(2, result.ClaimedPunches.Count);
        Assert.Same(stray, Assert.Single(result.UnclaimedPunches));
    }

    /// <summary>The fully resolved shape: every segment whole, so the day is
    /// Complete. A morning interval (8:00-12:00) and an afternoon one (13:00 to a
    /// manually entered 17:00 badge-out) credit 8h between them. Also covers a
    /// device punch and a manual punch sharing an Id -- the two id spaces are
    /// unrelated, and IsManualPunch is what keeps their slots apart.</summary>
    [Fact]
    public void EverySegmentWhole_MakesTheDayComplete()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);

        var morningIn = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));
        var morningOut = DevicePunch(employee.Pin, 2, new TimeOnly(12, 0));
        var afternoonIn = DevicePunch(employee.Pin, 3, new TimeOnly(13, 0));
        var afternoonOut = ManualPunch(employee.Pin, 1, new TimeOnly(17, 0)); // id 1 in the *manual* id space
        List<AttendanceLog> punches = [morningIn, morningOut, afternoonIn, afternoonOut];

        var pairing = Pairing(employee.Pin,
            (morningIn, 0, PairingRole.In),
            (morningOut, 0, PairingRole.Out),
            (afternoonIn, 1, PairingRole.In),
            (afternoonOut, 1, PairingRole.Out));

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy, pairing);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Complete, summary.Status);
        Assert.Equal(8.0, summary.WorkedHours, precision: 3); // 4h + 4h
        Assert.Equal(4, result.ClaimedPunches.Count);
        Assert.Empty(result.UnclaimedPunches);

        // A device punch and a manual punch can share an id without colliding --
        // IsManualPunch is what tells the two id spaces apart.
        Assert.Equal(new TimeOnly(17, 0), summary.ClockOut);
        Assert.True(summary.ClockOutIsManual);
    }

    /// <summary>A saved pairing isn't rigid: a device punch imported after it was
    /// saved and named by none of its slots is folded in by timestamp with a
    /// positional default role. Here that flips a Complete day back to Partial,
    /// which is the intended signal to re-open the editor rather than a silent
    /// change nobody sees.</summary>
    [Fact]
    public void PunchArrivingAfterTheSave_IsAutoAppendedByTime()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);

        var morningIn = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));
        var morningOut = DevicePunch(employee.Pin, 2, new TimeOnly(17, 0));

        var pairing = Pairing(employee.Pin,
            (morningIn, 0, PairingRole.In),
            (morningOut, 0, PairingRole.Out));

        // As saved: two punches, one whole segment, Complete.
        var before = AttendanceCalculator.CalculateShift(
            schedule, [morningIn, morningOut], DefaultPolicy, pairing);
        Assert.Equal(PunchStatus.Complete, Assert.Single(before.Summaries).Status);

        // A third punch turns up later, referenced by no slot.
        var lateArrival = DevicePunch(employee.Pin, 3, new TimeOnly(19, 0));
        var after = AttendanceCalculator.CalculateShift(
            schedule, [morningIn, morningOut, lateArrival], DefaultPolicy, pairing);
        var summary = Assert.Single(after.Summaries);

        // It lands after morningOut (an Out), so it takes the opposite role (In)
        // in that same segment -- leaving segment 0 as in/out/in, i.e. one whole
        // interval plus an orphan.
        Assert.Equal(PunchStatus.Partial, summary.Status);
        Assert.Equal(9.0, summary.WorkedHours, precision: 3); // the saved 8:00->17:00 still counts
        Assert.Same(lateArrival, Assert.Single(after.UnclaimedPunches));
    }

    /// <summary>A slot pointing at a punch that no longer exists (deleted since
    /// the pairing was saved) leaves its segment half-open rather than throwing or
    /// silently re-pairing around it.</summary>
    [Fact]
    public void SlotReferencingAMissingPunch_LeavesTheSegmentHalfOpen()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);

        var morningIn = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));
        var deletedSince = DevicePunch(employee.Pin, 99, new TimeOnly(17, 0));

        var pairing = Pairing(employee.Pin,
            (morningIn, 0, PairingRole.In),
            (deletedSince, 0, PairingRole.Out));

        // Punch 99 is not in the day's punch list any more.
        var result = AttendanceCalculator.CalculateShift(schedule, [morningIn], DefaultPolicy, pairing);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(PunchStatus.Partial, summary.Status);
        Assert.Equal(0.0, summary.WorkedHours);
        Assert.Same(morningIn, Assert.Single(result.UnclaimedPunches));
    }

    /// <summary>A pairing saved for a day whose punches have all since been
    /// deleted still reads Absent, the same answer the default path gives -- not
    /// an empty Partial.</summary>
    [Fact]
    public void OverriddenDayWithNoPunchesLeft_IsAbsent()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee);
        var gone = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));

        var result = AttendanceCalculator.CalculateShift(
            schedule, [], DefaultPolicy, Pairing(employee.Pin, (gone, 0, PairingRole.In)));

        Assert.Equal(PunchStatus.Absent, Assert.Single(result.Summaries).Status);
    }

    /// <summary>An override is only consulted for Flexible. A Normal day matches
    /// punches against its scheduled window instead, so passing one is ignored
    /// rather than an error -- that's what lets AttendanceWorkflowService look an
    /// override up without also re-checking the schedule type.</summary>
    [Fact]
    public void OverrideOnANonFlexibleDay_IsIgnored()
    {
        var employee = Employee();
        var normal = new ScheduleEntry
        {
            EmployeeId = employee.Pin,
            Employee = employee,
            ScheduleType = ScheduleType.Normal,
            Date = Date,
            TimeIn = new TimeOnly(8, 0),
            WorkTimeHours = 8.0m,
        };

        var inPunch = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));
        var outPunch = DevicePunch(employee.Pin, 2, new TimeOnly(17, 0));
        List<AttendanceLog> punches = [inPunch, outPunch];

        // A deliberately nonsensical pairing for this day -- both punches as Ins.
        var pairing = Pairing(employee.Pin,
            (inPunch, 0, PairingRole.In),
            (outPunch, 1, PairingRole.In));

        var withOverride = AttendanceCalculator.CalculateShift(normal, punches, DefaultPolicy, pairing);
        var withoutOverride = AttendanceCalculator.CalculateShift(normal, punches, DefaultPolicy);

        var a = Assert.Single(withOverride.Summaries);
        var b = Assert.Single(withoutOverride.Summaries);
        Assert.Equal(b.Status, a.Status);
        Assert.Equal(b.WorkedHours, a.WorkedHours, precision: 6);
        Assert.Equal(b.ClockIn, a.ClockIn);
        Assert.Equal(b.ClockOut, a.ClockOut);
    }

    /// <summary>The hand-edited path deliberately skips the odd-count
    /// normalization the default path applies (dropping the noisier side of the
    /// day's smallest adjacent gap). That heuristic exists to guess which punches
    /// are real when nothing better is known -- and an override *is* something
    /// better, so guessing on top of it would silently undo the edit.</summary>
    [Fact]
    public void OverriddenDay_DoesNotDropADuplicateTapTheDefaultPathWouldNormalizeAway()
    {
        var employee = Employee();
        var schedule = FlexibleSchedule(employee, workTimeHours: 8.0m);

        var firstTap = DevicePunch(employee.Pin, 1, new TimeOnly(8, 0));
        var duplicateTap = DevicePunch(employee.Pin, 2, new TimeOnly(8, 8)); // 8 min later
        var badgeOut = DevicePunch(employee.Pin, 3, new TimeOnly(17, 43));
        List<AttendanceLog> punches = [firstTap, duplicateTap, badgeOut];

        // Default path: three punches, smallest adjacent gap is 8 min, so 8:08 is
        // normalized away and the day comes out Complete on 8:00 -> 17:43.
        var normalized = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy);
        var normalizedSummary = Assert.Single(normalized.Summaries);
        Assert.Equal(PunchStatus.Complete, normalizedSummary.Status);
        Assert.Equal(new TimeOnly(8, 0), normalizedSummary.ClockIn);

        // Hand-edited: the person says 8:08 is the real clock-in. That stands, and
        // 8:00 is left as the orphan rather than 8:08 being silently dropped.
        var pairing = Pairing(employee.Pin,
            (duplicateTap, 0, PairingRole.In),
            (badgeOut, 0, PairingRole.Out),
            (firstTap, 1, PairingRole.In));

        var result = AttendanceCalculator.CalculateShift(schedule, punches, DefaultPolicy, pairing);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal(9.0 + 35.0 / 60.0, summary.WorkedHours, precision: 3); // 8:08 -> 17:43
        Assert.Same(firstTap, Assert.Single(result.UnclaimedPunches));
    }
}
