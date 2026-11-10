using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110170000_AddBillingPlanCatalog")]
public partial class AddBillingPlanCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "BillingPlans" (
                "Id" uuid NOT NULL,
                "Key" character varying(64) NOT NULL,
                "Name" character varying(120) NOT NULL,
                "Cadence" smallint NOT NULL,
                "PriceMinor" bigint NULL,
                "Currency" character varying(3) NULL,
                "LimitsJson" jsonb NOT NULL,
                "FeaturesJson" jsonb NOT NULL,
                "IsActive" boolean NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_BillingPlans" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_BillingPlans_Key"
                    CHECK ("Key" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
                CONSTRAINT "CK_BillingPlans_Cadence"
                    CHECK ("Cadence" IN (0, 1, 2)),
                CONSTRAINT "CK_BillingPlans_PriceMinor"
                    CHECK ("PriceMinor" IS NULL OR "PriceMinor" >= 0),
                CONSTRAINT "CK_BillingPlans_PriceCurrency"
                    CHECK (("PriceMinor" IS NULL AND "Currency" IS NULL)
                        OR ("PriceMinor" IS NOT NULL AND "Currency" IS NOT NULL
                            AND "Currency" ~ '^[A-Z]{3}
                CONSTRAINT "CK_BillingPlans_LimitsJson"
                    CHECK (jsonb_typeof("LimitsJson") = 'object'),
                CONSTRAINT "CK_BillingPlans_FeaturesJson"
                    CHECK (jsonb_typeof("FeaturesJson") = 'array'
                        AND "FeaturesJson" @> '["core"]'::jsonb)
            );

            CREATE UNIQUE INDEX "IX_BillingPlans_Key" ON "BillingPlans" ("Key");
            CREATE INDEX "IX_BillingPlans_IsActive" ON "BillingPlans" ("IsActive");

            ALTER TABLE "BillingPlans" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "BillingPlans" FORCE ROW LEVEL SECURITY;
            CREATE POLICY billing_plans_operator_only ON "BillingPlans"
                FOR ALL
                USING (current_setting('app.tenant_unscoped', true) = 'true')
                WITH CHECK (current_setting('app.tenant_unscoped', true) = 'true');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS billing_plans_operator_only ON "BillingPlans";
            ALTER TABLE "BillingPlans" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "BillingPlans" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "BillingPlans";
            """);
    }
}
)),
                CONSTRAINT "CK_BillingPlans_LimitsJson"
                    CHECK (jsonb_typeof("LimitsJson") = 'object'),
                CONSTRAINT "CK_BillingPlans_FeaturesJson"
                    CHECK (jsonb_typeof("FeaturesJson") = 'array'
                        AND "FeaturesJson" @> '["core"]'::jsonb)
            );

            CREATE UNIQUE INDEX "IX_BillingPlans_Key" ON "BillingPlans" ("Key");
            CREATE INDEX "IX_BillingPlans_IsActive" ON "BillingPlans" ("IsActive");

            ALTER TABLE "BillingPlans" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "BillingPlans" FORCE ROW LEVEL SECURITY;
            CREATE POLICY billing_plans_operator_only ON "BillingPlans"
                FOR ALL
                USING (current_setting('app.tenant_unscoped', true) = 'true')
                WITH CHECK (current_setting('app.tenant_unscoped', true) = 'true');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS billing_plans_operator_only ON "BillingPlans";
            ALTER TABLE "BillingPlans" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "BillingPlans" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "BillingPlans";
            """);
    }
}
