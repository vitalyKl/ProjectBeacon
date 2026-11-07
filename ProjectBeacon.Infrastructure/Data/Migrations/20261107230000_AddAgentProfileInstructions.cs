using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261107230000_AddAgentProfileInstructions")]
public sealed class AddAgentProfileInstructions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PlannerInstructions",
            table: "AgentTemplates",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ActorInstructions",
            table: "AgentTemplates",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ReviewInstructions",
            table: "AgentTemplates",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ProfileInstructions",
            table: "RoleBindings",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PlannerInstructions", table: "AgentTemplates");
        migrationBuilder.DropColumn(name: "ActorInstructions", table: "AgentTemplates");
        migrationBuilder.DropColumn(name: "ReviewInstructions", table: "AgentTemplates");
        migrationBuilder.DropColumn(name: "ProfileInstructions", table: "RoleBindings");
    }
}
