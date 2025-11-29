using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20251129212247 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChargePaymentReservation",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConnectorId = table.Column<int>(type: "int", nullable: false),
                    ChargeTagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MaxEnergyKwh = table.Column<double>(type: "float", nullable: false),
                    PricePerKwh = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UserSessionFee = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerSessionFee = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerCommissionPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerCommissionFixedPerKwh = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MaxAmountCents = table.Column<long>(type: "bigint", nullable: false),
                    UsageFeePerMinute = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    StartUsageFeeAfterMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxUsageFeeMinutes = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StripeCheckoutSessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AuthorizedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TransactionId = table.Column<int>(type: "int", nullable: true),
                    CapturedAmountCents = table.Column<long>(type: "bigint", nullable: true),
                    ActualEnergyKwh = table.Column<double>(type: "float", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargePaymentReservation", x => x.ReservationId);
                });

            migrationBuilder.CreateTable(
                name: "ChargePoint",
                columns: table => new
                {
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Password = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClientCertThumb = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FreeChargingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PricePerKwh = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UserSessionFee = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerSessionFee = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerCommissionPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerCommissionFixedPerKwh = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MaxSessionKwh = table.Column<double>(type: "float", nullable: false),
                    StartUsageFeeAfterMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxUsageFeeMinutes = table.Column<int>(type: "int", nullable: false),
                    ConnectorUsageFeePerMinute = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OwnerEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargePoint", x => x.ChargePointId);
                });

            migrationBuilder.CreateTable(
                name: "ChargeTags",
                columns: table => new
                {
                    TagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TagName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ParentTagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Blocked = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeKeys", x => x.TagId);
                });

            migrationBuilder.CreateTable(
                name: "MessageLog",
                columns: table => new
                {
                    LogId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LogTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConnectorId = table.Column<int>(type: "int", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageLog", x => x.LogId);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorStatus",
                columns: table => new
                {
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConnectorId = table.Column<int>(type: "int", nullable: false),
                    ConnectorName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastStatusTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastMeter = table.Column<double>(type: "float", nullable: true),
                    LastMeterTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorStatus", x => new { x.ChargePointId, x.ConnectorId });
                    table.ForeignKey(
                        name: "FK_ConnectorStatus_ChargePoint_ChargePointId",
                        column: x => x.ChargePointId,
                        principalTable: "ChargePoint",
                        principalColumn: "ChargePointId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    TransactionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uid = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConnectorId = table.Column<int>(type: "int", nullable: false),
                    StartTagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    StartTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MeterStart = table.Column<double>(type: "float", nullable: false),
                    StartResult = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StopTagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    StopTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MeterStop = table.Column<double>(type: "float", nullable: true),
                    StopReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EnergyKwh = table.Column<double>(type: "float", nullable: false),
                    EnergyCost = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UsageFeeMinutes = table.Column<int>(type: "int", nullable: false),
                    UsageFeeAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UserSessionFeeAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerSessionFeeAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerCommissionPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerCommissionFixedPerKwh = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OperatorCommissionAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OperatorRevenueTotal = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OwnerPayoutTotal = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.TransactionId);
                    table.ForeignKey(
                        name: "FK_Transactions_ChargePoint",
                        column: x => x.ChargePointId,
                        principalTable: "ChargePoint",
                        principalColumn: "ChargePointId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReservations_PaymentIntent",
                table: "ChargePaymentReservation",
                column: "StripePaymentIntentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReservations_StripeSession",
                table: "ChargePaymentReservation",
                column: "StripeCheckoutSessionId");

            migrationBuilder.CreateIndex(
                name: "ChargePoint_Identifier",
                table: "ChargePoint",
                column: "ChargePointId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageLog_ChargePointId",
                table: "MessageLog",
                column: "LogTime");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ChargePointId_ConnectorId",
                table: "Transactions",
                columns: new[] { "ChargePointId", "ConnectorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargePaymentReservation");

            migrationBuilder.DropTable(
                name: "ChargeTags");

            migrationBuilder.DropTable(
                name: "ConnectorStatus");

            migrationBuilder.DropTable(
                name: "MessageLog");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "ChargePoint");
        }
    }
}
