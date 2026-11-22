using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddEventsAndDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_group_seats_active_owner",
                table: "SeatReservations");

            migrationBuilder.AddColumn<string>(
                name: "GuestEmail",
                table: "SeatReservations",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuestName",
                table: "SeatReservations",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoverCity",
                table: "Resources",
                type: "character varying(70)",
                maxLength: 70,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DiscoverFeatured",
                table: "Resources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "EventMails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventMails_SeatReservations_SeatId",
                        column: x => x.SeatId,
                        principalTable: "SeatReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishedEvents",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    City = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    OpensAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosesAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CoHostId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedEvents", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_PublishedEvents_Bookings_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_group_seats_active_owner",
                table: "SeatReservations",
                columns: new[] { "SessionId", "OwnerId" },
                unique: true,
                filter: "\"Status\" IN ('pending', 'confirmed', 'waiting')");

            migrationBuilder.CreateIndex(
                name: "IX_EventMails_NextAttemptAt",
                table: "EventMails",
                column: "NextAttemptAt",
                filter: "\"CompletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EventMails_SeatId",
                table: "EventMails",
                column: "SeatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventMails");

            migrationBuilder.DropTable(
                name: "PublishedEvents");

            migrationBuilder.DropIndex(
                name: "ux_group_seats_active_owner",
                table: "SeatReservations");

            migrationBuilder.DropColumn(
                name: "GuestEmail",
                table: "SeatReservations");

            migrationBuilder.DropColumn(
                name: "GuestName",
                table: "SeatReservations");

            migrationBuilder.DropColumn(
                name: "DiscoverCity",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "DiscoverFeatured",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "ux_group_seats_active_owner",
                table: "SeatReservations",
                columns: new[] { "SessionId", "OwnerId" },
                unique: true,
                filter: "\"Status\" IN ('pending', 'confirmed')");
        }
    }
}
