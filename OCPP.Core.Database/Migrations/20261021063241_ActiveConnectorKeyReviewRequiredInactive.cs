using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class ActiveConnectorKeyReviewRequiredInactive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PaymentReservations_ActiveConnector",
                table: "ChargePaymentReservation");

            migrationBuilder.AlterColumn<string>(
                name: "ActiveConnectorKey",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed','ReviewRequired','StartRejected','StartTimeout','Abandoned') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldComputedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed','StartRejected','StartTimeout','Abandoned') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentReservations_ActiveConnector",
                table: "ChargePaymentReservation",
                columns: new[] { "ChargePointId", "ConnectorId", "ActiveConnectorKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PaymentReservations_ActiveConnector",
                table: "ChargePaymentReservation");

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
                oldComputedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed','ReviewRequired','StartRejected','StartTimeout','Abandoned') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentReservations_ActiveConnector",
                table: "ChargePaymentReservation",
                columns: new[] { "ChargePointId", "ConnectorId", "ActiveConnectorKey" },
                unique: true);
        }
    }
}
