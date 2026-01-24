using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20260124183500 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StripeWebhookEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StripeCreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeWebhookEvents", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StripeWebhookEvents_ProcessedAtUtc",
                table: "StripeWebhookEvents",
                column: "ProcessedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeWebhookEvents");
        }
    }
}
