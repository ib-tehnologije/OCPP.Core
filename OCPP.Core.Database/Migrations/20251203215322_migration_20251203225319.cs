using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20251203225319 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveConnectorKey",
                table: "ChargePaymentReservation",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END");

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

            migrationBuilder.DropColumn(
                name: "ActiveConnectorKey",
                table: "ChargePaymentReservation");
        }
    }
}
