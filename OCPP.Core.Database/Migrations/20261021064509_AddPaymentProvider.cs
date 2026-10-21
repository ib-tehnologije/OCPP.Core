using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayerEmail",
                table: "ChargePaymentReservation",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerName",
                table: "ChargePaymentReservation",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentProvider",
                table: "ChargePaymentReservation",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Stripe");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayerEmail",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "PayerName",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "PaymentProvider",
                table: "ChargePaymentReservation");
        }
    }
}
