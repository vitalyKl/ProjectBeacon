using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261107220000_AddTaskWorkspaceSections")]
public sealed class AddTaskWorkspaceSections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaskWorkspaceSections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Abstract = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Content = table.Column<string>(type: "text", nullable: false),
                AuthorType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                AuthorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                AuthorRole = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                AuthorProfileKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                AuthorSubtaskId = table.Column<Guid>(type: "uuid", nullable: true),
                AuthorSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                Revision = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskWorkspaceSections", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskWorkspaceSections_Tasks_TaskId",
                    column: x => x.TaskId,
                    principalTable: "Tasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("ALTER TABLE \"TaskWorkspaceSections\" ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"TaskWorkspaceSections\" FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("""
            CREATE POLICY tenant_project_isolation ON "TaskWorkspaceSections"
                USING (current_setting('app.tenant_unscoped', true) = 'true'
                       OR "ProjectId"::text = current_setting('app.tenant_project_id', true));
            """);

        migrationBuilder.CreateIndex(
            name: "IX_TaskWorkspaceSections_ProjectId",
            table: "TaskWorkspaceSections",
            column: "ProjectId");
        migrationBuilder.CreateIndex(
            name: "IX_TaskWorkspaceSections_TaskId_Key",
            table: "TaskWorkspaceSections",
            columns: new[] { "TaskId", "Key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_TaskWorkspaceSections_TaskId_UpdatedAt",
            table: "TaskWorkspaceSections",
            columns: new[] { "TaskId", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "TaskWorkspaceSections");
}
