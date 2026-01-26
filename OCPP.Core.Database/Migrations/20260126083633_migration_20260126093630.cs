using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20260126093630 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AwaitingPlug",
                table: "ChargePaymentReservation",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "ChargePaymentReservation",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureMessage",
                table: "ChargePaymentReservation",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastOcppEventAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OcppIdTag",
                table: "ChargePaymentReservation",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemoteStartAcceptedAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteStartResult",
                table: "ChargePaymentReservation",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemoteStartSentAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDeadlineAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartTransactionAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartTransactionId",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StopTransactionAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ActiveConnectorKey",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed','StartRejected','StartTimeout','Abandoned') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldComputedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReservations_CpConnTag",
                table: "ChargePaymentReservation",
                columns: new[] { "ChargePointId", "ConnectorId", "OcppIdTag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentReservations_CpConnTag",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AwaitingPlug",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "FailureMessage",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "LastOcppEventAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "OcppIdTag",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "RemoteStartAcceptedAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "RemoteStartResult",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "RemoteStartSentAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "StartDeadlineAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "StartTransactionAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "StartTransactionId",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "StopTransactionAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.AlterColumn<string>(
                name: "ActiveConnectorKey",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldComputedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed','StartRejected','StartTimeout','Abandoned') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END");
        }
    }
}
