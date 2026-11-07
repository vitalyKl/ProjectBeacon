using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    [DbContext(typeof(BeaconDbContext))]
    [Migration("20261107180000_RemoveOpenCodeRuntime")]
    public partial class RemoveOpenCodeRuntime : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OpenCodeModel",
                table: "LocalModelBackends",
                newName: "ProviderModelId");

            // Pending or running commands created by a previous client must not be replayed
            // against the Native-only daemon. Mark them terminal before renaming their enum keys.
            migrationBuilder.Sql("""
                UPDATE "WorkstationCommands"
                SET "Status" = 'Failed',
                    "Error" = 'Legacy external-agent command retired during Native runtime migration.',
                    "CompletedAt" = COALESCE("CompletedAt", CURRENT_TIMESTAMP),
                    "StartedAt" = COALESCE("StartedAt", CURRENT_TIMESTAMP),
                    "LeaseExpiresAt" = NULL
                WHERE "Kind" IN ('ApplyOpencode', 'ConfigureOpenCode')
                  AND "Status" IN ('Pending', 'Running');

                UPDATE "WorkstationCommands"
                SET "Status" = 'Failed',
                    "Error" = 'Legacy OpenCode eval command retired during Native runtime migration.',
                    "CompletedAt" = COALESCE("CompletedAt", CURRENT_TIMESTAMP),
                    "StartedAt" = COALESCE("StartedAt", CURRENT_TIMESTAMP),
                    "LeaseExpiresAt" = NULL
                WHERE "Kind" = 'RunEvalTurn'
                  AND "Status" IN ('Pending', 'Running')
                  AND "PayloadJson"::jsonb ->> 'runtime' = 'opencode';

                UPDATE "WorkstationCommands"
                SET "Kind" = CASE "Kind"
                    WHEN 'ApplyOpencode' THEN 'RetiredAgentConfigApply'
                    WHEN 'ConfigureOpenCode' THEN 'RetiredProviderSync'
                    ELSE "Kind"
                END
                WHERE "Kind" IN ('ApplyOpencode', 'ConfigureOpenCode');
                """);

            // Keep old eval rows and their checks/tokens/transcript references. Only their
            // retired runtime label is normalized; no new comparison runs are created.
            migrationBuilder.Sql("""
                UPDATE "EvalRuns"
                SET "Runtime" = 'native'
                WHERE "Runtime" <> 'native';

                UPDATE "EvalRuns"
                SET "Condition" = 'HistoricalComparisonA'
                WHERE "Condition" = 'OpenCode';

                UPDATE "EvalRuns"
                SET "Condition" = 'HistoricalComparisonB'
                WHERE "Condition" = 'Native';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Runtime",
                table: "EvalRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "native",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "opencode");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Runtime",
                table: "EvalRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "opencode",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "native");

            migrationBuilder.Sql("""
                UPDATE "EvalRuns"
                SET "Condition" = 'OpenCode'
                WHERE "Condition" = 'HistoricalComparisonA';

                UPDATE "EvalRuns"
                SET "Condition" = 'Native'
                WHERE "Condition" = 'HistoricalComparisonB';

                UPDATE "EvalRuns"
                SET "Runtime" = 'opencode';

                UPDATE "WorkstationCommands"
                SET "Kind" = CASE "Kind"
                    WHEN 'RetiredAgentConfigApply' THEN 'ApplyOpencode'
                    WHEN 'RetiredProviderSync' THEN 'ConfigureOpenCode'
                    ELSE "Kind"
                END
                WHERE "Kind" IN ('RetiredAgentConfigApply', 'RetiredProviderSync');
                """);

            migrationBuilder.RenameColumn(
                name: "ProviderModelId",
                table: "LocalModelBackends",
                newName: "OpenCodeModel");
        }
    }
}