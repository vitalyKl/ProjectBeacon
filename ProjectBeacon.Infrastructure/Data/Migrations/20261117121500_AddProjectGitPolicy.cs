using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261117121500_AddProjectGitPolicy")]
public sealed class AddProjectGitPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "GitPolicyJson",
            table: "Projects",
            type: "jsonb",
            nullable: false,
            defaultValue: "{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}");

        migrationBuilder.AddColumn<int>(
            name: "GitPolicyRevision",
            table: "Projects",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<string>(
            name: "EscalationCode",
            table: "LandingAttempts",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "GitPolicyRevision",
            table: "LandingAttempts",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<string>(
            name: "GitPolicySnapshotJson",
            table: "LandingAttempts",
            type: "jsonb",
            nullable: false,
            defaultValue: "{}");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "GitPolicyJson", table: "Projects");
        migrationBuilder.DropColumn(name: "GitPolicyRevision", table: "Projects");
        migrationBuilder.DropColumn(name: "EscalationCode", table: "LandingAttempts");
        migrationBuilder.DropColumn(name: "GitPolicyRevision", table: "LandingAttempts");
        migrationBuilder.DropColumn(name: "GitPolicySnapshotJson", table: "LandingAttempts");
    }
}
