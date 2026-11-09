using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261109143000_AddOrganizationEntitlements")]
public partial class AddOrganizationEntitlements : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "OrganizationEntitlements" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrgId" uuid NOT NULL,
                "Source" smallint NOT NULL,
                "Status" smallint NOT NULL,
                "StartsAtUtc" timestamp with time zone NOT NULL,
                "EndsAtUtc" timestamp with time zone NULL,
                "LimitsJson" jsonb NOT NULL DEFAULT '{}'::jsonb,
                "FeaturesJson" jsonb NOT NULL DEFAULT '[]'::jsonb,
                "SourceReference" character varying(200) NULL,
                "GrantedByUserId" uuid NULL,
                "Reason" character varying(500) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_OrganizationEntitlements_Orgs_OrgId"
                    FOREIGN KEY ("OrgId") REFERENCES "Orgs" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_OrganizationEntitlements_Users_GrantedByUserId"
                    FOREIGN KEY ("GrantedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
                CONSTRAINT "CK_OrganizationEntitlements_Status" CHECK ("Status" BETWEEN 0 AND 4),
                CONSTRAINT "CK_OrganizationEntitlements_Source" CHECK ("Source" BETWEEN 0 AND 3),
                CONSTRAINT "CK_OrganizationEntitlements_Window"
                    CHECK ("EndsAtUtc" IS NULL OR "EndsAtUtc" > "StartsAtUtc")
            );

            CREATE INDEX "IX_OrganizationEntitlements_OrgId_CreatedAtUtc"
                ON "OrganizationEntitlements" ("OrgId", "CreatedAtUtc" DESC);
            CREATE UNIQUE INDEX "IX_OrganizationEntitlements_OneCurrentGrantPerOrg"
                ON "OrganizationEntitlements" ("OrgId") WHERE "Status" IN (0, 1);
            CREATE INDEX "IX_OrganizationEntitlements_Source_Status"
                ON "OrganizationEntitlements" ("Source", "Status");

            ALTER TABLE "OrganizationEntitlements" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "OrganizationEntitlements" FORCE ROW LEVEL SECURITY;
            CREATE POLICY organization_entitlements_tenant_or_operator ON "OrganizationEntitlements"
                FOR ALL
                USING (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                )
                WITH CHECK (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS organization_entitlements_tenant_or_operator ON "OrganizationEntitlements";
            ALTER TABLE "OrganizationEntitlements" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "OrganizationEntitlements" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "OrganizationEntitlements";
            """);
    }
}
