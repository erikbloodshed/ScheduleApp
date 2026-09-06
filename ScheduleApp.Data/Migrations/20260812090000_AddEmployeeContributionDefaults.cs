using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeContributionDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DefaultSss",
                table: "Employees",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultPhilHealth",
                table: "Employees",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultPagIbig",
                table: "Employees",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultSss",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "DefaultPhilHealth",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "DefaultPagIbig",
                table: "Employees");
        }
    }
}
