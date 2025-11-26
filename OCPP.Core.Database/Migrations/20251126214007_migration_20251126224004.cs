using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20251126224004 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ConnectorUsageFeePerMinute",
                table: "ChargePoint",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ChargePoint",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FreeChargingEnabled",
                table: "ChargePoint",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MaxSessionKwh",
                table: "ChargePoint",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsageFeeMinutes",
                table: "ChargePoint",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "PricePerKwh",
                table: "ChargePoint",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "StartUsageFeeAfterMinutes",
                table: "ChargePoint",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsageFeeMinutes",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StartUsageFeeAfterMinutes",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "UsageFeePerMinute",
                table: "ChargePaymentReservation",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConnectorUsageFeePerMinute",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "FreeChargingEnabled",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "MaxSessionKwh",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "MaxUsageFeeMinutes",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "PricePerKwh",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "StartUsageFeeAfterMinutes",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "MaxUsageFeeMinutes",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "StartUsageFeeAfterMinutes",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "UsageFeePerMinute",
                table: "ChargePaymentReservation");
        }
    }
}
