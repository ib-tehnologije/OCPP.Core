using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    public partial class AddPublicPortalSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicPortalSettings",
                columns: table => new
                {
                    PublicPortalSettingsId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BrandName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Tagline = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    SupportEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SupportPhone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HelpUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FooterCompanyLine = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FooterAddressLine = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FooterLegalLine = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CanonicalBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SeoTitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HeaderLogoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FooterLogoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    QrScannerEnabled = table.Column<bool>(type: "bit", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "getutcdate()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "getutcdate()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicPortalSettings", x => x.PublicPortalSettingsId);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicPortalSettings");
        }
    }
}
