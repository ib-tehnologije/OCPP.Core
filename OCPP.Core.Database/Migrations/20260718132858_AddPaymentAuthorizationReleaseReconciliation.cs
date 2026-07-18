using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAuthorizationReleaseReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthorizationReleaseAttemptCount",
                table: "ChargePaymentReservation",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthorizationReleaseLastAttemptAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorizationReleaseLastError",
                table: "ChargePaymentReservation",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthorizationReleaseNextAttemptAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorizationReleaseState",
                table: "ChargePaymentReservation",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthorizationReleasedAtUtc",
                table: "ChargePaymentReservation",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentAuthorizationReleaseAttempt",
                columns: table => new
                {
                    PaymentAuthorizationReleaseAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AmountCapturableCents = table.Column<long>(type: "bigint", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NextRetryAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAuthorizationReleaseAttempt", x => x.PaymentAuthorizationReleaseAttemptId);
                    table.ForeignKey(
                        name: "FK_PaymentAuthorizationReleaseAttempt_ChargePaymentReservation_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "ChargePaymentReservation",
                        principalColumn: "ReservationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReservations_AuthorizationReleaseDue",
                table: "ChargePaymentReservation",
                columns: new[] { "AuthorizationReleaseState", "AuthorizationReleaseNextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationReleaseAttempt_NextRetry",
                table: "PaymentAuthorizationReleaseAttempt",
                column: "NextRetryAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationReleaseAttempt_Reservation_Started",
                table: "PaymentAuthorizationReleaseAttempt",
                columns: new[] { "ReservationId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_AuthorizationReleaseAttempt_Reservation_Number",
                table: "PaymentAuthorizationReleaseAttempt",
                columns: new[] { "ReservationId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentAuthorizationReleaseAttempt");

            migrationBuilder.DropIndex(
                name: "IX_PaymentReservations_AuthorizationReleaseDue",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AuthorizationReleaseAttemptCount",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AuthorizationReleaseLastAttemptAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AuthorizationReleaseLastError",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AuthorizationReleaseNextAttemptAtUtc",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AuthorizationReleaseState",
                table: "ChargePaymentReservation");

            migrationBuilder.DropColumn(
                name: "AuthorizationReleasedAtUtc",
                table: "ChargePaymentReservation");
        }
    }
}
