using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HoldExpiresAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WaitlistEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ResourceId = table.Column<string>(type: "character varying(32)", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitlistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaitlistEntries_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_HoldExpiresAt",
                table: "Bookings",
                column: "HoldExpiresAt",
                filter: "\"CancelledAt\" IS NULL AND \"HoldExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_ResourceId_StartUtc_Id",
                table: "WaitlistEntries",
                columns: new[] { "ResourceId", "StartUtc", "Id" },
                filter: "\"RemovedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_ResourceId_StartUtc_OwnerId",
                table: "WaitlistEntries",
                columns: new[] { "ResourceId", "StartUtc", "OwnerId" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaitlistEntries");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_HoldExpiresAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "HoldExpiresAt",
                table: "Bookings");
        }
    }
}
