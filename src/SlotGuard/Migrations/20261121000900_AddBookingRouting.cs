using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoutingContext",
                table: "Bookings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoutingReviewerId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingRouteRules",
                columns: table => new
                {
                    OrganizationId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Equipment = table.Column<string[]>(type: "text[]", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingRouteRules", x => new { x.OrganizationId, x.Purpose });
                    table.ForeignKey(
                        name: "FK_BookingRouteRules_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_RoutingReviewerId",
                table: "Bookings",
                column: "RoutingReviewerId",
                filter: "\"RoutingReviewerId\" IS NOT NULL AND \"CancelledAt\" IS NULL AND \"ApprovalStatus\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_BookingRouteRules_ReviewerId",
                table: "BookingRouteRules",
                column: "ReviewerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingRouteRules");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_RoutingReviewerId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "RoutingContext",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "RoutingReviewerId",
                table: "Bookings");
        }
    }
}
