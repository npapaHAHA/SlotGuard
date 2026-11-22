using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicResourceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublicLinkId",
                table: "Resources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicPhotoUrl",
                table: "Resources",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_PublicLinkId",
                table: "Resources",
                column: "PublicLinkId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_PublicLinkId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "PublicLinkId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "PublicPhotoUrl",
                table: "Resources");
        }
    }
}
