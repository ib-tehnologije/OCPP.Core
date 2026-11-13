using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicPortalTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrivacyUrl",
                table: "PublicPortalSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireTermsAcceptance",
                table: "PublicPortalSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsUrl",
                table: "PublicPortalSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "PublicPortalSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TermsAcceptedAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "ChargePaymentReservation",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrivacyUrl",
                table: "PublicPortalSettings");

            migrationBuilder.DropColumn(
                name: "RequireTermsAcceptance",
                table: "PublicPortalSettings");

            migrationBuilder.DropColumn(
                name: "TermsUrl",
                table: "PublicPortalSettings");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "PublicPortalSettings");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "ChargePaymentReservation");
        }
    }
}
