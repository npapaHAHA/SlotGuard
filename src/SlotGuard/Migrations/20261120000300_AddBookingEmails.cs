using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261120000300_AddBookingEmails")]
public sealed class AddBookingEmails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("CalendarRootId", "Bookings", "uuid", nullable: true);
        migrationBuilder.AddColumn<int>("CalendarSequence", "Bookings", "integer", defaultValue: 0);
        migrationBuilder.AddColumn<string>("ContactEmail", "Bookings", "character varying(254)", maxLength: 254, nullable: true);
        migrationBuilder.AddColumn<bool>("GuestManagement", "Bookings", "boolean", defaultValue: false);
        migrationBuilder.Sql("""
            WITH RECURSIVE chain AS (
                SELECT original."Id", original."Id" AS root, 0 AS version
                FROM "Bookings" original
                WHERE original."RescheduledFromId" IS NULL
                    AND EXISTS (SELECT 1 FROM "Bookings" moved WHERE moved."RescheduledFromId" = original."Id")
                UNION ALL
                SELECT moved."Id", chain.root, chain.version + 1
                FROM "Bookings" moved JOIN chain ON moved."RescheduledFromId" = chain."Id"
            )
            UPDATE "Bookings" booking SET "CalendarRootId" = chain.root, "CalendarSequence" = chain.version
            FROM chain WHERE booking."Id" = chain."Id" AND chain.version > 0;
            """);
        migrationBuilder.CreateIndex("IX_Bookings_CalendarRootId", "Bookings", "CalendarRootId");
        migrationBuilder.CreateTable("BookingEmails", table => new
        {
            BookingId = table.Column<Guid>("uuid", nullable: false),
            Attempts = table.Column<int>("integer", nullable: false),
            NextAttemptAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
            CompletedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BookingEmails", item => item.BookingId);
            table.ForeignKey("FK_BookingEmails_Bookings_BookingId", item => item.BookingId, "Bookings", "Id",
                onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_BookingEmails_NextAttemptAt", "BookingEmails", "NextAttemptAt",
            filter: "\"CompletedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BookingEmails");
        migrationBuilder.DropIndex("IX_Bookings_CalendarRootId", "Bookings");
        migrationBuilder.DropColumn("CalendarRootId", "Bookings");
        migrationBuilder.DropColumn("CalendarSequence", "Bookings");
        migrationBuilder.DropColumn("ContactEmail", "Bookings");
        migrationBuilder.DropColumn("GuestManagement", "Bookings");
    }
}
