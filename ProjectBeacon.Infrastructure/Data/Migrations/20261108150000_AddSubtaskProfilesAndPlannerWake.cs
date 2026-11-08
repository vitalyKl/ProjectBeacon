using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261108150000_AddSubtaskProfilesAndPlannerWake")]
public sealed class AddSubtaskProfilesAndPlannerWake : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfileKey",
            table: "Subtasks",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "WakePlannerAfter",
            table: "Subtasks",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "ProfileSnapshotJson",
            table: "Subtasks",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AgentProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Instruction = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ToolsJson = table.Column<string>(type: "text", nullable: false),
                PathPolicy = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                DefaultPathsJson = table.Column<string>(type: "text", nullable: false),
                CommandsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                ReportsDiff = table.Column<bool>(type: "boolean", nullable: false),
                SizeHint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Effort = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentProfiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_AgentProfiles_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentProfiles_UserId",
            table: "AgentProfiles",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AgentProfiles_UserId_Key",
            table: "AgentProfiles",
            columns: new[] { "UserId", "Key" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProfileKey", table: "Subtasks");
        migrationBuilder.DropTable(name: "AgentProfiles");
        migrationBuilder.DropColumn(name: "ProfileSnapshotJson", table: "Subtasks");
        migrationBuilder.DropColumn(name: "WakePlannerAfter", table: "Subtasks");
    }
}