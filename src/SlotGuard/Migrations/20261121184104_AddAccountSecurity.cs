using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "FactorRecoveryHashes",
                table: "AspNetUsers",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<long>(
                name: "LastAuthenticatorStep",
                table: "AspNetUsers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingFactorId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedAuthenticatorKey",
                table: "AspNetUsers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FactorRecoveryHashes",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LastAuthenticatorStep",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PendingFactorId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ProtectedAuthenticatorKey",
                table: "AspNetUsers");
        }
    }
}
