using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddNightEnergyTariff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "NightEnergyCost",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "NightEnergyKwh",
                table: "Transactions",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "NightTariffEndMinute",
                table: "Transactions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NightTariffStartMinute",
                table: "Transactions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NightTariffTimeZoneId",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NightPricePerKwh",
                table: "ChargePoint",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "NightTariffEnabled",
                table: "ChargePoint",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NightTariffEndMinute",
                table: "ChargePoint",
                type: "int",
                nullable: false,
                defaultValue: 420); // 07:00 local time

            migrationBuilder.AddColumn<int>(
                name: "NightTariffStartMinute",
                table: "ChargePoint",
                type: "int",
                nullable: false,
                defaultValue: 1320); // 22:00 local time

            migrationBuilder.AddColumn<decimal>(
                name: "NightPricePerKwh",
                table: "ChargePaymentReservation",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NightTariffEndMinute",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NightTariffStartMinute",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NightTariffTimeZoneId",
                table: "ChargePaymentReservation",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NightEnergyCost",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "NightEnergyKwh",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "NightTariffEndMinute",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "NightTariffStartMinute",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "NightTariffTimeZoneId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "NightPricePerKwh",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "NightTariffEnabled",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "NightTariffEndMinute",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "NightTariffStartMinute",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "NightPricePerKwh",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "NightTariffEndMinute",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "NightTariffStartMinute",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "NightTariffTimeZoneId",
                table: "ChargePaymentReservation");
        }
    }
}
