using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Enables and forces Postgres row level security on tenant-scoped child tables that carry
    /// no ProjectId/OrgId column of their own. Their visibility derives from the parent rows
    /// (Task / Decision) through the same app.tenant_* GUCs: the parent policy applies inside
    /// the EXISTS subquery, so a row is visible only while its parent is in scope.
    /// </summary>
    public partial class EnableRlsChildTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TaskComments: visible while the owning Task is in scope.
            migrationBuilder.Sql($@"CREATE POLICY tenant_project_isolation ON ""TaskComments""
                USING (current_setting('app.tenant_unscoped', true) = 'true'
                       OR EXISTS (SELECT 1 FROM ""Tasks"" t WHERE t.""Id"" = ""TaskId""));");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskComments"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskComments"" FORCE ROW LEVEL SECURITY;");

            // TaskDependencies: both linked Tasks must be in scope, so a cross-tenant
            // dependency pair is invisible from every tenant.
            migrationBuilder.Sql($@"CREATE POLICY tenant_project_isolation ON ""TaskDependencies""
                USING (current_setting('app.tenant_unscoped', true) = 'true'
                       OR EXISTS (SELECT 1 FROM ""Tasks"" t WHERE t.""Id"" = ""TaskId"")
                          AND EXISTS (SELECT 1 FROM ""Tasks"" d WHERE d.""Id"" = ""DependentTaskId""));");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskDependencies"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskDependencies"" FORCE ROW LEVEL SECURITY;");

            // DecisionTasks: the Decision and the linked Task must both be in scope.
            migrationBuilder.Sql($@"CREATE POLICY tenant_project_isolation ON ""DecisionTasks""
                USING (current_setting('app.tenant_unscoped', true) = 'true'
                       OR EXISTS (SELECT 1 FROM ""Decisions"" d WHERE d.""Id"" = ""DecisionId"")
                          AND EXISTS (SELECT 1 FROM ""Tasks"" t WHERE t.""Id"" = ""TaskId""));");
            migrationBuilder.Sql($@"ALTER TABLE ""DecisionTasks"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"ALTER TABLE ""DecisionTasks"" FORCE ROW LEVEL SECURITY;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_project_isolation ON ""TaskComments"";");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskComments"" NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskComments"" DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_project_isolation ON ""TaskDependencies"";");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskDependencies"" NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"ALTER TABLE ""TaskDependencies"" DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_project_isolation ON ""DecisionTasks"";");
            migrationBuilder.Sql($@"ALTER TABLE ""DecisionTasks"" NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($@"ALTER TABLE ""DecisionTasks"" DISABLE ROW LEVEL SECURITY;");
        }
    }
}
