using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261106120000_AddTaskAttachments")]
public sealed class AddTaskAttachments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaskAttachments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Length = table.Column<long>(type: "bigint", nullable: false),
                StorageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedByType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskAttachments", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskAttachments_Tasks_TaskId",
                    column: x => x.TaskId,
                    principalTable: "Tasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("ALTER TABLE \"TaskAttachments\" ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"TaskAttachments\" FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql(@"CREATE POLICY tenant_project_isolation ON \"TaskAttachments\"
            USING (current_setting('app.tenant_unscoped', true) = 'true'
                   OR \"ProjectId\"::text = current_setting('app.tenant_project_id', true));");

        migrationBuilder.CreateIndex(
            name: "IX_TaskAttachments_ProjectId", table: "TaskAttachments", column: "ProjectId");
        migrationBuilder.CreateIndex(
            name: "IX_TaskAttachments_StorageKey", table: "TaskAttachments", column: "StorageKey", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_TaskAttachments_TaskId_CreatedAt", table: "TaskAttachments", columns: new[] { "TaskId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "TaskAttachments");
}
