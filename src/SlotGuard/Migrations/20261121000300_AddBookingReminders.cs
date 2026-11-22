using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261121000300_AddBookingReminders")]
public sealed class AddBookingReminders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("Kind", "BookingEmails", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.DropPrimaryKey("PK_BookingEmails", "BookingEmails");
        migrationBuilder.AddPrimaryKey("PK_BookingEmails", "BookingEmails", ["BookingId", "Kind"]);
        migrationBuilder.Sql("""
            UPDATE "Bookings" AS booking
            SET "ContactEmail" = account."Email"
            FROM "AspNetUsers" AS account, "Resources" AS resource
            WHERE booking."ContactEmail" IS NULL AND booking."OwnerId" = account."Id"
                AND account."EmailConfirmed" AND NULLIF(btrim(account."Email"), '') IS NOT NULL
                AND booking."ResourceId" = resource."Id" AND NOT resource."IsArchived"
                AND booking."StartUtc" >= CURRENT_TIMESTAMP + interval '1 hour'
                AND booking."CancelledAt" IS NULL AND booking."HoldExpiresAt" IS NULL
                AND booking."MaintenanceReason" IS NULL
                AND (booking."ApprovalStatus" IS NULL OR booking."ApprovalStatus" = 'approved');

            INSERT INTO "BookingEmails" ("BookingId", "Kind", "Attempts", "NextAttemptAt")
            SELECT booking."Id", notice.hours, 0, booking."StartUtc" - make_interval(hours => notice.hours)
            FROM "Bookings" AS booking
            JOIN "Resources" AS resource ON resource."Id" = booking."ResourceId"
            CROSS JOIN (VALUES (24), (1)) AS notice(hours)
            WHERE booking."StartUtc" - make_interval(hours => notice.hours) >= CURRENT_TIMESTAMP
                AND NOT resource."IsArchived" AND NULLIF(btrim(booking."ContactEmail"), '') IS NOT NULL
                AND booking."CancelledAt" IS NULL AND booking."HoldExpiresAt" IS NULL
                AND booking."MaintenanceReason" IS NULL
                AND (booking."ApprovalStatus" IS NULL OR booking."ApprovalStatus" = 'approved')
            ON CONFLICT ("BookingId", "Kind") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DELETE FROM "BookingEmails" WHERE "Kind" <> 0;""");
        migrationBuilder.DropPrimaryKey("PK_BookingEmails", "BookingEmails");
        migrationBuilder.DropColumn("Kind", "BookingEmails");
        migrationBuilder.AddPrimaryKey("PK_BookingEmails", "BookingEmails", "BookingId");
    }
}
