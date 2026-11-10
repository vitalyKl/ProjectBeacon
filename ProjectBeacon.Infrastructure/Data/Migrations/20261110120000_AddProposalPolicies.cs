using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

public sealed partial class AddProposalPolicies : Migration
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
