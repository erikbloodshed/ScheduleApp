using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaidLeave",
                table: "ScheduleEntries",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DailyRate",
                table: "Employees",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "PayrollAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EnteredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdjustments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustments_EmployeeId_PeriodStart_PeriodEnd",
                table: "PayrollAdjustments",
                columns: new[] { "EmployeeId", "PeriodStart", "PeriodEnd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayrollAdjustments");

            migrationBuilder.DropColumn(
                name: "IsPaidLeave",
                table: "ScheduleEntries");

            migrationBuilder.DropColumn(
                name: "DailyRate",
                table: "Employees");
        }
    }
}
