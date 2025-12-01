using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class add_owner_entity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Owner",
                columns: table => new
                {
                    OwnerId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Owner", x => x.OwnerId);
                });

            migrationBuilder.AddColumn<int>(
                name: "OwnerId",
                table: "ChargePoint",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(@"
INSERT INTO Owner (Name, Email)
SELECT DISTINCT
    COALESCE(NULLIF(LTRIM(RTRIM(OwnerName)), ''), NULLIF(LTRIM(RTRIM(OwnerEmail)), '')),
    NULLIF(LTRIM(RTRIM(OwnerEmail)), '')
FROM ChargePoint
WHERE (OwnerName IS NOT NULL AND LTRIM(RTRIM(OwnerName)) <> '')
   OR (OwnerEmail IS NOT NULL AND LTRIM(RTRIM(OwnerEmail)) <> '');

UPDATE cp
SET OwnerId = o.OwnerId
FROM ChargePoint cp
INNER JOIN Owner o
    ON ISNULL(LTRIM(RTRIM(cp.OwnerName)), '') = ISNULL(LTRIM(RTRIM(o.Name)), '')
    AND ISNULL(LTRIM(RTRIM(cp.OwnerEmail)), '') = ISNULL(LTRIM(RTRIM(o.Email)), '');
");

            migrationBuilder.CreateIndex(
                name: "IX_ChargePoint_OwnerId",
                table: "ChargePoint",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChargePoint_Owner_OwnerId",
                table: "ChargePoint",
                column: "OwnerId",
                principalTable: "Owner",
                principalColumn: "OwnerId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "OwnerName",
                table: "ChargePoint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "ChargePoint",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerName",
                table: "ChargePoint",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE cp
SET OwnerName = LTRIM(RTRIM(o.Name)),
    OwnerEmail = LTRIM(RTRIM(o.Email))
FROM ChargePoint cp
LEFT JOIN Owner o ON cp.OwnerId = o.OwnerId;
");

            migrationBuilder.DropForeignKey(
                name: "FK_ChargePoint_Owner_OwnerId",
                table: "ChargePoint");

            migrationBuilder.DropIndex(
                name: "IX_ChargePoint_OwnerId",
                table: "ChargePoint");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "ChargePoint");

            migrationBuilder.DropTable(
                name: "Owner");
        }
    }
}
