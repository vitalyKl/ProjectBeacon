using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    public partial class EnableRls : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var projectScopedTables = new[]
            {
                "Tasks", "Labels", "LabelPaths", "Reports", "Milestones", "Constraints",
                "Decisions", "ApiTokens", "PipelineSessions", "ProjectRuntimes",
                "ProjectMembers", "ProjectInvites", "ReviewVerdicts", "Subtasks",
                "RoleBindings", "TaskPhases", "TaskSteps", "ChatSessions", "ChatParts",
                "EvalRuns", "ReviewRuns", "ContextSections", "ContextRevisions"
            };

            var orgScopedTables = new[] { "Projects", "OrgMembers", "OrgInvites" };

            foreach (var table in projectScopedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"CREATE POLICY tenant_project_isolation ON ""{table}""
                    USING (current_setting('app.tenant_unscoped', true) = 'true'
                           OR ""ProjectId""::text = current_setting('app.tenant_project_id', true));");
            }

            foreach (var table in orgScopedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"CREATE POLICY tenant_org_isolation ON ""{table}""
                    USING (current_setting('app.tenant_unscoped', true) = 'true'
                           OR ""OrgId""::text = current_setting('app.tenant_org_id', true));");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var projectScopedTables = new[]
            {
                "Tasks", "Labels", "LabelPaths", "Reports", "Milestones", "Constraints",
                "Decisions", "ApiTokens", "PipelineSessions", "ProjectRuntimes",
                "ProjectMembers", "ProjectInvites", "ReviewVerdicts", "Subtasks",
                "RoleBindings", "TaskPhases", "TaskSteps", "ChatSessions", "ChatParts",
                "EvalRuns", "ReviewRuns", "ContextSections", "ContextRevisions"
            };

            var orgScopedTables = new[] { "Projects", "OrgMembers", "OrgInvites" };

            foreach (var table in projectScopedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_project_isolation ON \"{table}\";");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" DISABLE ROW LEVEL SECURITY;");
            }

            foreach (var table in orgScopedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_org_isolation ON \"{table}\";");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
