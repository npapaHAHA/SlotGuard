using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261121000200_AddPrivateBookingLinks")]
public sealed class AddPrivateBookingLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("PrivateBookingLinks", table => new
        {
            Id = table.Column<Guid>("uuid", nullable: false),
            ResourceId = table.Column<string>("character varying(32)", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
            ExpiresAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
            MaxUses = table.Column<int>("integer", nullable: false),
            UsedCount = table.Column<int>("integer", nullable: false),
            EmailDomain = table.Column<string>("character varying(253)", maxLength: 253, nullable: true),
            RevokedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_PrivateBookingLinks", item => item.Id);
            table.ForeignKey("FK_PrivateBookingLinks_Resources_ResourceId", item => item.ResourceId, "Resources", "Id",
                onDelete: ReferentialAction.Restrict);
            table.CheckConstraint("ck_private_booking_links_usage",
                "\"MaxUses\" BETWEEN 1 AND 1000 AND \"UsedCount\" BETWEEN 0 AND \"MaxUses\"");
        });
        migrationBuilder.CreateIndex("IX_PrivateBookingLinks_ResourceId", "PrivateBookingLinks", "ResourceId");
        migrationBuilder.AddColumn<Guid>("PrivateLinkId", "Bookings", "uuid", nullable: true);
        migrationBuilder.CreateIndex("IX_Bookings_PrivateLinkId", "Bookings", "PrivateLinkId");
        migrationBuilder.AddForeignKey("FK_Bookings_PrivateBookingLinks_PrivateLinkId", "Bookings", "PrivateLinkId",
            "PrivateBookingLinks", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Bookings_PrivateBookingLinks_PrivateLinkId", "Bookings");
        migrationBuilder.DropIndex("IX_Bookings_PrivateLinkId", "Bookings");
        migrationBuilder.DropColumn("PrivateLinkId", "Bookings");
        migrationBuilder.DropTable("PrivateBookingLinks");
    }
}
