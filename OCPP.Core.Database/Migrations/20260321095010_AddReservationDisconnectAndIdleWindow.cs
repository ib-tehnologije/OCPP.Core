using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationDisconnectAndIdleWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdleFeeExcludedWindow",
                table: "PublicPortalSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IdleFeeExcludedWindowEnabled",
                table: "PublicPortalSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisconnectedAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdleFeeExcludedWindow",
                table: "PublicPortalSettings");

            migrationBuilder.DropColumn(
                name: "IdleFeeExcludedWindowEnabled",
                table: "PublicPortalSettings");

            migrationBuilder.DropColumn(
                name: "DisconnectedAtUtc",
                table: "ChargePaymentReservation");
        }
    }
}
