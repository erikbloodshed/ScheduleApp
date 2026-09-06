using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameEmployeeLegacyIdToPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the filtered unique index first -- its predicate text references the
            // column by name ("[LegacyId] IS NOT NULL"), so it needs to be recreated
            // against the new name rather than relying on the rename to carry it over.
            migrationBuilder.DropIndex(
                name: "IX_Employees_LegacyId",
                table: "Employees");

            migrationBuilder.RenameColumn(
                name: "LegacyId",
                table: "Employees",
                newName: "Pin");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Pin",
                table: "Employees",
                column: "Pin",
                unique: true,
                filter: "[Pin] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Employees_Pin",
                table: "Employees");

            migrationBuilder.RenameColumn(
                name: "Pin",
                table: "Employees",
                newName: "LegacyId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_LegacyId",
                table: "Employees",
                column: "LegacyId",
                unique: true,
                filter: "[LegacyId] IS NOT NULL");
        }
    }
}
