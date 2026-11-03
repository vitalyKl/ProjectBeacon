using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

/// <summary>Stores encrypted, project-scoped MCP server configuration.</summary>
[DbContext(typeof(BeaconDbContext))]
[Migration("20261103190000_AddProjectMcpServers")]
public sealed class AddProjectMcpServers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "McpServersCipher",
            table: "Projects",
            type: "text",
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "McpServersCipher",
            table: "Projects");
    }
}
