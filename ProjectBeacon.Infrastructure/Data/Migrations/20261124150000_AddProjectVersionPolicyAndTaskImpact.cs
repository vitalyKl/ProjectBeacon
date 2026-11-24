using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124150000_AddProjectVersionPolicyAndTaskImpact")]
public sealed partial class AddProjectVersionPolicyAndTaskImpact : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OfficialVersion",
            table: "Projects",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VersionPolicyJson",
            table: "Projects",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'{\\"scheme\\":\\"SemVer\\",\\"componentCount\\":3,\\"calendarFormat\\":null,\\"tagPattern\\":\\"v{version}\\",\\"allowPrerelease\\":false,\\"sources\\":[]}'::jsonb");

        migrationBuilder.AddColumn<int>(
            name: "VersionPolicyRevision",
            table: "Projects",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<DateTime>(
            name: "VersionImpactAt",
            table: "Tasks",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VersionImpact",
            table: "Tasks",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VersionImpactRationale",
            table: "Tasks",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "OfficialVersion", table: "Projects");
        migrationBuilder.DropColumn(name: "VersionPolicyJson", table: "Projects");
        migrationBuilder.DropColumn(name: "VersionPolicyRevision", table: "Projects");
        migrationBuilder.DropColumn(name: "VersionImpactAt", table: "Tasks");
        migrationBuilder.DropColumn(name: "VersionImpact", table: "Tasks");
        migrationBuilder.DropColumn(name: "VersionImpactRationale", table: "Tasks");
    }
}
