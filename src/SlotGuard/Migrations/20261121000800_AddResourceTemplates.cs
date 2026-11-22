using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_OrganizationId",
                table: "Resources");

            migrationBuilder.AddColumn<Guid>(
                name: "TemplateId",
                table: "Resources",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ResourceTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceTemplates", x => x.Id);
                    table.UniqueConstraint("AK_ResourceTemplates_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_ResourceTemplates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_OrganizationId_TemplateId",
                table: "Resources",
                columns: new[] { "OrganizationId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "ux_resource_templates_org_name",
                table: "ResourceTemplates",
                columns: new[] { "OrganizationId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Resources_ResourceTemplates_OrganizationId_TemplateId",
                table: "Resources",
                columns: new[] { "OrganizationId", "TemplateId" },
                principalTable: "ResourceTemplates",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Resources_ResourceTemplates_OrganizationId_TemplateId",
                table: "Resources");

            migrationBuilder.DropTable(
                name: "ResourceTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Resources_OrganizationId_TemplateId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_OrganizationId",
                table: "Resources",
                column: "OrganizationId");
        }
    }
}
