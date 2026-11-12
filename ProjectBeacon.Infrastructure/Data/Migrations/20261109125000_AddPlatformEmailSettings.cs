using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

public partial class AddPlatformEmailSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PlatformEmailSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Port = table.Column<int>(type: "integer", nullable: false),
                Username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                PasswordCipher = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                UseStartTls = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_PlatformEmailSettings", x => x.Id));

        migrationBuilder.CreateTable(
            name: "EmailDeliveryLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Purpose = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_EmailDeliveryLogs", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_EmailDeliveryLogs_At",
            table: "EmailDeliveryLogs",
            column: "At");

        migrationBuilder.Sql("""
            ALTER TABLE "PlatformEmailSettings" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "PlatformEmailSettings" FORCE ROW LEVEL SECURITY;
            CREATE POLICY platform_email_settings_operator_only ON "PlatformEmailSettings"
                FOR ALL
                USING (current_setting('app.tenant_unscoped', true) = 'true')
                WITH CHECK (current_setting('app.tenant_unscoped', true) = 'true');

            ALTER TABLE "EmailDeliveryLogs" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "EmailDeliveryLogs" FORCE ROW LEVEL SECURITY;
            CREATE POLICY email_delivery_logs_operator_only ON "EmailDeliveryLogs"
                FOR ALL
                USING (current_setting('app.tenant_unscoped', true) = 'true')
                WITH CHECK (current_setting('app.tenant_unscoped', true) = 'true');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS platform_email_settings_operator_only ON "PlatformEmailSettings";
            ALTER TABLE "PlatformEmailSettings" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "PlatformEmailSettings" DISABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS email_delivery_logs_operator_only ON "EmailDeliveryLogs";
            ALTER TABLE "EmailDeliveryLogs" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "EmailDeliveryLogs" DISABLE ROW LEVEL SECURITY;
            """);

        migrationBuilder.DropTable(name: "EmailDeliveryLogs");
        migrationBuilder.DropTable(name: "PlatformEmailSettings");
    }
}
