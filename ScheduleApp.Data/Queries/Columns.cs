using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Core.Users;

namespace ScheduleApp.Data.Queries;

/// <summary>
/// The columns every Dapper read selects for each entity, Id first so a multi-mapped join can
/// split on it. They must match EF's model exactly: a column left out here reads back as its
/// C# default without any error. ColumnsTests checks them against the model, so a new or renamed
/// property fails that test until it's listed.
/// </summary>
internal static class Columns
{
    internal static readonly IReadOnlyDictionary<Type, string[]> ByEntity = new Dictionary<Type, string[]>
    {
        [typeof(Department)] = ["Id", "Name", "SortOrder"],
        [typeof(Holiday)] = ["Id", "Date", "Name"],
        [typeof(Employee)] =
        [
            "Id", "LastName", "FirstName", "Pin", "DepartmentId", "IsBlacklisted", "QualifiesForOvertime",
            "QualifiesForNightDiff", "ApplyOvertimeRatePercentageByDefault", "EmployeeType", "DailyRate", "MonthlyRate",
            "ClockInBufferBeforeHours", "ClockInBufferAfterHours", "ClockOutBufferBeforeHours", "ClockOutBufferAfterHours",
            "DefaultWorkTimeHours", "ExemptFromUndertimeDeduction", "RestDayWorkPremiumPercentage", "QualifiesForRestDayPay",
            "DefaultLeaveIsPaid", "DefaultSss", "DefaultPhilHealth", "DefaultPagIbig", "HolidayPremiumPercentage",
            "QualifiesForPremiumPay", "DefaultPremiumPay", "DefaultAllowance", "DefaultCashAdvance",
        ],
        [typeof(ScheduleEntry)] =
        [
            "Id", "EmployeeId", "ScheduleType", "Date", "WorkTimeHours", "TimeIn", "IsPaidLeave", "RestrictedTimeIn",
            "RestrictedTimeOut", "ClockInBufferBeforeHours", "ClockInBufferAfterHours", "ClockOutBufferBeforeHours",
            "ClockOutBufferAfterHours", "OvertimeEligibleOverride", "NightDiffEligibleOverride",
            "ApplyOvertimeRatePercentageOverride", "OvertimeRatePercentageOverride", "NightDiffRatePercentageOverride",
        ],
        [typeof(FlexibleSegment)] = ["Id", "ScheduleEntryId", "TimeIn", "TimeOut", "ClockInBufferHours", "ClockOutBufferHours"],
        [typeof(AttendanceLog)] = ["Id", "EmployeeId", "Timestamp", "PunchType", "Source", "DeviceSerialNumber", "ImportedAt"],
        [typeof(ManualAttendanceLog)] = ["Id", "EmployeeId", "Timestamp", "PunchType", "Reason", "EnteredBy", "CreatedAt"],
        [typeof(DayPunchPairing)] = ["Id", "EmployeeId", "Date", "EditedBy", "EditedAt"],
        [typeof(DayPunchPairingSlot)] = ["Id", "DayPunchPairingId", "PunchId", "IsManualPunch", "SegmentIndex", "Role"],
        [typeof(UserAccount)] = ["Id", "Username", "PasswordHash", "PasswordSalt", "CreatedAtUtc", "IsActive"],
        [typeof(PayrollAdjustment)] =
            ["Id", "EmployeeId", "PeriodStart", "PeriodEnd", "Type", "Amount", "Description", "EnteredBy", "CreatedAt"],
        [typeof(PayrollUndertimeWaiver)] = ["Id", "EmployeeId", "PeriodStart", "PeriodEnd", "CreatedAt"],
        [typeof(PayrollRun)] = ["Id", "Label", "PeriodStart", "PeriodEnd", "CreatedAt", "CreatedBy"],
        [typeof(PayrollRunEmployee)] = ["Id", "PayrollRunId", "EmployeeId"],
    };

    /// <summary><typeparamref name="T"/>'s columns qualified by <paramref name="alias"/>, for a SELECT list.</summary>
    public static string Of<T>(string alias) => string.Join(", ", ByEntity[typeof(T)].Select(c => $"{alias}.{c}"));
}
