using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261120000100_AddBookingIntervals")]
public sealed class AddBookingIntervals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SELECT pg_advisory_xact_lock(714873020041);
            CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA public;

            ALTER TABLE "Bookings"
                ADD COLUMN "EndUtc" timestamptz,
                ADD COLUMN "OccupiedFromUtc" timestamptz,
                ADD COLUMN "OccupiedUntilUtc" timestamptz;

            UPDATE "Bookings"
                SET "EndUtc" = "StartUtc" + INTERVAL '60 minutes',
                    "OccupiedFromUtc" = "StartUtc",
                    "OccupiedUntilUtc" = "StartUtc" + INTERVAL '60 minutes';

            ALTER TABLE "Bookings"
                ALTER COLUMN "EndUtc" SET NOT NULL,
                ALTER COLUMN "OccupiedFromUtc" SET NOT NULL,
                ALTER COLUMN "OccupiedUntilUtc" SET NOT NULL,
                ADD CONSTRAINT ck_bookings_window CHECK (
                    "OccupiedFromUtc" <= "StartUtc" AND "StartUtc" < "EndUtc" AND "EndUtc" <= "OccupiedUntilUtc"
                ),
                ADD CONSTRAINT ex_bookings_active_window EXCLUDE USING gist (
                    "ResourceId" WITH =,
                    tstzrange("OccupiedFromUtc", "OccupiedUntilUtc", '[)') WITH &&
                ) WHERE ("CancelledAt" IS NULL);

            ALTER TABLE "WaitlistEntries" ADD COLUMN "DurationMinutes" integer NOT NULL DEFAULT 60;
            DROP INDEX "IX_WaitlistEntries_ResourceId_StartUtc_OwnerId";
            CREATE UNIQUE INDEX ux_waitlist_active_request
                ON "WaitlistEntries" ("ResourceId", "StartUtc", "OwnerId", "DurationMinutes") WHERE "RemovedAt" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX ux_waitlist_active_request;
            ALTER TABLE "WaitlistEntries" DROP COLUMN "DurationMinutes";
            CREATE UNIQUE INDEX "IX_WaitlistEntries_ResourceId_StartUtc_OwnerId"
                ON "WaitlistEntries" ("ResourceId", "StartUtc", "OwnerId") WHERE "RemovedAt" IS NULL;

            ALTER TABLE "Bookings"
                DROP CONSTRAINT ex_bookings_active_window,
                DROP CONSTRAINT ck_bookings_window,
                DROP COLUMN "OccupiedUntilUtc",
                DROP COLUMN "OccupiedFromUtc",
                DROP COLUMN "EndUtc";
            """);
    }
}
