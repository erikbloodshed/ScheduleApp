using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeEligibilityFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both default true so every existing employee stays eligible for both
            // (the pre-this-feature behavior) unless someone explicitly opts them out
            // via the Add/Edit Employee dialog afterward.
            migrationBuilder.AddColumn<bool>(
                name: "QualifiesForOvertime",
                table: "Employees",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "QualifiesForNightDiff",
                table: "Employees",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualifiesForOvertime",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "QualifiesForNightDiff",
                table: "Employees");
        }
    }
}
