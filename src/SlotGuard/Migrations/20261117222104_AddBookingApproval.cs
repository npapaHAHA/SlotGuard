using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                table: "Resources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalReason",
                table: "Bookings",
                type: "character varying(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalStatus",
                table: "Bookings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "electronics",
                column: "RequiresApproval",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "meeting-room",
                column: "RequiresApproval",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "microscope",
                column: "RequiresApproval",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "podcast-room",
                column: "RequiresApproval",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "studio",
                column: "RequiresApproval",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings",
                column: "StartUtc",
                filter: "\"ApprovalStatus\" = 'pending' AND \"CancelledAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "ApprovalReason",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "Bookings");
        }
    }
}
