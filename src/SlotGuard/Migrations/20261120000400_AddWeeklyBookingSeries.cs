using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261120000400_AddWeeklyBookingSeries")]
public sealed class AddWeeklyBookingSeries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("SeriesId", "Bookings", "uuid", nullable: true);
        migrationBuilder.CreateIndex("IX_Bookings_ResourceId_OwnerId_SeriesId", "Bookings",
            ["ResourceId", "OwnerId", "SeriesId"], filter: "\"SeriesId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Bookings_ResourceId_OwnerId_SeriesId", "Bookings");
        migrationBuilder.DropColumn("SeriesId", "Bookings");
    }
}
