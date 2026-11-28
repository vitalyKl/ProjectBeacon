using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124170000_AddReleaseCandidateSnapshots")]
public sealed partial class AddReleaseCandidateSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ReleaseCandidateSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TagName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                TargetBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                BaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PreparedCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CandidateSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CandidateTreeSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                VersionPolicyRevision = table.Column<int>(type: "integer", nullable: false),
                ReleaseRevision = table.Column<int>(type: "integer", nullable: false),
                ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                PreparedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReleaseCandidateSnapshots", x => x.Id);
                table.UniqueConstraint(
                    "AK_ReleaseCandidateSnapshots_ProjectId_ReleaseId",
                    x => new { x.ProjectId, x.ReleaseId });
                table.ForeignKey(
                    name: "FK_ReleaseCandidateSnapshots_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ReleaseCandidateSnapshots_Releases_ProjectId_ReleaseId",
                    columns: x => new { x.ProjectId, x.ReleaseId },
                    principalTable: "Releases",
                    principalColumns: new[] { "ProjectId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReleaseCandidateSnapshots_ProjectId_CandidateSha",
            table: "ReleaseCandidateSnapshots",
            columns: new[] { "ProjectId", "CandidateSha" });

        migrationBuilder.Sql("ALTER TABLE \"ReleaseCandidateSnapshots\" ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"ReleaseCandidateSnapshots\" FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("CREATE POLICY tenant_project_isolation ON \"ReleaseCandidateSnapshots\" USING (current_setting('app.tenant_unscoped', true) = 'true' OR \"ProjectId\"::text = current_setting('app.tenant_project_id', true));");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_project_isolation ON \"ReleaseCandidateSnapshots\";");
        migrationBuilder.Sql("ALTER TABLE \"ReleaseCandidateSnapshots\" NO FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql("ALTER TABLE \"ReleaseCandidateSnapshots\" DISABLE ROW LEVEL SECURITY;");
        migrationBuilder.DropTable(name: "ReleaseCandidateSnapshots");
    }
}
