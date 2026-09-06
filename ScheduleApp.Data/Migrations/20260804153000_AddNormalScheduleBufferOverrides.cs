using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNormalScheduleBufferOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ClockInBufferBeforeHours",
                table: "ScheduleEntries",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClockInBufferAfterHours",
                table: "ScheduleEntries",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClockOutBufferBeforeHours",
                table: "ScheduleEntries",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClockOutBufferAfterHours",
                table: "ScheduleEntries",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockInBufferBeforeHours",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "ClockInBufferAfterHours",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "ClockOutBufferBeforeHours",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "ClockOutBufferAfterHours",
                table: "ScheduleEntries");
        }
    }
}
