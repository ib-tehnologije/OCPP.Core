using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceBuyerVatValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerNormalizedVatIdentifier",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerOriginalTaxIdentifier",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerVatValidationStatus",
                table: "ChargePaymentReservation",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceBuyerVatVerificationCheckedAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerVatVerificationReference",
                table: "ChargePaymentReservation",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerVatVerificationStatus",
                table: "ChargePaymentReservation",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceBuyerNormalizedVatIdentifier",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerOriginalTaxIdentifier",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerVatValidationStatus",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerVatVerificationCheckedAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerVatVerificationReference",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerVatVerificationStatus",
                table: "ChargePaymentReservation");
        }
    }
}
