using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261120000200_AddBookingReschedules")]
public sealed class AddBookingReschedules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "RescheduledFromId", table: "Bookings", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex(name: "ux_bookings_rescheduled_from", table: "Bookings",
            column: "RescheduledFromId", unique: true);
        migrationBuilder.AddForeignKey(name: "FK_Bookings_Bookings_RescheduledFromId", table: "Bookings",
            column: "RescheduledFromId", principalTable: "Bookings", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_Bookings_Bookings_RescheduledFromId", table: "Bookings");
        migrationBuilder.DropIndex(name: "ux_bookings_rescheduled_from", table: "Bookings");
        migrationBuilder.DropColumn(name: "RescheduledFromId", table: "Bookings");
    }
}
