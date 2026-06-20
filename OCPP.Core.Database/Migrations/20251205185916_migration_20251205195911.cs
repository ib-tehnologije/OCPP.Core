using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20251205195911 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FreeReason",
                table: "Transactions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChargeTagPrivilege",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TagId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChargePointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FreeChargingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "getutcdate()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeTagPrivilege", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChargeTagPrivilege_ChargePoint_ChargePointId",
                        column: x => x.ChargePointId,
                        principalTable: "ChargePoint",
                        principalColumn: "ChargePointId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChargeTagPrivilege_ChargeTags_TagId",
                        column: x => x.TagId,
                        principalTable: "ChargeTags",
                        principalColumn: "TagId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeTagPrivilege_ChargePointId",
                table: "ChargeTagPrivilege",
                column: "ChargePointId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeTagPrivilege_Tag_Point",
                table: "ChargeTagPrivilege",
                columns: new[] { "TagId", "ChargePointId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargeTagPrivilege");

            migrationBuilder.DropColumn(
                name: "FreeReason",
                table: "Transactions");
        }
    }
}
