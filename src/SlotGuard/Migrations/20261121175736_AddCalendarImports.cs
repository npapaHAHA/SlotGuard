using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CalendarImportId",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CalendarImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(32)", nullable: false),
                    Revision = table.Column<string>(type: "character varying(97)", maxLength: 97, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarImports_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CalendarImportId",
                table: "Bookings",
                column: "CalendarImportId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarImports_ResourceId_Revision",
                table: "CalendarImports",
                columns: new[] { "ResourceId", "Revision" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_CalendarImports_CalendarImportId",
                table: "Bookings",
                column: "CalendarImportId",
                principalTable: "CalendarImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_CalendarImports_CalendarImportId",
                table: "Bookings");

            migrationBuilder.DropTable(
                name: "CalendarImports");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_CalendarImportId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CalendarImportId",
                table: "Bookings");
        }
    }
}
