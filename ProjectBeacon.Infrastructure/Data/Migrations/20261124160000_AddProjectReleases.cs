using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124160000_AddProjectReleases")]
public sealed partial class AddProjectReleases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Releases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                TargetVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                TargetBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                Order = table.Column<int>(type: "integer", nullable: false),
                Revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Releases", x => x.Id);
                table.UniqueConstraint("AK_Releases_ProjectId_Id", x => new { x.ProjectId, x.Id });
                table.ForeignKey(
                    name: "FK_Releases_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "ReleaseId",
            table: "Milestones",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Releases_ProjectId_Status_Order",
            table: "Releases",
            columns: new[] { "ProjectId", "Status", "Order" });

        migrationBuilder.CreateIndex(
            name: "IX_Milestones_ProjectId_ReleaseId_Order",
            table: "Milestones",
            columns: new[] { "ProjectId", "ReleaseId", "Order" });

        migrationBuilder.CreateIndex(
            name: "IX_Milestones_ReleaseId",
            table: "Milestones",
            column: "ReleaseId");

        migrationBuilder.AddForeignKey(
            name: "FK_Milestones_Releases_ProjectId_ReleaseId",
            table: "Milestones",
            columns: new[] { "ProjectId", "ReleaseId" },
            principalTable: "Releases",
            principalColumns: new[] { "ProjectId", "Id" },
            onDelete: ReferentialAction.Restrict);

        // Releases are project-scoped just like every other customer-owned roadmap row.
        migrationBuilder.Sql(@"ALTER TABLE ""Releases"" ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql(@"ALTER TABLE ""Releases"" FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql(@"CREATE POLICY tenant_project_isolation ON ""Releases""
            USING (current_setting('app.tenant_unscoped', true) = 'true'
                   OR ""ProjectId""::text = current_setting('app.tenant_project_id', true));");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"DROP POLICY IF EXISTS tenant_project_isolation ON ""Releases"";");
        migrationBuilder.Sql(@"ALTER TABLE ""Releases"" NO FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql(@"ALTER TABLE ""Releases"" DISABLE ROW LEVEL SECURITY;");

        migrationBuilder.DropForeignKey(
            name: "FK_Milestones_Releases_ProjectId_ReleaseId",
            table: "Milestones");

        migrationBuilder.DropIndex(
            name: "IX_Milestones_ProjectId_ReleaseId_Order",
            table: "Milestones");

        migrationBuilder.DropIndex(
            name: "IX_Milestones_ReleaseId",
            table: "Milestones");

        migrationBuilder.DropColumn(
            name: "ReleaseId",
            table: "Milestones");

        migrationBuilder.DropTable(
            name: "Releases");
    }
}
