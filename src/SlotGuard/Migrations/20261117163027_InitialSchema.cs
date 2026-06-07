using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(32)", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookings_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Resources",
                columns: new[] { "Id", "Location", "Name", "SortOrder" },
                values: new object[,]
                {
                    { "electronics", "Мастерская · кабинет 108", "Паяльная станция", 2 },
                    { "microscope", "Лаборатория А · кабинет 204", "Оптический микроскоп", 1 },
                    { "studio", "Медиалаб · кабинет 312", "Студия записи", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "ux_bookings_active_slot",
                table: "Bookings",
                columns: new[] { "ResourceId", "StartUtc" },
                unique: true,
                filter: "\"CancelledAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bookings");

            migrationBuilder.DropTable(
                name: "Resources");
        }
    }
}
