using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOvertimeNightDiffPerDayOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ApplyOvertimeRatePercentageByDefault",
                table: "Employees",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ApplyOvertimeRatePercentageOverride",
                table: "ScheduleEntries",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NightDiffEligibleOverride",
                table: "ScheduleEntries",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NightDiffRatePercentageOverride",
                table: "ScheduleEntries",
                type: "decimal(5,4)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OvertimeEligibleOverride",
                table: "ScheduleEntries",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OvertimeRatePercentageOverride",
                table: "ScheduleEntries",
                type: "decimal(5,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplyOvertimeRatePercentageByDefault",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "ApplyOvertimeRatePercentageOverride",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "NightDiffEligibleOverride",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "NightDiffRatePercentageOverride",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "OvertimeEligibleOverride",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "OvertimeRatePercentageOverride",
                table: "ScheduleEntries");
        }
    }
}
