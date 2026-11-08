using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_AuditEvents", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrgId_At",
                table: "AuditEvents",
                columns: new[] { "OrgId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ProjectId_At",
                table: "AuditEvents",
                columns: new[] { "ProjectId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_At",
                table: "AuditEvents",
                columns: new[] { "Action", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TargetType_TargetId",
                table: "AuditEvents",
                columns: new[] { "TargetType", "TargetId" });

            // Both tenant filtering and database RLS fail closed. Global rows require an explicit
            // unscoped operator path; org/project rows must match at least one active tenant id.
            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "AuditEvents" FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_audit_event_isolation ON "AuditEvents"
                    FOR ALL
                    USING (
                        current_setting('app.tenant_unscoped', true) = 'true'
                        OR ("OrgId" IS NOT NULL AND "OrgId"::text = current_setting('app.tenant_org_id', true))
                        OR ("ProjectId" IS NOT NULL AND "ProjectId"::text = current_setting('app.tenant_project_id', true))
                    )
                    WITH CHECK (
                        current_setting('app.tenant_unscoped', true) = 'true'
                        OR ("OrgId" IS NOT NULL AND "OrgId"::text = current_setting('app.tenant_org_id', true))
                        OR ("ProjectId" IS NOT NULL AND "ProjectId"::text = current_setting('app.tenant_project_id', true))
                    );
                """);

            // The application database role can insert audit events, but cannot rewrite history.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reject_audit_event_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditEvents are append-only';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_audit_events_append_only
                    BEFORE UPDATE OR DELETE ON "AuditEvents"
                    FOR EACH ROW EXECUTE FUNCTION reject_audit_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_events_append_only ON "AuditEvents";
                DROP FUNCTION IF EXISTS reject_audit_event_mutation();
                DROP POLICY IF EXISTS tenant_audit_event_isolation ON "AuditEvents";
                ALTER TABLE "AuditEvents" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "AuditEvents" DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(name: "AuditEvents");
        }
    }
}
