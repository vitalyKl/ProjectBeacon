using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124232000_AddRemoteLandingProofs")]
public sealed partial class AddRemoteLandingProofs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RemoteLandingProofs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                ReviewRunId = table.Column<Guid>(type: "uuid", nullable: false),
                GitHubRepository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                PullRequestNumber = table.Column<int>(type: "integer", nullable: false),
                PullRequestUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                SourceBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                TargetBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SourceSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                MergeCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RemoteBaseSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                MergedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                VerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RemoteLandingProofs", x => x.Id);
                table.ForeignKey(
                    name: "FK_RemoteLandingProofs_Tasks_ProjectId_TaskId",
                    columns: x => new { x.ProjectId, x.TaskId },
                    principalTable: "Tasks",
                    principalColumns: new[] { "ProjectId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RemoteLandingProofs_ProjectId_TaskId",
            table: "RemoteLandingProofs",
            columns: new[] { "ProjectId", "TaskId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RemoteLandingProofs_ProjectId_Repository_PullRequest",
            table: "RemoteLandingProofs",
            columns: new[] { "ProjectId", "GitHubRepository", "PullRequestNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RemoteLandingProofs_ProjectId_MergeCommitSha",
            table: "RemoteLandingProofs",
            columns: new[] { "ProjectId", "MergeCommitSha" });

        migrationBuilder.Sql("""
            ALTER TABLE "RemoteLandingProofs" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "RemoteLandingProofs" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_project_isolation_remote_landing_proofs ON "RemoteLandingProofs"
                FOR ALL
                USING (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "ProjectId"::text = NULLIF(current_setting('app.tenant_project_id', true), '')
                )
                WITH CHECK (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "ProjectId"::text = NULLIF(current_setting('app.tenant_project_id', true), '')
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS tenant_project_isolation_remote_landing_proofs ON "RemoteLandingProofs";
            ALTER TABLE "RemoteLandingProofs" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "RemoteLandingProofs" DISABLE ROW LEVEL SECURITY;
            """);
        migrationBuilder.DropTable(name: "RemoteLandingProofs");
    }
}
