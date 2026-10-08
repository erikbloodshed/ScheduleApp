using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Users;
using ScheduleApp.Data.Attendance;
using ScheduleApp.Data.Tests.Fixtures;
using ScheduleApp.Data.Users;
using Xunit;

namespace ScheduleApp.Data.Tests;

/// <summary>
/// The Dapper reads in SqlAttendanceLogRepository, SqlManualAttendanceLogRepository,
/// SqlDayPunchPairingRepository and SqlUserAccountRepository.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceReadTests(DatabaseFixture fixture) : PersistenceTestBase(fixture)
{
    private static readonly DateTime DayStart = new(2026, 3, 2);
    private static readonly DateTime DayEnd = DayStart.AddDays(1).AddTicks(-1);

    private static AttendanceLog Punch(int pin, DateTime timestamp, int punchType = 0) => new()
    {
        EmployeeId = pin,
        Timestamp = timestamp,
        PunchType = punchType,
        Source = AttendanceLogSource.Network,
        DeviceSerialNumber = "SN-1",
        ImportedAt = new DateTime(2026, 3, 3, 1, 2, 3).AddTicks(1234567),
    };

    [Fact]
    public async Task Logs_InRange_ComeByTimestamp_WithEveryColumn()
    {
        var late = Punch(1001, DayStart.AddHours(17), punchType: 1);
        Db.AttendanceLogs.AddRange(late, Punch(1001, DayStart.AddHours(8)), Punch(1002, DayStart.AddHours(9)));
        await Db.SaveChangesAsync();

        var logs = await new SqlAttendanceLogRepository(Db).GetLogsAsync(DayStart, DayEnd);

        Assert.Equal([8, 9, 17], logs.Select(l => l.Timestamp.Hour));
        Scalars.AssertEqual(late, logs[2]);
    }

    [Fact]
    public async Task Logs_RangeEnd_IsExact_ToTheTick()
    {
        // A range ending at 23:59:59.9999999 must not take in the next midnight -- which it would
        // if the bound went to SQL Server as datetime, rounded to the nearest 1/300 s.
        Db.AttendanceLogs.AddRange(Punch(1001, DayEnd), Punch(1001, DayEnd.AddTicks(1)));
        await Db.SaveChangesAsync();

        var log = Assert.Single(await new SqlAttendanceLogRepository(Db).GetLogsAsync(DayStart, DayEnd));

        Assert.Equal(DayEnd, log.Timestamp);
    }

    [Fact]
    public async Task Logs_FilterByPins_WhenGiven()
    {
        Db.AttendanceLogs.AddRange(Punch(1001, DayStart.AddHours(8)), Punch(1002, DayStart.AddHours(9)));
        await Db.SaveChangesAsync();
        var repository = new SqlAttendanceLogRepository(Db);

        Assert.Equal([1002], (await repository.GetLogsAsync(DayStart, DayEnd, [1002])).Select(l => l.EmployeeId));
        Assert.Empty(await repository.GetLogsAsync(DayStart, DayEnd, []));
    }

    [Fact]
    public async Task ManualLogs_InRange_ByTimestamp_AndAll_NewestFirst()
    {
        var saved = new ManualAttendanceLog
        {
            EmployeeId = 1001, Timestamp = DayStart.AddHours(8), PunchType = 0,
            Reason = "Forgot to punch in", EnteredBy = "admin", CreatedAt = new DateTime(2026, 3, 2, 12, 0, 0),
        };
        Db.ManualAttendanceLogs.AddRange(
            saved,
            new ManualAttendanceLog { EmployeeId = 1002, Timestamp = DayStart.AddHours(7), Reason = "r", EnteredBy = "admin" },
            new ManualAttendanceLog { EmployeeId = 1001, Timestamp = DayStart.AddDays(2), Reason = "r", EnteredBy = "admin" });
        await Db.SaveChangesAsync();
        var repository = new SqlManualAttendanceLogRepository(Db);

        var inRange = await repository.GetLogsAsync(DayStart, DayEnd);
        var forPin = await repository.GetLogsAsync(DayStart, DayEnd, [1001]);
        var all = await repository.GetAllAsync();

        Assert.Equal([1002, 1001], inRange.Select(l => l.EmployeeId));
        Scalars.AssertEqual(saved, Assert.Single(forPin));
        Assert.Equal([DayStart.AddDays(2), DayStart.AddHours(8), DayStart.AddHours(7)], all.Select(l => l.Timestamp));
    }

    [Fact]
    public async Task Pairings_ComeWithTheirSlots()
    {
        var day = DateOnly.FromDateTime(DayStart);
        var saved = new DayPunchPairing
        {
            EmployeeId = 1001, Date = day, EditedBy = "admin", EditedAt = new DateTime(2026, 3, 3, 8, 0, 0),
            Slots =
            [
                new DayPunchPairingSlot { PunchId = 11, IsManualPunch = false, SegmentIndex = 0, Role = PairingRole.In },
                new DayPunchPairingSlot { PunchId = 4, IsManualPunch = true, SegmentIndex = 0, Role = PairingRole.Out },
            ],
        };
        Db.DayPunchPairings.AddRange(
            saved,
            new DayPunchPairing { EmployeeId = 1002, Date = day, EditedBy = "admin" },
            new DayPunchPairing { EmployeeId = 1001, Date = day.AddDays(5), EditedBy = "admin" });
        await Db.SaveChangesAsync();
        var repository = new SqlDayPunchPairingRepository(Db);

        var one = await repository.GetAsync(1001, day);
        var range = await repository.GetForRangeAsync(day, day);
        var forPin = await repository.GetForRangeAsync(day, day.AddDays(5), [1001]);

        Assert.NotNull(one);
        Scalars.AssertEqual(saved, one);
        Assert.Equal([(11, false, PairingRole.In), (4, true, PairingRole.Out)],
            one.Slots.Select(s => (s.PunchId, s.IsManualPunch, s.Role)));
        Assert.Same(one, one.Slots[0].DayPunchPairing);
        Assert.Equal([1001, 1002], range.Select(p => p.EmployeeId));
        Assert.Empty(range[1].Slots);
        Assert.Equal([day, day.AddDays(5)], forPin.Select(p => p.Date));
        Assert.Null(await repository.GetAsync(1003, day));
    }

    [Fact]
    public async Task UserAccounts_AnyByUsernameAndAll()
    {
        var repository = new SqlUserAccountRepository(Db);
        Assert.False(await repository.AnyAsync());

        var admin = new UserAccount
        {
            Username = "Admin", PasswordHash = "hash", PasswordSalt = "salt",
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0), IsActive = false,
        };
        Db.UserAccounts.AddRange(
            admin,
            new UserAccount { Username = "clerk", PasswordHash = "h", PasswordSalt = "s", CreatedAtUtc = new DateTime(2026, 2, 1) });
        await Db.SaveChangesAsync();

        Assert.True(await repository.AnyAsync());
        var found = await repository.GetByUsernameAsync("admin");
        Assert.NotNull(found);
        Scalars.AssertEqual(admin, found);
        Assert.Null(await repository.GetByUsernameAsync("nobody"));
        Assert.Equal(["clerk", "Admin"], (await repository.GetAllAsync()).Select(u => u.Username));
    }
}
