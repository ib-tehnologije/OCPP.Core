using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20251201212900 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChargingEndedAtUtc",
                table: "Transactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "IdleUsageFeeAmount",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "IdleUsageFeeMinutes",
                table: "Transactions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "UsageFeeAfterChargingEnds",
                table: "ChargePoint",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UsageFeeAnchorMinutes",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargingEndedAtUtc",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "IdleUsageFeeAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "IdleUsageFeeMinutes",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "UsageFeeAfterChargingEnds",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "UsageFeeAnchorMinutes",
                table: "ChargePaymentReservation");
        }
    }
}
