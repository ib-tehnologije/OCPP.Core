using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class migration_20251128143633 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Transactions",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EnergyCost",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "EnergyKwh",
                table: "Transactions",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<decimal>(
                name: "OperatorCommissionAmount",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OperatorRevenueTotal",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerCommissionFixedPerKwh",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerCommissionPercent",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerPayoutTotal",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerSessionFeeAmount",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "UsageFeeAmount",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "UsageFeeMinutes",
                table: "Transactions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "UserSessionFeeAmount",
                table: "Transactions",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "EnergyCost",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "EnergyKwh",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OperatorCommissionAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OperatorRevenueTotal",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OwnerCommissionFixedPerKwh",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OwnerCommissionPercent",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OwnerPayoutTotal",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "OwnerSessionFeeAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "UsageFeeAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "UsageFeeMinutes",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "UserSessionFeeAmount",
                table: "Transactions");
        }
    }
}
