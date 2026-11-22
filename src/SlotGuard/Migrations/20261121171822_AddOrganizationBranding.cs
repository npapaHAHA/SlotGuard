using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Branding",
                columns: table => new
                {
                    OrganizationId = table.Column<string>(type: "character varying(32)", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    BannerUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Palette = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletionUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CompletionLabel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    SenderName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ReplyTo = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    SmtpProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branding", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_Branding_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Branding");
        }
    }
}
