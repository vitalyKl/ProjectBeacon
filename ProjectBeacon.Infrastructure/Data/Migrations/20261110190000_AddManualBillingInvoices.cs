using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110190000_AddManualBillingInvoices")]
public partial class AddManualBillingInvoices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SEQUENCE "BillingInvoiceNumberSequence" START WITH 1 INCREMENT BY 1;

            CREATE TABLE "BillingInvoices" (
                "Id" uuid NOT NULL,
                "OrgId" uuid NOT NULL,
                "Number" character varying(32) NOT NULL,
                "Description" character varying(200) NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" character varying(3) NOT NULL,
                "IssuedAtUtc" timestamp with time zone NOT NULL,
                "DueAtUtc" timestamp with time zone NOT NULL,
                "Status" smallint NOT NULL,
                "PaidAtUtc" timestamp with time zone NULL,
                "PaymentReference" character varying(128) NULL,
                "ProviderKey" character varying(64) NULL,
                "ExternalInvoiceReference" character varying(128) NULL,
                "CreatedByUserId" uuid NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_BillingInvoices" PRIMARY KEY ("Id"),
                CONSTRAINT "UQ_BillingInvoices_Id_OrgId" UNIQUE ("Id", "OrgId"),
                CONSTRAINT "FK_BillingInvoices_Orgs_OrgId"
                    FOREIGN KEY ("OrgId") REFERENCES "Orgs" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "CK_BillingInvoices_AmountCurrency"
                    CHECK ("AmountMinor" > 0 AND "Currency" ~ '^[A-Z]{3}$'),
                CONSTRAINT "CK_BillingInvoices_Dates"
                    CHECK ("DueAtUtc" >= "IssuedAtUtc"),
                CONSTRAINT "CK_BillingInvoices_Status"
                    CHECK ("Status" BETWEEN 0 AND 4),
                CONSTRAINT "CK_BillingInvoices_PaidState"
                    CHECK (
                        ("Status" = 2 AND "PaidAtUtc" IS NOT NULL AND "PaymentReference" IS NOT NULL)
                        OR ("Status" <> 2 AND "PaidAtUtc" IS NULL AND "PaymentReference" IS NULL)
                    ),
                CONSTRAINT "CK_BillingInvoices_ProviderReference"
                    CHECK (
                        ("ProviderKey" IS NULL AND "ExternalInvoiceReference" IS NULL)
                        OR ("ProviderKey" IS NOT NULL AND "ExternalInvoiceReference" IS NOT NULL)
                    )
            );

            CREATE UNIQUE INDEX "IX_BillingInvoices_Number"
                ON "BillingInvoices" ("Number");
            CREATE UNIQUE INDEX "IX_BillingInvoices_OrgId_PaymentReference"
                ON "BillingInvoices" ("OrgId", "PaymentReference")
                WHERE "PaymentReference" IS NOT NULL;
            CREATE INDEX "IX_BillingInvoices_OrgId_IssuedAtUtc"
                ON "BillingInvoices" ("OrgId", "IssuedAtUtc" DESC);
            CREATE INDEX "IX_BillingInvoices_Status_DueAtUtc"
                ON "BillingInvoices" ("Status", "DueAtUtc");

            ALTER TABLE "BillingInvoices" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "BillingInvoices" FORCE ROW LEVEL SECURITY;
            CREATE POLICY billing_invoices_tenant_or_operator
                ON "BillingInvoices"
                FOR ALL
                USING (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                )
                WITH CHECK (
                    current_setting('app.tenant_unscoped', true) = 'true'
                    OR "OrgId"::text = NULLIF(current_setting('app.tenant_org_id', true), '')
                );

            ALTER TABLE "BillingLedgerEntries"
                ADD COLUMN "InvoiceId" uuid NULL;
            ALTER TABLE "BillingLedgerEntries"
                ADD CONSTRAINT "FK_BillingLedgerEntries_BillingInvoices_InvoiceId_OrgId"
                FOREIGN KEY ("InvoiceId", "OrgId")
                REFERENCES "BillingInvoices" ("Id", "OrgId")
                ON DELETE RESTRICT;
            CREATE INDEX "IX_BillingLedgerEntries_InvoiceId_OrgId"
                ON "BillingLedgerEntries" ("InvoiceId", "OrgId");

            ALTER TABLE "BillingLedgerEntries"
                DROP CONSTRAINT "CK_BillingLedgerEntries_EventType";
            ALTER TABLE "BillingLedgerEntries"
                ADD CONSTRAINT "CK_BillingLedgerEntries_EventType"
                CHECK ("EventType" BETWEEN 0 AND 12);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "BillingLedgerEntries"
                DROP CONSTRAINT IF EXISTS "FK_BillingLedgerEntries_BillingInvoices_InvoiceId_OrgId";
            DROP INDEX IF EXISTS "IX_BillingLedgerEntries_InvoiceId_OrgId";
            ALTER TABLE "BillingLedgerEntries" DROP COLUMN IF EXISTS "InvoiceId";
            ALTER TABLE "BillingLedgerEntries"
                DROP CONSTRAINT IF EXISTS "CK_BillingLedgerEntries_EventType";
            ALTER TABLE "BillingLedgerEntries"
                ADD CONSTRAINT "CK_BillingLedgerEntries_EventType"
                CHECK ("EventType" BETWEEN 0 AND 10);

            DROP POLICY IF EXISTS billing_invoices_tenant_or_operator ON "BillingInvoices";
            ALTER TABLE "BillingInvoices" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "BillingInvoices" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "BillingInvoices";
            DROP SEQUENCE IF EXISTS "BillingInvoiceNumberSequence";
            """);
    }
}
