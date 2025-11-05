using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class StripePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChargePaymentReservation",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConnectorId = table.Column<int>(type: "int", nullable: false),
                    ChargeTagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MaxEnergyKwh = table.Column<double>(type: "float", nullable: false),
                    PricePerKwh = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MaxAmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AuthorizedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TransactionId = table.Column<int>(type: "int", nullable: true),
                    CapturedAmountCents = table.Column<long>(type: "bigint", nullable: true),
                    ActualEnergyKwh = table.Column<double>(type: "float", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargePaymentReservation", x => x.ReservationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReservations_StripeSession",
                table: "ChargePaymentReservation",
                column: "StripeCheckoutSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReservations_PaymentIntent",
                table: "ChargePaymentReservation",
                column: "StripePaymentIntentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargePaymentReservation");
        }
    }
}
