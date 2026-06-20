using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20260306000414 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceSubmissionLog",
                columns: table => new
                {
                    InvoiceSubmissionLogId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransactionId = table.Column<int>(type: "int", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvoiceKind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProviderOperation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApiTransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripeCheckoutSessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    ExternalDocumentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExternalInvoiceNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExternalPublicUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExternalPdfUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProviderResponseStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceSubmissionLog", x => x.InvoiceSubmissionLogId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceSubmissionLog_Created",
                table: "InvoiceSubmissionLog",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceSubmissionLog_Reservation_Created",
                table: "InvoiceSubmissionLog",
                columns: new[] { "ReservationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceSubmissionLog_Transaction",
                table: "InvoiceSubmissionLog",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceSubmissionLog");
        }
    }
}
