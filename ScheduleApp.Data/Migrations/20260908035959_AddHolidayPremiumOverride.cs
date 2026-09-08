using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds Employee.HolidayPremiumPercentage -- a nullable decimal(5,4) override column,
    /// same shape as RestDayWorkPremiumPercentage (see AddRestDayPremiumOverride), where
    /// null means "inherit PayrollPolicy.HolidayPremiumPercentage" (1.00 by default -- one
    /// full extra day, PH labor law's worked-regular-holiday rate).
    ///
    /// Unlike that migration, there's no data backfill here: this column is new from the
    /// start, not converted from an existing non-nullable one, so every row is simply
    /// added as null (inherit) with nothing to correct.
    /// </summary>
    public partial class AddHolidayPremiumOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HolidayPremiumPercentage",
                table: "Employees",
                type: "decimal(5,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HolidayPremiumPercentage",
                table: "Employees");
        }
    }
}
