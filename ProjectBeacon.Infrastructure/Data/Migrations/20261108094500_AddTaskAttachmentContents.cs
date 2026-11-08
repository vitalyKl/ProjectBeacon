using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261108094500_AddTaskAttachmentContents")]
public sealed class AddTaskAttachmentContents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaskAttachmentContents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                TaskAttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                Content = table.Column<byte[]>(type: "bytea", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskAttachmentContents", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskAttachmentContents_TaskAttachments_TaskAttachmentId",
                    column: x => x.TaskAttachmentId,
                    principalTable: "TaskAttachments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("ALTER TABLE \"TaskAttachmentContents\" ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"TaskAttachmentContents\" FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("""
            CREATE POLICY tenant_project_isolation ON "TaskAttachmentContents"
                USING (current_setting('app.tenant_unscoped', true) = 'true'
                       OR "ProjectId"::text = current_setting('app.tenant_project_id', true));
            """);

        migrationBuilder.CreateIndex(
            name: "IX_TaskAttachmentContents_ProjectId",
            table: "TaskAttachmentContents",
            column: "ProjectId");
        migrationBuilder.CreateIndex(
            name: "IX_TaskAttachmentContents_TaskAttachmentId",
            table: "TaskAttachmentContents",
            column: "TaskAttachmentId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "TaskAttachmentContents");
}
