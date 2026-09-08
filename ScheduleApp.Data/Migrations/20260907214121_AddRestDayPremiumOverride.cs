using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Turns Employee.RestDayWorkPremiumPercentage from a non-nullable column
    /// defaulting to 0 into a nullable *override* column, where null means "inherit
    /// PayrollPolicy.RestDayPremiumPercentage" (0.30 by default -- PH labor law's
    /// 130% rest day rate). Same nullable-means-inherit shape the employee-level
    /// buffer defaults already use (see AddEmployeeBufferDefaults), so the
    /// HasDefaultValue that used to back this column is dropped along with it.
    ///
    /// The UPDATE below is the load-bearing half, not a tidy-up. Every existing row
    /// holds a non-null 0.0000 purely because the column was non-nullable with a 0
    /// default -- left as-is, each of those would read as a deliberate "0% premium"
    /// override and suppress the new company default for every employee on file,
    /// making the whole rest-day-rate change a no-op in production while passing
    /// every test. A stored 0 here means "nobody ever configured this" (the field
    /// was opt-in and started at 0), not "this employee is deliberately paid straight
    /// time for rest day work" -- which wouldn't be lawful anyway. An employee with a
    /// real non-zero premium keeps it untouched.
    ///
    /// Down reverses both halves in the order the schema requires: nulls have to
    /// become 0 *before* the column can go back to NOT NULL. That collapses the
    /// inherit-vs-explicitly-zero distinction, which is inherent to going back to a
    /// column that can't express it.
    /// </summary>
    public partial class AddRestDayPremiumOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "RestDayWorkPremiumPercentage",
                table: "Employees",
                type: "decimal(5,4)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldDefaultValue: 0m);

            // See this migration's own remarks -- a stored 0 is "never configured",
            // and has to become null so the company default can apply.
            migrationBuilder.Sql(
                "UPDATE Employees SET RestDayWorkPremiumPercentage = NULL WHERE RestDayWorkPremiumPercentage = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Must run first: the AlterColumn below can't make a column NOT NULL
            // while any row still holds a null.
            migrationBuilder.Sql(
                "UPDATE Employees SET RestDayWorkPremiumPercentage = 0 WHERE RestDayWorkPremiumPercentage IS NULL;");

            migrationBuilder.AlterColumn<decimal>(
                name: "RestDayWorkPremiumPercentage",
                table: "Employees",
                type: "decimal(5,4)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldNullable: true);
        }
    }
}
