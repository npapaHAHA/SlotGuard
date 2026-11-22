using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OfferedBookingId",
                table: "WaitlistEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasApproved",
                table: "SeatReservations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                WITH matches AS (
                    SELECT w."Id" AS "RequestId", b."Id" AS "BookingId",
                        count(*) OVER (PARTITION BY w."Id") AS "Offers",
                        count(*) OVER (PARTITION BY b."Id") AS "Requests"
                    FROM "WaitlistEntries" w
                    JOIN "HoldOutbox" h ON h."CreatedAt" = w."RemovedAt"
                    JOIN "Bookings" b ON b."Id" = h."BookingId" AND b."ResourceId" = w."ResourceId"
                        AND b."OwnerId" = w."OwnerId" AND b."StartUtc" = w."StartUtc"
                        AND b."EndUtc" = w."StartUtc" + w."DurationMinutes" * interval '1 minute'
                )
                UPDATE "WaitlistEntries" w SET "OfferedBookingId" = m."BookingId"
                FROM matches m WHERE w."Id" = m."RequestId" AND m."Offers" = 1 AND m."Requests" = 1;

                UPDATE "SeatReservations" seat SET "WasApproved" = TRUE
                FROM "Bookings" session
                WHERE seat."SessionId" = session."Id"
                    AND session."SeatApprovalRequired" AND seat."Status" = 'confirmed';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_OfferedBookingId",
                table: "WaitlistEntries",
                column: "OfferedBookingId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WaitlistEntries_Bookings_OfferedBookingId",
                table: "WaitlistEntries",
                column: "OfferedBookingId",
                principalTable: "Bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WaitlistEntries_Bookings_OfferedBookingId",
                table: "WaitlistEntries");

            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_OfferedBookingId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "OfferedBookingId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "WasApproved",
                table: "SeatReservations");
        }
    }
}
