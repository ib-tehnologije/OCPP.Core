using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeterEvidenceSafeguard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedMeterAtUtc",
                table: "Transactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AcceptedMeterKwh",
                table: "Transactions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AcceptedMeterToleranceKwh",
                table: "Transactions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeterEvidenceReason",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeterEvidenceState",
                table: "Transactions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrustedMaximumPowerAtUtc",
                table: "Transactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TrustedMaximumPowerKw",
                table: "Transactions",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustedMaximumPowerSource",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TrustedMaximumPowerToleranceKw",
                table: "Transactions",
                type: "float",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeterEvidenceAnomaly",
                columns: table => new
                {
                    MeterEvidenceAnomalyId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TransactionId = table.Column<int>(type: "int", nullable: false),
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConnectorId = table.Column<int>(type: "int", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RawValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RawUnit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RawUnitMultiplier = table.Column<int>(type: "int", nullable: false),
                    EvidenceKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NormalizedMeterKwh = table.Column<double>(type: "float", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AcceptedMeterKwh = table.Column<double>(type: "float", nullable: true),
                    AcceptedMeterAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TrustedMaximumPowerKw = table.Column<double>(type: "float", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterEvidenceAnomaly", x => x.MeterEvidenceAnomalyId);
                    table.ForeignKey(
                        name: "FK_MeterEvidenceAnomaly_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvidenceAnomaly_TransactionId_EvidenceKey",
                table: "MeterEvidenceAnomaly",
                columns: new[] { "TransactionId", "EvidenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvidenceAnomaly_TransactionId_ObservedAtUtc",
                table: "MeterEvidenceAnomaly",
                columns: new[] { "TransactionId", "ObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeterEvidenceAnomaly");

            migrationBuilder.DropColumn(
                name: "AcceptedMeterAtUtc",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "AcceptedMeterKwh",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "AcceptedMeterToleranceKwh",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "MeterEvidenceReason",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "MeterEvidenceState",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "TrustedMaximumPowerAtUtc",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "TrustedMaximumPowerKw",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "TrustedMaximumPowerSource",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "TrustedMaximumPowerToleranceKw",
                table: "Transactions");
        }
    }
}
