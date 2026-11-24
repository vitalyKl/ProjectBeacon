using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124183000_AddReleasePublicationEvidence")]
public sealed partial class AddReleasePublicationEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DeployedCommitSha",
            table: "Releases",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "DeployedAtUtc",
            table: "Releases",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeployedVersion",
            table: "Releases",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "DeploymentWorkflowRunId",
            table: "Releases",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeploymentWorkflowRunUrl",
            table: "Releases",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PublicationError",
            table: "Releases",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PublicationCommandId",
            table: "Releases",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PublicationDeviceId",
            table: "Releases",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PublicationPayloadJson",
            table: "Releases",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PublicationRevision",
            table: "Releases",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<string>(
            name: "PublicationResultJson",
            table: "Releases",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PublicationStatus",
            table: "Releases",
            type: "text",
            nullable: false,
            defaultValue: "NotStarted");

        migrationBuilder.AddColumn<bool>(
            name: "PublicationTagPushed",
            table: "Releases",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "PublicationUpdatedAtUtc",
            table: "Releases",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PublishedTagName",
            table: "Releases",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DeployedCommitSha", table: "Releases");
        migrationBuilder.DropColumn(name: "DeployedAtUtc", table: "Releases");
        migrationBuilder.DropColumn(name: "DeployedVersion", table: "Releases");
        migrationBuilder.DropColumn(name: "DeploymentWorkflowRunId", table: "Releases");
        migrationBuilder.DropColumn(name: "DeploymentWorkflowRunUrl", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationError", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationCommandId", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationDeviceId", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationPayloadJson", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationRevision", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationResultJson", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationStatus", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationTagPushed", table: "Releases");
        migrationBuilder.DropColumn(name: "PublicationUpdatedAtUtc", table: "Releases");
        migrationBuilder.DropColumn(name: "PublishedTagName", table: "Releases");
    }
}
