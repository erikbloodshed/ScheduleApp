using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScheduleApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDayPunchPairings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DayPunchPairings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    EditedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EditedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayPunchPairings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DayPunchPairingSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DayPunchPairingId = table.Column<int>(type: "int", nullable: false),
                    PunchId = table.Column<int>(type: "int", nullable: false),
                    IsManualPunch = table.Column<bool>(type: "bit", nullable: false),
                    SegmentIndex = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayPunchPairingSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DayPunchPairingSlots_DayPunchPairings_DayPunchPairingId",
                        column: x => x.DayPunchPairingId,
                        principalTable: "DayPunchPairings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DayPunchPairings_EmployeeId_Date",
                table: "DayPunchPairings",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DayPunchPairingSlots_DayPunchPairingId",
                table: "DayPunchPairingSlots",
                column: "DayPunchPairingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DayPunchPairingSlots");

            migrationBuilder.DropTable(
                name: "DayPunchPairings");
        }
    }
}
