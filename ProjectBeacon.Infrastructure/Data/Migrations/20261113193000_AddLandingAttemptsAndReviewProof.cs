using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261113193000_AddLandingAttemptsAndReviewProof")]
public sealed class AddLandingAttemptsAndReviewProof : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CheckCommandHash",
            table: "ReviewRuns",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CheckedSourceSha",
            table: "ReviewRuns",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CheckedTreeSha",
            table: "ReviewRuns",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "LandingAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                ReviewRunId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Rationale = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                SourceBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SourceSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ReviewCommandHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TargetBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                TargetHeadSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CandidateTreeSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CandidateCheckOutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CandidateCheckExitCode = table.Column<int>(type: "integer", nullable: true),
                ConflictFilesJson = table.Column<string>(type: "jsonb", nullable: true),
                FailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                MergeCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                RevertCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PreviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RevertedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LandingAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_LandingAttempts_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LandingAttempts_ProjectId_Status_CreatedAt",
            table: "LandingAttempts",
            columns: new[] { "ProjectId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_LandingAttempts_ProjectId_TaskId_CreatedAt",
            table: "LandingAttempts",
            columns: new[] { "ProjectId", "TaskId", "CreatedAt" });

        migrationBuilder.Sql("""
            ALTER TABLE "LandingAttempts" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "LandingAttempts" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_project_isolation_landing_attempts ON "LandingAttempts"
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
            DROP POLICY IF EXISTS tenant_project_isolation_landing_attempts ON "LandingAttempts";
            ALTER TABLE "LandingAttempts" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "LandingAttempts" DISABLE ROW LEVEL SECURITY;
            """);

        migrationBuilder.DropTable(name: "LandingAttempts");
        migrationBuilder.DropColumn(name: "CheckCommandHash", table: "ReviewRuns");
        migrationBuilder.DropColumn(name: "CheckedSourceSha", table: "ReviewRuns");
        migrationBuilder.DropColumn(name: "CheckedTreeSha", table: "ReviewRuns");
    }
}
