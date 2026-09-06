using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeBlacklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBlacklisted",
                table: "Employees",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsBlacklisted",
                table: "Employees");
        }
    }
}
