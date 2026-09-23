using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCPP.Core.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeterEvidencePowerCandidateProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CandidateOfferedPowerMultiplier",
                table: "MeterEvidenceAnomaly",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateOfferedPowerRawValue",
                table: "MeterEvidenceAnomaly",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateOfferedPowerUnit",
                table: "MeterEvidenceAnomaly",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateOfferedPowerMultiplier",
                table: "MeterEvidenceAnomaly");

            migrationBuilder.DropColumn(
                name: "CandidateOfferedPowerRawValue",
                table: "MeterEvidenceAnomaly");

            migrationBuilder.DropColumn(
                name: "CandidateOfferedPowerUnit",
                table: "MeterEvidenceAnomaly");
        }
    }
}
