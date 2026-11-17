using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Resources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkspaceActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<string>(type: "character varying(32)", nullable: false),
                    ResourceId = table.Column<string>(type: "text", nullable: false),
                    ResourceName = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceActivities_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "electronics",
                column: "IsArchived",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "meeting-room",
                column: "IsArchived",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "microscope",
                column: "IsArchived",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "podcast-room",
                column: "IsArchived",
                value: false);

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "studio",
                column: "IsArchived",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceActivities_OrganizationId_Id",
                table: "WorkspaceActivities",
                columns: new[] { "OrganizationId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceActivities");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Resources");
        }
    }
}
