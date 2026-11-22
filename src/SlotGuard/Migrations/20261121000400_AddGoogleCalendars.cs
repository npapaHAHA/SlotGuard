using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SlotGuard.Migrations;

[DbContext(typeof(BookingDb))]
[Migration("20261121000400_AddGoogleCalendars")]
public sealed class AddGoogleCalendars : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("GoogleCalendars", table => new
        {
            ResourceId = table.Column<string>("character varying(32)", nullable: false),
            Key = table.Column<Guid>("uuid", nullable: false),
            Revision = table.Column<Guid>("uuid", nullable: false),
            CalendarId = table.Column<string>("text", nullable: false),
            Name = table.Column<string>("text", nullable: false),
            TimeZone = table.Column<string>("text", nullable: false),
            ProtectedRefreshToken = table.Column<string>("text", nullable: true),
            BusyJson = table.Column<string>("jsonb", nullable: false),
            ImportedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
            ExportedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
            NextSyncAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
            Error = table.Column<string>("text", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_GoogleCalendars", link => link.ResourceId);
            table.ForeignKey("FK_GoogleCalendars_Resources_ResourceId", link => link.ResourceId, "Resources", "Id",
                onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_GoogleCalendars_CalendarId", "GoogleCalendars", "CalendarId", unique: true,
            filter: "\"ProtectedRefreshToken\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("GoogleCalendars");
}
