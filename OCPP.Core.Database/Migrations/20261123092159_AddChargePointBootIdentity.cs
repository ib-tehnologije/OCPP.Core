using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddChargePointBootIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChargeBoxSerialNumber",
                table: "ChargePoint",
                type: "nvarchar(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChargePointModel",
                table: "ChargePoint",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChargePointSerialNumber",
                table: "ChargePoint",
                type: "nvarchar(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChargePointVendor",
                table: "ChargePoint",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirmwareVersion",
                table: "ChargePoint",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Iccid",
                table: "ChargePoint",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Imsi",
                table: "ChargePoint",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeterSerialNumber",
                table: "ChargePoint",
                type: "nvarchar(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeterType",
                table: "ChargePoint",
                type: "nvarchar(25)",
                maxLength: 25,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargeBoxSerialNumber",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "ChargePointModel",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "ChargePointSerialNumber",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "ChargePointVendor",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "FirmwareVersion",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "Iccid",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "Imsi",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "MeterSerialNumber",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "MeterType",
                table: "ChargePoint");
        }
    }
}
