using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

/// <summary>Persists the Plan/Act mode selected for each workstation chat session.</summary>
[DbContext(typeof(BeaconDbContext))]
[Migration("20261031191800_AddChatSessionAgentMode")]
public sealed class AddChatSessionAgentMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AgentMode",
            table: "ChatSessions",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "build");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AgentMode",
            table: "ChatSessions");
    }
}
