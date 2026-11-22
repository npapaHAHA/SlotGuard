using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261121000500_AddResourceBundles")]
public sealed class AddResourceBundles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "BundleId", table: "Bookings", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_Bookings_BundleId", table: "Bookings", column: "BundleId", filter: "\"BundleId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Bookings_BundleId", table: "Bookings");
        migrationBuilder.DropColumn(name: "BundleId", table: "Bookings");
    }
}
