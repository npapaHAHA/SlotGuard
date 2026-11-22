using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PriceMinor",
                table: "Resources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentRefundedAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceMinor",
                table: "Bookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_resources_price",
                table: "Resources",
                sql: "\"PriceMinor\" BETWEEN 100 AND 100000000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_bookings_payment",
                table: "Bookings",
                sql: "(\"PriceMinor\" IS NULL AND \"PaidAt\" IS NULL AND \"PaymentRefundedAt\" IS NULL)\nOR (\"PriceMinor\" IS NOT NULL AND \"PriceMinor\" BETWEEN 100 AND 100000000\n    AND (\"PaidAt\" IS NOT NULL OR \"HoldExpiresAt\" IS NOT NULL)\n    AND (\"PaymentRefundedAt\" IS NULL OR (\"PaidAt\" IS NOT NULL AND \"CancelledAt\" IS NOT NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_resources_price",
                table: "Resources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_bookings_payment",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PriceMinor",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PaymentRefundedAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PriceMinor",
                table: "Bookings");
        }
    }
}
