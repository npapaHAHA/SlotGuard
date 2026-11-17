using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationsAndMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OrganizationId",
                table: "Resources",
                type: "character varying(32)",
                nullable: false,
                defaultValue: "university");

            migrationBuilder.Sql("""ALTER TABLE "Resources" ALTER COLUMN "OrganizationId" DROP DEFAULT;""");

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceReason",
                table: "Bookings",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(32)", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.OwnerId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_Memberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Organizations",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { "coworking", "Коворкинг" },
                    { "university", "Университет" }
                });

            migrationBuilder.InsertData(
                table: "Memberships",
                columns: new[] { "OrganizationId", "OwnerId", "Role" },
                values: new object[,]
                {
                    { "coworking", new Guid("1e72ddf8-da00-45fe-9fe6-63ca75a7919a"), "admin" },
                    { "coworking", new Guid("9e4f30ad-030d-4d28-929a-f028f82f4421"), "member" },
                    { "university", new Guid("9e4f30ad-030d-4d28-929a-f028f82f4421"), "admin" }
                });

            migrationBuilder.InsertData(
                table: "Resources",
                columns: new[] { "Id", "Location", "Name", "OrganizationId", "SortOrder" },
                values: new object[,]
                {
                    { "meeting-room", "Коворкинг · кабинет 403", "Переговорная", "coworking", 1 },
                    { "podcast-room", "Коворкинг · кабинет 405", "Подкаст-студия", "coworking", 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_OrganizationId",
                table: "Resources",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_OrganizationId",
                table: "Memberships",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Resources_Organizations_OrganizationId",
                table: "Resources",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Resources_Organizations_OrganizationId",
                table: "Resources");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Resources_OrganizationId",
                table: "Resources");

            migrationBuilder.DeleteData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "meeting-room");

            migrationBuilder.DeleteData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "podcast-room");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "MaintenanceReason",
                table: "Bookings");
        }
    }
}
