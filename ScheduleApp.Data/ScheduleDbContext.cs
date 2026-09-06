using Microsoft.EntityFrameworkCore;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Core.Users;

namespace ScheduleApp.Data;

public class ScheduleDbContext : DbContext
{
    public ScheduleDbContext(DbContextOptions<ScheduleDbContext> options) : base(options)
    {
    }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();
    public DbSet<FlexibleSegment> FlexibleSegments => Set<FlexibleSegment>();
    public DbSet<AttendanceLog> AttendanceLogs => Set<AttendanceLog>();
    public DbSet<ManualAttendanceLog> ManualAttendanceLogs => Set<ManualAttendanceLog>();
    public DbSet<DayPunchPairing> DayPunchPairings => Set<DayPunchPairing>();
    public DbSet<DayPunchPairingSlot> DayPunchPairingSlots => Set<DayPunchPairingSlot>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<PayrollAdjustment> PayrollAdjustments => Set<PayrollAdjustment>();
    public DbSet<PayrollUndertimeWaiver> PayrollUndertimeWaivers => Set<PayrollUndertimeWaiver>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollRunEmployee> PayrollRunEmployees => Set<PayrollRunEmployee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Department>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.Property(d => d.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Holiday>(e =>
        {
            e.Property(h => h.Name).HasMaxLength(200).IsRequired();

            // The actual enforcement of "one holiday per calendar date" -- the
            // backstop behind HolidayRepository.EnsureDateIsFreeAsync's own
            // check-then-act pre-check, same relationship Employee's Pin index has
            // with AddEmployeeAsync/UpdateEmployeeAsync's own Pin check above.
            e.HasIndex(h => h.Date).IsUnique();
        });

        modelBuilder.Entity<Employee>(e =>
        {
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();

            e.HasOne(x => x.Department)
                .WithMany(d => d.Employees)
                .HasForeignKey(x => x.DepartmentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // The check in ScheduleRepository.AddEmployeeAsync/UpdateEmployeeAsync (query
            // for an existing Pin, then insert/update) is a check-then-act race: two
            // near-simultaneous writes can both pass the check before either commits. This
            // index is the actual guarantee. Plain IsUnique(), not filtered, now that Pin
            // is required (see Employee.Pin's own doc comment) -- there's no longer a
            // "many employees legitimately have no Employee ID yet" population of NULLs to
            // exempt the way the filter used to. This is also what makes Pin usable as
            // ScheduleEntry.Employee's own alternate-key FK target below -- SQL Server
            // requires the referenced column under a full PRIMARY KEY or UNIQUE CONSTRAINT,
            // which a filtered index specifically doesn't satisfy.
            e.HasIndex(x => x.Pin)
                .IsUnique();

            // Nullable on purpose -- null means "inherit AttendancePolicy's
            // ClockInBufferBefore/ClockInBufferAfter/ClockOutBufferBefore/
            // ClockOutBufferAfter default" (see NormalBufferResolver), which is
            // what every employee gets unless they're individually given their
            // own default. Same pattern, and same "float" column type, as
            // ScheduleEntry's own per-day buffer overrides below -- this is just
            // the middle tier of that same three-tier cascade (day overrides
            // employee, employee overrides policy) rather than a new shape.
            e.Property(x => x.ClockInBufferBeforeHours).HasColumnType("float");
            e.Property(x => x.ClockInBufferAfterHours).HasColumnType("float");
            e.Property(x => x.ClockOutBufferBeforeHours).HasColumnType("float");
            e.Property(x => x.ClockOutBufferAfterHours).HasColumnType("float");

            // Defaults false at the database level too, matching Employee.
            // IsBlacklisted's own C# default -- a raw SQL insert or an older
            // migration replay still leaves existing/newly-added employees
            // active unless explicitly blacklisted.
            e.Property(x => x.IsBlacklisted).HasDefaultValue(false);

            // Both default true at the database level too (not just the C# property
            // default), so a raw SQL insert or an older migration replay still leaves
            // existing/newly-added employees eligible unless explicitly opted out.
            e.Property(x => x.QualifiesForOvertime).HasDefaultValue(true);
            e.Property(x => x.QualifiesForNightDiff).HasDefaultValue(true);

            // Same defensive-default reasoning as the two flags above, but false is
            // the safe/opt-in direction here (same reasoning as DefaultLeaveIsPaid
            // below) -- a raw SQL insert or an older migration replay still leaves
            // existing/newly-added employees ineligible for Rest Day Pay/Premium Pay
            // until explicitly opted in, matching Employee.QualifiesForRestDayPay/
            // QualifiesForPremiumPay's own C# default.
            e.Property(x => x.QualifiesForRestDayPay).HasDefaultValue(false);
            e.Property(x => x.QualifiesForPremiumPay).HasDefaultValue(false);

            // Same defensive-default reasoning as the two flags above -- this is
            // the third overtime state's per-employee default (see Employee.
            // ApplyOvertimeRatePercentageByDefault's own doc comment), and
            // defaults to eligible-for-the-real-premium at the database level
            // too, matching the common, labor-law-compliant case.
            e.Property(x => x.ApplyOvertimeRatePercentageByDefault).HasDefaultValue(true);

            // Same defensive-default reasoning as the flags above -- defaults to
            // Daily at the database level too, matching Employee.EmployeeType's
            // own C# default, so a raw SQL insert or an older migration replay
            // still leaves existing/newly-added employees computing the
            // pre-this-feature way unless explicitly opted into Monthly.
            e.Property(x => x.EmployeeType).HasDefaultValue(EmployeeType.Daily);

            // Same defensive-default reasoning as the two flags above -- a raw SQL
            // insert or an older migration replay still leaves DailyRate at a sane
            // ₱0 rather than a nullable gap PayrollCalculator would have to guard
            // against separately.
            e.Property(x => x.DailyRate).HasColumnType("decimal(10,2)").HasDefaultValue(0m);

            // Same decimal(10,2)/0-default convention as DailyRate above -- see
            // Employee.MonthlyRate's own doc comment for what this feeds into.
            e.Property(x => x.MonthlyRate).HasColumnType("decimal(10,2)").HasDefaultValue(0m);

            // decimal(5,4) -- same precision/headroom convention as ScheduleEntry's
            // OvertimeRatePercentageOverride/NightDiffRatePercentageOverride below
            // (see those for why: enough room for an oddball value like 1.25 typed
            // in by mistake to fail loudly rather than silently truncate). Defaults
            // to 0 at the database level too, matching Employee.
            // RestDayWorkPremiumPercentage's own C# default -- a Rest Day worked
            // with no premium set just pays straight time rather than erroring.
            e.Property(x => x.RestDayWorkPremiumPercentage).HasColumnType("decimal(5,4)").HasDefaultValue(0m);

            // Same defensive-default reasoning again -- defaults false (Unpaid) at
            // the database level too, matching Employee.DefaultLeaveIsPaid's own
            // C# default. Unlike QualifiesForOvertime/QualifiesForNightDiff above,
            // the safe default here is the opt-IN direction: an employee has to be
            // explicitly marked eligible for paid leave rather than assumed to be.
            e.Property(x => x.DefaultLeaveIsPaid).HasDefaultValue(false);

            // Same decimal(10,2)/0-default convention as DailyRate above -- see
            // Employee.DefaultSss/DefaultPhilHealth/DefaultPagIbig's own doc
            // comments for what these feed into.
            e.Property(x => x.DefaultSss).HasColumnType("decimal(10,2)").HasDefaultValue(0m);
            e.Property(x => x.DefaultPhilHealth).HasColumnType("decimal(10,2)").HasDefaultValue(0m);
            e.Property(x => x.DefaultPagIbig).HasColumnType("decimal(10,2)").HasDefaultValue(0m);

            // Same decimal(10,2)/0-default convention as DefaultSss/DefaultPhilHealth/
            // DefaultPagIbig above -- see Employee.DefaultPremiumPay/DefaultAllowance/
            // DefaultCashAdvance's own doc comments for what these feed into.
            e.Property(x => x.DefaultPremiumPay).HasColumnType("decimal(10,2)").HasDefaultValue(0m);
            e.Property(x => x.DefaultAllowance).HasColumnType("decimal(10,2)").HasDefaultValue(0m);
            e.Property(x => x.DefaultCashAdvance).HasColumnType("decimal(10,2)").HasDefaultValue(0m);
        });

        modelBuilder.Entity<ScheduleEntry>(e =>
        {
            // TimeOut is computed in code (ScheduleEntry.TimeOut) and never persisted.
            e.Ignore(x => x.TimeOut);
            e.Ignore(x => x.CrossesMidnight);
            e.Ignore(x => x.DisplayText);

            e.Property(x => x.WorkTimeHours).HasColumnType("decimal(5,2)");

            // Nullable on purpose -- null means "inherit the employee's own
            // default (Employee.ClockInBufferBeforeHours/etc. above), or
            // AttendancePolicy's ClockInBufferBefore/ClockInBufferAfter/
            // ClockOutBufferBefore/ClockOutBufferAfter default if that's null
            // too" (see NormalBufferResolver), which is what every Normal entry
            // gets unless it's individually overridden. Same pattern as
            // FlexibleSegment's buffer overrides below, just scoped to the whole
            // entry rather than a per-segment child row, since Normal has no
            // segments to hang them off of.
            e.Property(x => x.ClockInBufferBeforeHours).HasColumnType("float");
            e.Property(x => x.ClockInBufferAfterHours).HasColumnType("float");
            e.Property(x => x.ClockOutBufferBeforeHours).HasColumnType("float");
            e.Property(x => x.ClockOutBufferAfterHours).HasColumnType("float");

            // Nullable on purpose -- meaningful only for Flexible, and even then
            // only when the day's single allowed punching window is restricted
            // (null on both sides is "no restriction," the common case). Same
            // "time" column type TimeIn/FlexibleSegment.TimeIn/TimeOut already
            // use -- see ScheduleEntry.RestrictedTimeIn/RestrictedTimeOut.
            e.Property(x => x.RestrictedTimeIn).HasColumnType("time");
            e.Property(x => x.RestrictedTimeOut).HasColumnType("time");

            // Nullable on purpose -- meaningful only when ScheduleType == Leave, null
            // for every other type. No HasDefaultValue: unlike Employee's eligibility
            // flags/DailyRate above, there's no single safe default that applies
            // regardless of ScheduleType, so this stays genuinely null until the write
            // path that saves a Leave day sets it (see ScheduleEntry.IsPaidLeave).
            e.Property(x => x.IsPaidLeave).HasColumnType("bit");

            // Per-day overrides added for the Overtime/Night Diff rate-percentage
            // refactor (see ScheduleEntry's own doc comments on each of these five
            // properties). All five stay genuinely null (no HasDefaultValue) --
            // null is itself the meaningful "no override, fall through to the
            // employee-level/global default" value, not a placeholder waiting to
            // be replaced, so a default-value column would be actively wrong here.
            e.Property(x => x.OvertimeEligibleOverride).HasColumnType("bit");
            e.Property(x => x.NightDiffEligibleOverride).HasColumnType("bit");
            e.Property(x => x.ApplyOvertimeRatePercentageOverride).HasColumnType("bit");

            // decimal(5,4) -- same precision PayrollPolicy.OvertimeRatePercentage/
            // NightDiffRatePercentage need in C# (e.g. 0.25, 0.125 for an unusual
            // half-premium policy), with headroom up to 9.9999 so an oddball
            // multiplier-shaped value typed in by mistake doesn't silently
            // truncate instead of failing loudly.
            e.Property(x => x.OvertimeRatePercentageOverride).HasColumnType("decimal(5,4)");
            e.Property(x => x.NightDiffRatePercentageOverride).HasColumnType("decimal(5,4)");

            // Real FK again -- possible now that Employee.Pin is required and uniquely
            // indexed (see Employee.Pin's own doc comment and Employee's own Pin index
            // above), unlike the brief stretch where EmployeeId meant Pin but Pin itself
            // could still be null. HasPrincipalKey(x => x.Pin), not the default
            // (Employee.Id) -- this FK targets Pin specifically, since that's what
            // EmployeeId actually matches (see ScheduleEntry.EmployeeId's own doc
            // comment), not the ScheduleApp database key. OnDelete(DeleteBehavior.Cascade)
            // is back too -- ScheduleRepository.DeleteEmployeeAsync no longer needs its
            // own explicit ScheduleEntries cleanup, same as before Employee.Pin's brief
            // stretch of being optional.
            e.HasOne(x => x.Employee)
                .WithMany(emp => emp.ScheduleEntries)
                .HasForeignKey(x => x.EmployeeId)
                .HasPrincipalKey(emp => emp.Pin)
                .OnDelete(DeleteBehavior.Cascade);

            // The actual enforcement of "one schedule per employee per day" --
            // SetScheduleForDatesAsync relies on this being true. Still just as valid a
            // constraint with EmployeeId meaning Pin as it was meaning Employee.Id --
            // either way it's "one row per (whichever employee this is, calendar day)."
            e.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique();
        });

        modelBuilder.Entity<FlexibleSegment>(e =>
        {
            // Computed in code (FlexibleSegment.CrossesMidnight) and never
            // persisted -- same reasoning as ScheduleEntry.CrossesMidnight above.
            e.Ignore(x => x.CrossesMidnight);

            // Nullable on purpose -- null means "inherit AttendancePolicy's
            // FlexibleSegmentClockInBuffer/FlexibleSegmentClockOutBuffer
            // default" (see FlexibleShiftCalculationStrategy), which is what
            // every segment gets unless it's individually overridden.
            e.Property(x => x.ClockInBufferHours).HasColumnType("float");
            e.Property(x => x.ClockOutBufferHours).HasColumnType("float");

            e.HasOne(x => x.ScheduleEntry)
                .WithMany(s => s.FlexibleSegments)
                .HasForeignKey(x => x.ScheduleEntryId)
                // Deleting/replacing a ScheduleEntry (or the whole day it belongs to)
                // must take its segments with it -- there's no scenario where an
                // orphaned segment should survive its parent entry.
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AttendanceLog>(e =>
        {
            e.ToTable("AttendanceLogs");

            // Stored as a string (not the default int) so the raw table is readable
            // without a lookup, and so adding a source later doesn't renumber anything
            // that's already persisted.
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DeviceSerialNumber).HasMaxLength(50);

            // Only ever populated on the transient, in-memory Source=Manual
            // projection ManualAttendanceLog.ToAttendanceLog() produces -- see
            // AttendanceLog.Reason's remarks. There's no AttendanceLogs.Reason
            // column, so this can't round-trip through a real row anyway.
            e.Ignore(x => x.Reason);

            // Same treatment as Reason above -- see AttendanceLog.EnteredBy's
            // remarks. No AttendanceLogs.EnteredBy column either.
            e.Ignore(x => x.EnteredBy);

            // The actual idempotency guarantee behind AddLogsAsync -- re-importing the
            // same or an overlapping .dat export only inserts punches not already here.
            e.HasIndex(x => new { x.EmployeeId, x.Timestamp, x.PunchType }).IsUnique();

            // GetLogsAsync filters by Timestamp range only (not EmployeeId), so it can't
            // use the composite index above as a range scan -- this is what makes that
            // query fast instead of a table scan once AttendanceLogs has real history in it.
            e.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<ManualAttendanceLog>(e =>
        {
            e.ToTable("ManualAttendanceLogs");

            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.EnteredBy).HasMaxLength(100).IsRequired();

            // Still no uniqueness constraint, unlike AttendanceLogs -- a manual
            // entry typed by hand through the single-entry dialog (AddAsync) is
            // still never rejected as a duplicate at this level, only meant to be
            // caught and deleted by the person afterward if it turns out to be one
            // (see IManualAttendanceLogRepository.DeleteAsync). Bulk import
            // (AddRangeAsync) dedups its own batch against what's already here, but
            // does so as an in-app check before insert, not by leaning on a
            // constraint here -- see that method's own doc comment for why.
            //
            // Range queries (GetLogsAsync) filter by Timestamp only, same as
            // AttendanceLogs -- see its own Timestamp index above for why that
            // needs its own index rather than relying on a composite one.
            e.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<DayPunchPairing>(e =>
        {
            e.ToTable("DayPunchPairings");

            e.Property(x => x.EditedBy).HasMaxLength(100).IsRequired();

            // "One hand-edited pairing per employee per day" -- the same shape,
            // and the same enforcement role, as ScheduleEntries' own
            // (EmployeeId, Date) unique index above, and what makes
            // SqlDayPunchPairingRepository.SaveAsync a real upsert rather than a
            // check-then-insert that could double up under a concurrent save.
            // Also the lookup index AttendanceWorkflowService's per-period fetch
            // uses. No FK to Employee, unlike ScheduleEntry: EmployeeId here is
            // the punch clock's Pin, and this table follows the punch-log tables'
            // convention (see AttendanceLog.EmployeeId) rather than the schedule
            // side's -- a pairing can outlive the employee row the same way a
            // punch can.
            e.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique();
        });

        modelBuilder.Entity<DayPunchPairingSlot>(e =>
        {
            e.ToTable("DayPunchPairingSlots");

            // Stored as a string, same reasoning as AttendanceLog.Source above --
            // the raw table stays readable without a lookup ("In"/"Out" rather
            // than 0/1), and adding a role later wouldn't renumber anything
            // already persisted.
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(3).IsRequired();

            e.HasOne(x => x.DayPunchPairing)
                .WithMany(p => p.Slots)
                .HasForeignKey(x => x.DayPunchPairingId)
                // A pairing and its slots are one unit -- deleting the pairing
                // ("reset to automatic") takes its slots with it, same reasoning
                // as FlexibleSegment's cascade off ScheduleEntry above.
                .OnDelete(DeleteBehavior.Cascade);

            // The FK lookup index for loading a pairing's slots. Not unique: one
            // pairing legitimately has many slots, and (PunchId, IsManualPunch)
            // isn't constrained either -- a corrupt duplicate is tolerated and
            // resolved last-wins at read time rather than blocking a save (see
            // OverriddenFlexibleShiftCalculationStrategy's overrideMap).
            e.HasIndex(x => x.DayPunchPairingId);
        });

        modelBuilder.Entity<UserAccount>(e =>
        {
            e.ToTable("UserAccounts");

            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordSalt).HasMaxLength(200).IsRequired();

            // The actual enforcement behind SqlUserAccountRepository.AddAsync's
            // check-then-act pre-check -- see its IsUniqueUsernameViolation for the
            // race-condition backstop this index makes possible. SQL Server's default
            // collation is already case-insensitive, so this alone is enough to reject
            // "Admin" while "admin" already exists, without a separate normalized
            // column.
            e.HasIndex(x => x.Username).IsUnique();

            e.Property(x => x.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<PayrollAdjustment>(e =>
        {
            e.ToTable("PayrollAdjustments");

            // Stored as a string, same reasoning as AttendanceLog.Source above --
            // the raw table stays readable without a lookup, and adding a new
            // PayrollAdjustmentType later doesn't renumber anything already persisted.
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20).IsRequired();

            e.Property(x => x.Amount).HasColumnType("decimal(10,2)");
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.EnteredBy).HasMaxLength(100).IsRequired();

            // GetForEmployeePeriodAsync always filters by this exact triple -- this is
            // what makes that a range scan instead of a table scan once
            // PayrollAdjustments has real history in it. Not unique: multiple rows per
            // employee/period/type are expected and normal (see PayrollAdjustment's own
            // doc comment), so nothing here should ever reject a second Allowance row
            // for the same period.
            e.HasIndex(x => new { x.EmployeeId, x.PeriodStart, x.PeriodEnd });
        });

        modelBuilder.Entity<PayrollUndertimeWaiver>(e =>
        {
            e.ToTable("PayrollUndertimeWaivers");

            // Unlike PayrollAdjustments' non-unique index on the same three columns,
            // this one IS unique -- a second row for the same employee/period would be
            // meaningless (there's no Amount/Description to differentiate two rows the
            // way PayrollAdjustment has; presence alone is the whole signal, see
            // PayrollUndertimeWaiver's own doc comment). This is also the actual
            // guarantee behind PayrollUndertimeWaiverRepository.SetWaivedAsync's
            // check-then-insert, same "backstop, not the primary guard" role
            // PayrollAdjustments' own indexes play for their repository.
            e.HasIndex(x => new { x.EmployeeId, x.PeriodStart, x.PeriodEnd }).IsUnique();
        });

        modelBuilder.Entity<PayrollRun>(e =>
        {
            e.ToTable("PayrollRuns");

            e.Property(x => x.Label).HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();

            // "Load Payroll Group…" lists newest-first over this -- see
            // IPayrollRunRepository.ListAsync.
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<PayrollRunEmployee>(e =>
        {
            e.ToTable("PayrollRunEmployees");

            e.HasOne(x => x.PayrollRun)
                .WithMany(r => r.Employees)
                .HasForeignKey(x => x.PayrollRunId)
                // A run and its membership rows are one unit -- deleting a saved
                // PayrollRun (see IPayrollRunRepository.DeleteAsync) takes its
                // PayrollRunEmployee rows with it, same reasoning as
                // FlexibleSegment's cascade off ScheduleEntry above.
                .OnDelete(DeleteBehavior.Cascade);

            // Backstop against ever inserting the same employee twice into one
            // run's membership list -- same non-primary-guard role
            // PayrollAdjustments' own indexes play (see
            // PayrollAdjustmentRepository.EnsureNoExistingRowAsync), just
            // enforced here at the database level since
            // PayrollRunRepository.CreateAsync writes the whole membership
            // list in one shot rather than one row at a time. Also serves as
            // the FK lookup index for PayrollRunId (same "composite index
            // covers the FK convention" precedent as ScheduleEntries'
            // (EmployeeId, Date) unique index above -- no separate
            // PayrollRunId-only index needed).
            e.HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique();
        });
    }
}