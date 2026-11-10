using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110180000_AddBillingSubscriptionsAndLedger")]
public partial class AddBillingSubscriptionsAndLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "OrganizationSubscriptions" (
                "Id" uuid NOT NULL,
                "OrgId" uuid NOT NULL,
                "PlanKey" character varying(64) NOT NULL,
                "Status" smallint NOT NULL,
                "Source" smallint NOT NULL,
                "TrialEndsAtUtc" timestamp with time zone NULL,
                "CurrentPeriodStartsAtUtc" timestamp with time zone NOT NULL,
                "CurrentPeriodEndsAtUtc" timestamp with time zone NULL,
                "CancelAtPeriodEnd" boolean NOT NULL,
                "GraceUntilUtc" timestamp with time zone NULL,
                "ProviderKey" character varying(64) NULL,
                "ExternalCustomerReference" character varying(255) NULL,
                "ExternalSubscriptionReference" character varying(255) NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_OrganizationSubscriptions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_OrganizationSubscriptions_Orgs_OrgId"
                    FOREIGN KEY ("OrgId") REFERENCES "Orgs" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_OrganizationSubscriptions_BillingPlans_PlanKey"
                    FOREIGN KEY ("PlanKey") REFERENCES "BillingPlans" ("Key") ON DELETE RESTRICT,
                CONSTRAINT "CK_OrganizationSubscriptions_Status"
                    CHECK ("Status" BETWEEN 0 AND 5),
                CONSTRAINT "CK_OrganizationSubscriptions_Source"
                    CHECK ("Source" BETWEEN 0 AND 2),
                CONSTRAINT "CK_OrganizationSubscriptions_Period"
                    CHECK ("CurrentPeriodEndsAtUtc" IS NULL
                        OR "CurrentPeriodEndsAtUtc" > "CurrentPeriodStartsAtUtc"),
                CONSTRAINT "CK_OrganizationSubscriptions_ProviderReferences"
                    CHECK (
                        ("ProviderKey" IS NULL AND "ExternalSubscriptionReference" IS NULL)
                        OR ("ProviderKey" IS NOT NULL AND "ExternalSubscriptionReference" IS NOT NULL)
                    )
            );

            CREATE INDEX "IX_OrganizationSubscriptions_OrgId_CreatedAtUtc"
                ON "OrganizationSubscriptions" ("OrgId", "CreatedAtUtc" DESC);
            CREATE UNIQUE INDEX "IX_OrganizationSubscriptions_OneNonTerminalPerOrg"
                ON "OrganizationSubscriptions" ("OrgId")
                WHERE "Status" IN (0, 1, 2, 3);
            CREATE UNIQUE INDEX "IX_OrganizationSubscriptions_ProviderReference"
                ON "OrganizationSubscriptions" ("ProviderKey", "ExternalSubscriptionReference")
                WHERE "ProviderKey" IS NOT NULL;

            ALTER TABLE "OrganizationSubscriptions" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "OrganizationSubscriptions" FORCE ROW LEVEL SECURITY;
            CREATE POLICY organization_subscriptions_tenant_or_operator
                ON "OrganizationSubscriptions"
                FOR ALL
                USING (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                )
                WITH CHECK (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                );

            CREATE TABLE "BillingLedgerEntries" (
                "Id" uuid NOT NULL,
                "OccurredAtUtc" timestamp with time zone NOT NULL,
                "OrgId" uuid NOT NULL,
                "EventType" smallint NOT NULL,
                "Source" smallint NOT NULL,
                "AmountMinor" bigint NULL,
                "Currency" character varying(3) NULL,
                "ProviderKey" character varying(64) NULL,
                "ProviderEventId" character varying(128) NULL,
                "Reference" character varying(255) NULL,
                "ActorUserId" uuid NULL,
                "Note" character varying(200) NULL,
                "IdempotencyKey" character varying(128) NULL,
                "SubscriptionId" uuid NULL,
                CONSTRAINT "PK_BillingLedgerEntries" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_BillingLedgerEntries_Orgs_OrgId"
                    FOREIGN KEY ("OrgId") REFERENCES "Orgs" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_BillingLedgerEntries_Users_ActorUserId"
                    FOREIGN KEY ("ActorUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_BillingLedgerEntries_OrganizationSubscriptions_SubscriptionId"
                    FOREIGN KEY ("SubscriptionId") REFERENCES "OrganizationSubscriptions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "CK_BillingLedgerEntries_EventType"
                    CHECK ("EventType" BETWEEN 0 AND 10),
                CONSTRAINT "CK_BillingLedgerEntries_Source"
                    CHECK ("Source" BETWEEN 0 AND 2),
                CONSTRAINT "CK_BillingLedgerEntries_AmountCurrency"
                    CHECK (
                        ("AmountMinor" IS NULL AND "Currency" IS NULL)
                        OR (
                            "AmountMinor" IS NOT NULL
                            AND "AmountMinor" >= 0
                            AND "Currency" IS NOT NULL
                            AND "Currency" ~ '^[A-Z]{3}$'
                        )
                    ),
                CONSTRAINT "CK_BillingLedgerEntries_ProviderEvent"
                    CHECK (("ProviderKey" IS NULL) = ("ProviderEventId" IS NULL))
            );

            CREATE INDEX "IX_BillingLedgerEntries_OrgId_OccurredAtUtc"
                ON "BillingLedgerEntries" ("OrgId", "OccurredAtUtc" DESC);
            CREATE UNIQUE INDEX "IX_BillingLedgerEntries_ProviderEvent"
                ON "BillingLedgerEntries" ("ProviderKey", "ProviderEventId")
                WHERE "ProviderKey" IS NOT NULL AND "ProviderEventId" IS NOT NULL;
            CREATE UNIQUE INDEX "IX_BillingLedgerEntries_IdempotencyKey"
                ON "BillingLedgerEntries" ("IdempotencyKey")
                WHERE "IdempotencyKey" IS NOT NULL;

            ALTER TABLE "BillingLedgerEntries" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "BillingLedgerEntries" FORCE ROW LEVEL SECURITY;
            CREATE POLICY billing_ledger_entries_tenant_or_operator
                ON "BillingLedgerEntries"
                FOR ALL
                USING (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                )
                WITH CHECK (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                );

            CREATE OR REPLACE FUNCTION billing_ledger_immutable_guard()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RAISE EXCEPTION 'BillingLedgerEntries is append-only'
                    USING ERRCODE = '55000';
            END;
            $function$;

            CREATE TRIGGER "TR_BillingLedgerEntries_AppendOnly"
                BEFORE UPDATE OR DELETE ON "BillingLedgerEntries"
                FOR EACH ROW EXECUTE FUNCTION billing_ledger_immutable_guard();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS "TR_BillingLedgerEntries_AppendOnly" ON "BillingLedgerEntries";
            DROP FUNCTION IF EXISTS billing_ledger_immutable_guard();
            DROP POLICY IF EXISTS billing_ledger_entries_tenant_or_operator ON "BillingLedgerEntries";
            ALTER TABLE "BillingLedgerEntries" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "BillingLedgerEntries" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "BillingLedgerEntries";

            DROP POLICY IF EXISTS organization_subscriptions_tenant_or_operator ON "OrganizationSubscriptions";
            ALTER TABLE "OrganizationSubscriptions" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "OrganizationSubscriptions" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "OrganizationSubscriptions";
            """);
    }
}
