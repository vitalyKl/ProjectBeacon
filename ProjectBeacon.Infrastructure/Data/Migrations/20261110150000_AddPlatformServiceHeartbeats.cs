using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110150000_AddPlatformServiceHeartbeats")]
public partial class AddPlatformServiceHeartbeats : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "PlatformServiceHeartbeats" (
                "ServiceName" character varying(16) NOT NULL,
                "InstanceId" character varying(64) NOT NULL,
                "Version" character varying(80) NOT NULL,
                "EnvironmentName" character varying(32) NOT NULL,
                "StartedAtUtc" timestamp with time zone NOT NULL,
                "HeartbeatAtUtc" timestamp with time zone NOT NULL,
                "HealthStatus" character varying(16) NOT NULL,
                CONSTRAINT "PK_PlatformServiceHeartbeats"
                    PRIMARY KEY ("ServiceName", "InstanceId"),
                CONSTRAINT "CK_PlatformServiceHeartbeats_ServiceName"
                    CHECK ("ServiceName" IN ('Web', 'Api', 'Worker')),
                CONSTRAINT "CK_PlatformServiceHeartbeats_HealthStatus"
                    CHECK ("HealthStatus" IN ('Healthy', 'Stopping')),
                CONSTRAINT "CK_PlatformServiceHeartbeats_TimeWindow"
                    CHECK ("HeartbeatAtUtc" >= "StartedAtUtc")
            );

            CREATE INDEX "IX_PlatformServiceHeartbeats_HeartbeatAtUtc"
                ON "PlatformServiceHeartbeats" ("HeartbeatAtUtc" DESC);

            ALTER TABLE "PlatformServiceHeartbeats" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "PlatformServiceHeartbeats" FORCE ROW LEVEL SECURITY;
            CREATE POLICY platform_service_heartbeats_operator_only ON "PlatformServiceHeartbeats"
                FOR ALL
                USING (current_setting('app.tenant_unscoped', true) = 'true')
                WITH CHECK (current_setting('app.tenant_unscoped', true) = 'true');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS platform_service_heartbeats_operator_only ON "PlatformServiceHeartbeats";
            ALTER TABLE "PlatformServiceHeartbeats" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "PlatformServiceHeartbeats" DISABLE ROW LEVEL SECURITY;
            DROP TABLE IF EXISTS "PlatformServiceHeartbeats";
            """);
    }
}
