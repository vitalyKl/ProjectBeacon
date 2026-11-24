using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124180000_AddReleasePreparationState")]
public sealed partial class AddReleasePreparationState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PreparationRevision",
            table: "Releases",
            type: "integer",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddColumn<string>(
            name: "PreparationStatus",
            table: "Releases",
            type: "text",
            nullable: false,
            defaultValue: "NotStarted");
        migrationBuilder.AddColumn<Guid>(
            name: "PreparationDeviceId",
            table: "Releases",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "CandidateId",
            table: "Releases",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "PreparationCommandId",
            table: "Releases",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "VerificationCommandId",
            table: "Releases",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PreparationPayloadJson",
            table: "Releases",
            type: "jsonb",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PreparationResultJson",
            table: "Releases",
            type: "jsonb",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PreparationError",
            table: "Releases",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "PreparationUpdatedAtUtc",
            table: "Releases",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CandidateId",
            table: "ReleaseCandidateSnapshots",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);
        migrationBuilder.AddColumn<Guid>(
            name: "PreparedByDeviceId",
            table: "ReleaseCandidateSnapshots",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.CreateIndex(
            name: "IX_Releases_ProjectId_PreparationDeviceId_PreparationStatus",
            table: "Releases",
            columns: new[] { "ProjectId", "PreparationDeviceId", "PreparationStatus" });

        migrationBuilder.CreateIndex(
            name: "IX_ReleaseCandidateSnapshots_CandidateId",
            table: "ReleaseCandidateSnapshots",
            column: "CandidateId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Releases_ProjectId_PreparationDeviceId_PreparationStatus",
            table: "Releases");
        migrationBuilder.DropIndex(
            name: "IX_ReleaseCandidateSnapshots_CandidateId",
            table: "ReleaseCandidateSnapshots");

        migrationBuilder.DropColumn(name: "PreparationRevision", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationStatus", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationDeviceId", table: "Releases");
        migrationBuilder.DropColumn(name: "CandidateId", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationCommandId", table: "Releases");
        migrationBuilder.DropColumn(name: "VerificationCommandId", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationPayloadJson", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationResultJson", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationError", table: "Releases");
        migrationBuilder.DropColumn(name: "PreparationUpdatedAtUtc", table: "Releases");
        migrationBuilder.DropColumn(name: "CandidateId", table: "ReleaseCandidateSnapshots");
        migrationBuilder.DropColumn(name: "PreparedByDeviceId", table: "ReleaseCandidateSnapshots");
    }
}
