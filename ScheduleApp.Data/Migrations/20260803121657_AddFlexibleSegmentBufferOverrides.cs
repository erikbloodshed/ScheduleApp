using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlexibleSegmentBufferOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ClockInBufferHours",
                table: "FlexibleSegments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClockOutBufferHours",
                table: "FlexibleSegments",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockInBufferHours",
                table: "FlexibleSegments");

            migrationBuilder.DropColumn(
                name: "ClockOutBufferHours",
                table: "FlexibleSegments");
        }
    }
}
