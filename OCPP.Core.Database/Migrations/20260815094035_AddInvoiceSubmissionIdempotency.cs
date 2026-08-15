using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceSubmissionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmissionKey",
                table: "InvoiceSubmissionLog",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_InvoiceSubmissionLog_SubmissionKey",
                table: "InvoiceSubmissionLog",
                column: "SubmissionKey",
                unique: true,
                filter: "[SubmissionKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InvoiceSubmissionLog_SubmissionKey",
                table: "InvoiceSubmissionLog");

            migrationBuilder.DropColumn(
                name: "SubmissionKey",
                table: "InvoiceSubmissionLog");
        }
    }
}
