using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSplitShiftAndFlexibleRestrictedTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ScheduleType.SplitShift (=4) needs no column change of its own --
            // ScheduleEntries.ScheduleType is a plain int, so the new enum value
            // round-trips through the existing column. These two are the only
            // actual schema change this migration makes: Flexible's new optional
            // single-window restriction (see ScheduleEntry.RestrictedTimeIn/
            // RestrictedTimeOut). No backfill -- every existing row's
            // ScheduleType stays exactly what it is today, and these two columns
            // start out null everywhere (see ScheduleTimeRefactor Phase 2).
            migrationBuilder.AddColumn<TimeOnly>(
                name: "RestrictedTimeIn",
                table: "ScheduleEntries",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "RestrictedTimeOut",
                table: "ScheduleEntries",
                type: "time",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestrictedTimeIn",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "RestrictedTimeOut",
                table: "ScheduleEntries");
        }
    }
}
