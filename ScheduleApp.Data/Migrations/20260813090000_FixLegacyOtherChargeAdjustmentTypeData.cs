using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Data-only fix, no schema change. PayrollAdjustmentType.OtherCharge was
    /// renamed to PayrollAdjustmentType.Charge in code (see
    /// ScheduleApp.Core.Enums.PayrollAdjustmentType), but PayrollAdjustments.Type
    /// is stored via HasConversion&lt;string&gt;() (see ScheduleDbContext), which
    /// serializes/deserializes using the enum member's *name*, not its
    /// underlying number. That's what lets new adjustment types get added later
    /// without renumbering anything already persisted -- but it also means a
    /// straight *rename* of an existing member isn't free: any row already
    /// holding the old name ("OtherCharge") fails to parse back into the enum
    /// once the code no longer has a member by that name, surfacing as "cannot
    /// convert string value 'OtherCharge' from the database to any value in the
    /// mapped PayrollAdjustmentType enum" the next time that row is read -- e.g.
    /// PayrollViewModel's SeedDefaultContributionsForPeriodAsync, which pulls
    /// every adjustment row for the batch regardless of type. This migration
    /// rewrites any leftover "OtherCharge" rows to "Charge" so they deserialize
    /// again, with the rest of each row (Id, EmployeeId, PeriodStart/End,
    /// Amount, Description, CreatedAt) untouched.
    /// </summary>
    public partial class FixLegacyOtherChargeAdjustmentTypeData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [PayrollAdjustments] SET [Type] = N'Charge' WHERE [Type] = N'OtherCharge';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort reversal only: any row genuinely entered as Charge
            // (added new, after the rename, rather than being one of these
            // renamed-back rows) gets swept back to the retired "OtherCharge"
            // name too, since the two are indistinguishable by the time Down
            // runs.
            migrationBuilder.Sql(
                "UPDATE [PayrollAdjustments] SET [Type] = N'OtherCharge' WHERE [Type] = N'Charge';");
        }
    }
}
