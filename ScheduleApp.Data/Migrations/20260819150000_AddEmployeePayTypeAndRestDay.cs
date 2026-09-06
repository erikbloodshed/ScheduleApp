using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeePayTypeAndRestDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmployeeType",
                table: "Employees",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyRate",
                table: "Employees",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RestDayWorkPremiumPercentage",
                table: "Employees",
                type: "decimal(5,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmployeeType",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "MonthlyRate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "RestDayWorkPremiumPercentage",
                table: "Employees");
        }
    }
}
