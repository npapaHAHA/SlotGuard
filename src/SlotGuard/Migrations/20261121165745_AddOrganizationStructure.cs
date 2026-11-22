using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Memberships_OrganizationId",
                table: "Memberships");

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Resources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "Memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrganizationUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(32)", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUnits", x => x.Id);
                    table.UniqueConstraint("AK_OrganizationUnits_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_OrganizationUnits_OrganizationId_BranchId",
                        columns: x => new { x.OrganizationId, x.BranchId },
                        principalTable: "OrganizationUnits",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResourceGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(32)", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceGroups", x => x.Id);
                    table.UniqueConstraint("AK_ResourceGroups_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_ResourceGroups_Memberships_ReviewerId_OrganizationId",
                        columns: x => new { x.ReviewerId, x.OrganizationId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "OwnerId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceGroups_OrganizationUnits_OrganizationId_UnitId",
                        columns: x => new { x.OrganizationId, x.UnitId },
                        principalTable: "OrganizationUnits",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceGroups_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_OrganizationId_GroupId",
                table: "Resources",
                columns: new[] { "OrganizationId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_OrganizationId_UnitId",
                table: "Memberships",
                columns: new[] { "OrganizationId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_OrganizationId_BranchId",
                table: "OrganizationUnits",
                columns: new[] { "OrganizationId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceGroups_OrganizationId_UnitId",
                table: "ResourceGroups",
                columns: new[] { "OrganizationId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceGroups_ReviewerId_OrganizationId",
                table: "ResourceGroups",
                columns: new[] { "ReviewerId", "OrganizationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Memberships_OrganizationUnits_OrganizationId_UnitId",
                table: "Memberships",
                columns: new[] { "OrganizationId", "UnitId" },
                principalTable: "OrganizationUnits",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Resources_ResourceGroups_OrganizationId_GroupId",
                table: "Resources",
                columns: new[] { "OrganizationId", "GroupId" },
                principalTable: "ResourceGroups",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Memberships_OrganizationUnits_OrganizationId_UnitId",
                table: "Memberships");

            migrationBuilder.DropForeignKey(
                name: "FK_Resources_ResourceGroups_OrganizationId_GroupId",
                table: "Resources");

            migrationBuilder.DropTable(
                name: "ResourceGroups");

            migrationBuilder.DropTable(
                name: "OrganizationUnits");

            migrationBuilder.DropIndex(
                name: "IX_Resources_OrganizationId_GroupId",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Memberships_OrganizationId_UnitId",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "Memberships");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_OrganizationId",
                table: "Memberships",
                column: "OrganizationId");
        }
    }
}
