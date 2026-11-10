using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110120000_AddProposalPolicies")]
public sealed class AddProposalPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProposalPoliciesJson",
            table: "Projects",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'{}'::jsonb");

        migrationBuilder.AddColumn<int>(
            name: "ProposalPolicyRevision",
            table: "Projects",
            type: "integer",
            nullable: false,
            defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProposalPoliciesJson",
            table: "Projects");

        migrationBuilder.DropColumn(
            name: "ProposalPolicyRevision",
            table: "Projects");
    }
}
