using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class OwnerReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerId",
                table: "ChargePoint",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PricePerKwh",
                table: "ChargePoint",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChargeStationOwner",
                columns: table => new
                {
                    OwnerId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProvisionPercentage = table.Column<decimal>(type: "decimal(5,2)", nullable: false, defaultValue: 0m),
                    LastReportYear = table.Column<int>(type: "int", nullable: true),
                    LastReportMonth = table.Column<int>(type: "int", nullable: true),
                    LastReportSentAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeStationOwner", x => x.OwnerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChargePoint_OwnerId",
                table: "ChargePoint",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChargePoint_Owner",
                table: "ChargePoint",
                column: "OwnerId",
                principalTable: "ChargeStationOwner",
                principalColumn: "OwnerId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorStatus_ChargePoint_ChargePointId",
                table: "ConnectorStatus",
                column: "ChargePointId",
                principalTable: "ChargePoint",
                principalColumn: "ChargePointId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChargePoint_Owner",
                table: "ChargePoint");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorStatus_ChargePoint_ChargePointId",
                table: "ConnectorStatus");

            migrationBuilder.DropTable(
                name: "ChargeStationOwner");

            migrationBuilder.DropIndex(
                name: "IX_ChargePoint_OwnerId",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "PricePerKwh",
                table: "ChargePoint");
        }
    }
}
