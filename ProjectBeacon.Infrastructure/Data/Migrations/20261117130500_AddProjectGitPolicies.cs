using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261117130500_AddProjectGitPolicies")]
public sealed class AddProjectGitPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProjectGitPolicies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                AutonomyLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Manual"),
                DirtyCheckoutPolicy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Escalate"),
                AutoRevertPolicy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Off"),
                MaxChangedFiles = table.Column<int>(type: "integer", nullable: false, defaultValue: 200),
                MaxChangedLines = table.Column<int>(type: "integer", nullable: false, defaultValue: 20000),
                AdditionalProtectedPathsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                Revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectGitPolicies", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProjectGitPolicies_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProjectGitPolicies_ProjectId",
            table: "ProjectGitPolicies",
            column: "ProjectId",
            unique: true);

        migrationBuilder.Sql("""
            INSERT INTO "ProjectGitPolicies"
                ("Id", "ProjectId", "AutonomyLevel", "DirtyCheckoutPolicy", "AutoRevertPolicy",
                 "MaxChangedFiles", "MaxChangedLines", "AdditionalProtectedPathsJson", "Revision",
                 "UpdatedByUserId", "CreatedAt", "UpdatedAt")
            SELECT
                gen_random_uuid(), "Id", 'Manual', 'Escalate', 'Off',
                200, 20000, '[]'::jsonb, 1,
                NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM "Projects";
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "ProjectGitPolicies" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "ProjectGitPolicies" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_project_isolation_git_policies ON "ProjectGitPolicies"
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
            DROP POLICY IF EXISTS tenant_project_isolation_git_policies ON "ProjectGitPolicies";
            ALTER TABLE "ProjectGitPolicies" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "ProjectGitPolicies" DISABLE ROW LEVEL SECURITY;
            """);
        migrationBuilder.DropTable(name: "ProjectGitPolicies");
    }
}
