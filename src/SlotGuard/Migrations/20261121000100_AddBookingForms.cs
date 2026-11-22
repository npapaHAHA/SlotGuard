using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261121000100_AddBookingForms")]
public sealed class AddBookingForms : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("BookingFormDefinition", "Resources", "jsonb", nullable: true);
        migrationBuilder.AddColumn<string>("AnswersJson", "Bookings", "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("BookingFormDefinition", "Resources");
        migrationBuilder.DropColumn("AnswersJson", "Bookings");
    }
}
