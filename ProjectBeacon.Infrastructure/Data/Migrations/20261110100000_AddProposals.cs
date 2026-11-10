using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110100000_AddProposals")]
public sealed class AddProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Proposals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                SubtaskId = table.Column<Guid>(type: "uuid", nullable: true),
                SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                TargetKind = table.Column<string>(type: "text", nullable: false),
                TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                Operation = table.Column<string>(type: "text", nullable: false),
                BaseRevision = table.Column<int>(type: "integer", nullable: false),
                BaseHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                RationaleMarkdown = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                AuthorKind = table.Column<string>(type: "text", nullable: false),
                AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                AuthorTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                AuthorProfileKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                AuthorRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                AcceptedPayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                AppliedRevision = table.Column<int>(type: "integer", nullable: true),
                ConflictRevision = table.Column<int>(type: "integer", nullable: true),
                ConflictReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                SupersededById = table.Column<Guid>(type: "uuid", nullable: true),
                StateRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Proposals", proposal => proposal.Id);
                table.ForeignKey(
                    name: "FK_Proposals_Projects_ProjectId",
                    column: proposal => proposal.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_ProjectId_Status_CreatedAt",
            table: "Proposals",
            columns: new[] { "ProjectId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_ProjectId_TaskId_CreatedAt",
            table: "Proposals",
            columns: new[] { "ProjectId", "TaskId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_ProjectId_IdempotencyKey",
            table: "Proposals",
            columns: new[] { "ProjectId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_ProjectId_SubtaskId_TargetKind_TargetId",
            table: "Proposals",
            columns: new[] { "ProjectId", "SubtaskId", "TargetKind", "TargetId" },
            unique: true,
            filter: "\"Status\" = 'Pending' AND \"SubtaskId\" IS NOT NULL");

        migrationBuilder.Sql("""
            ALTER TABLE "Proposals" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "Proposals" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_project_isolation_proposals ON "Proposals"
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
            DROP POLICY IF EXISTS tenant_project_isolation_proposals ON "Proposals";
            ALTER TABLE "Proposals" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "Proposals" DISABLE ROW LEVEL SECURITY;
            """);
        migrationBuilder.DropTable(name: "Proposals");
    }
}
