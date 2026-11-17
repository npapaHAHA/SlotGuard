using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceAvailabilityDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Capacity",
                table: "Resources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "Equipment",
                table: "Resources",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Resources",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "other");

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "electronics",
                columns: new[] { "Capacity", "Equipment", "Type" },
                values: new object[] { 1, new[] { "вытяжка", "термофен" }, "equipment" });

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "meeting-room",
                columns: new[] { "Capacity", "Equipment", "Type" },
                values: new object[] { 8, new[] { "видеосвязь", "доска", "экран" }, "room" });

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "microscope",
                columns: new[] { "Capacity", "Equipment", "Type" },
                values: new object[] { 2, new[] { "камера" }, "equipment" });

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "podcast-room",
                columns: new[] { "Capacity", "Equipment", "Type" },
                values: new object[] { 4, new[] { "аудиоинтерфейс", "микрофон" }, "studio" });

            migrationBuilder.UpdateData(
                table: "Resources",
                keyColumn: "Id",
                keyValue: "studio",
                columns: new[] { "Capacity", "Equipment", "Type" },
                values: new object[] { 4, new[] { "аудиоинтерфейс", "микрофон" }, "studio" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Capacity",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "Equipment",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Resources");
        }
    }
}
