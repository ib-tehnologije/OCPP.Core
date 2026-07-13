using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceBuyerSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerCity",
                table: "ChargePaymentReservation",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerCompanyName",
                table: "ChargePaymentReservation",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceBuyerConfirmedAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerCountry",
                table: "ChargePaymentReservation",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerEmail",
                table: "ChargePaymentReservation",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InvoiceBuyerIdentifierIsVatRegistration",
                table: "ChargePaymentReservation",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerPostalCode",
                table: "ChargePaymentReservation",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerRegistrationNumber",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerStreet",
                table: "ChargePaymentReservation",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerTaxIdentifier",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceBuyerCity",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerCompanyName",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerConfirmedAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerCountry",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerEmail",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerIdentifierIsVatRegistration",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerPostalCode",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerRegistrationNumber",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerStreet",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerTaxIdentifier",
                table: "ChargePaymentReservation");
        }
    }
}
