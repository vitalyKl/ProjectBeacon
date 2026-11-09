using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    public partial class AddRevisionedContent : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "ContextSections",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Decisions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Constraints",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Milestones",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ContentRevision",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "ContentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedById = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChangedByType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentVersions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentVersions_ProjectId_At",
                table: "ContentVersions",
                columns: new[] { "ProjectId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentVersions_ProjectId_TargetKind_TargetId_Revision",
                table: "ContentVersions",
                columns: new[] { "ProjectId", "TargetKind", "TargetId", "Revision" },
                unique: true);

            // Preserve the pre-revision state as revision 1 so existing records have a complete history.
            migrationBuilder.Sql("""
                INSERT INTO "ContentVersions"
                    ("Id", "At", "ChangedById", "ChangedByType", "ContentJson", "ProjectId", "ProposalId", "Revision", "TargetId", "TargetKind")
                SELECT gen_random_uuid(), CURRENT_TIMESTAMP, 'migration:20261109180000', 'System',
                    jsonb_build_object(
                        'sectionId', "SectionId",
                        'key', "Key",
                        'title', "Title",
                        'bodyMarkdown', "BodyMarkdown",
                        'ordinal', "Ordinal",
                        'scopeType', "ScopeType",
                        'repoId', "RepoId",
                        'taskId', "TaskId",
                        'path', "Path",
                        'deleted', false),
                    "ProjectId", NULL, 1, "Id", 'ContextSection'
                FROM "ContextSections";

                INSERT INTO "ContentVersions"
                    ("Id", "At", "ChangedById", "ChangedByType", "ContentJson", "ProjectId", "ProposalId", "Revision", "TargetId", "TargetKind")
                SELECT gen_random_uuid(), CURRENT_TIMESTAMP, 'migration:20261109180000', 'System',
                    jsonb_build_object(
                        'title', "Title",
                        'context', "Context",
                        'decisionBody', "DecisionBody",
                        'consequences', "Consequences",
                        'deleted', false),
                    "ProjectId", NULL, 1, "Id", 'Decision'
                FROM "Decisions";

                INSERT INTO "ContentVersions"
                    ("Id", "At", "ChangedById", "ChangedByType", "ContentJson", "ProjectId", "ProposalId", "Revision", "TargetId", "TargetKind")
                SELECT gen_random_uuid(), CURRENT_TIMESTAMP, 'migration:20261109180000', 'System',
                    jsonb_build_object(
                        'body', "Body",
                        'scopePath', "ScopePath",
                        'kind', CASE "Kind"
                            WHEN 'Must' THEN 0
                            WHEN 'MustNot' THEN 1
                            WHEN 'Security' THEN 2
                            WHEN 'Compliance' THEN 3
                            ELSE NULL
                        END,
                        'deleted', false),
                    "ProjectId", NULL, 1, "Id", 'Constraint'
                FROM "Constraints";

                INSERT INTO "ContentVersions"
                    ("Id", "At", "ChangedById", "ChangedByType", "ContentJson", "ProjectId", "ProposalId", "Revision", "TargetId", "TargetKind")
                SELECT gen_random_uuid(), CURRENT_TIMESTAMP, 'migration:20261109180000', 'System',
                    jsonb_build_object(
                        'name', "Name",
                        'description', "Description",
                        'order', "Order",
                        'deleted', false),
                    "ProjectId", NULL, 1, "Id", 'Milestone'
                FROM "Milestones";

                INSERT INTO "ContentVersions"
                    ("Id", "At", "ChangedById", "ChangedByType", "ContentJson", "ProjectId", "ProposalId", "Revision", "TargetId", "TargetKind")
                SELECT gen_random_uuid(), CURRENT_TIMESTAMP, 'migration:20261109180000', 'System',
                    jsonb_build_object(
                        'description', "Description",
                        'deleted', false),
                    "Id", NULL, 1, "Id", 'ProjectDescription'
                FROM "Projects";

                INSERT INTO "ContentVersions"
                    ("Id", "At", "ChangedById", "ChangedByType", "ContentJson", "ProjectId", "ProposalId", "Revision", "TargetId", "TargetKind")
                SELECT gen_random_uuid(), CURRENT_TIMESTAMP, 'migration:20261109180000', 'System',
                    jsonb_build_object(
                        'description', "Description",
                        'reviewNotes', "ReviewNotes",
                        'deleted', false),
                    "ProjectId", NULL, 1, "Id", 'TaskDescription'
                FROM "Tasks";
                """);

            // ContentVersions belongs to exactly one tenant project and fails closed without a project scope.
            migrationBuilder.Sql("""
                ALTER TABLE "ContentVersions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "ContentVersions" FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_project_isolation ON "ContentVersions"
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

            // History cannot be rewritten or individually removed. Project deletion may cascade and retire its tenant data.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reject_content_version_mutation()
                RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' OR pg_trigger_depth() <= 1 THEN
                        RAISE EXCEPTION 'ContentVersions are append-only';
                    END IF;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_content_versions_append_only
                    BEFORE UPDATE OR DELETE ON "ContentVersions"
                    FOR EACH ROW EXECUTE FUNCTION reject_content_version_mutation();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_content_versions_append_only ON "ContentVersions";
                DROP FUNCTION IF EXISTS reject_content_version_mutation();
                DROP POLICY IF EXISTS tenant_project_isolation ON "ContentVersions";
                ALTER TABLE "ContentVersions" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "ContentVersions" DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(name: "ContentVersions");

            migrationBuilder.DropColumn(name: "Revision", table: "ContextSections");
            migrationBuilder.DropColumn(name: "Revision", table: "Decisions");
            migrationBuilder.DropColumn(name: "Revision", table: "Constraints");
            migrationBuilder.DropColumn(name: "Revision", table: "Milestones");
            migrationBuilder.DropColumn(name: "Revision", table: "Projects");
            migrationBuilder.DropColumn(name: "ContentRevision", table: "Tasks");
        }
    }
}
