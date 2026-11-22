using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupSeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeatApprovalRequired",
                table: "Bookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SeatCapacity",
                table: "Bookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionTitle",
                table: "Bookings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SeatReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecisionReason = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: true),
                    Attendance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AttendanceAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeatReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeatReservations_Bookings_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_EndUtc",
                table: "Bookings",
                column: "EndUtc",
                filter: "\"SeatCapacity\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SeatReservations_OwnerId_CreatedAt",
                table: "SeatReservations",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_group_seats_active_owner",
                table: "SeatReservations",
                columns: new[] { "SessionId", "OwnerId" },
                unique: true,
                filter: "\"Status\" IN ('pending', 'confirmed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeatReservations");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_EndUtc",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SeatApprovalRequired",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SeatCapacity",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SessionTitle",
                table: "Bookings");
        }
    }
}
