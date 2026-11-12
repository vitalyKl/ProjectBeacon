using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261112060000_AddImportedContextFileIdentity")]
public sealed class AddImportedContextFileIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "ContextSections"
                    WHERE "Source" <> 0
                      AND "SourcePath" IS NOT NULL
                      AND "SourcePath" <> ''
                      AND "ScopeType" = 0
                      AND "RepoId" IS NULL
                      AND "TaskId" IS NULL
                      AND "Path" = ''
                    GROUP BY "ProjectId", "SourcePath"
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Duplicate imported ContextSection source paths exist; resolve them before applying the imported-file identity index.';
                END IF;
            END $$;
            """);

        migrationBuilder.DropIndex(
            name: "IX_ContextSections_ProjectId",
            table: "ContextSections");

        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX "IX_ContextSections_ImportedFileIdentity"
                ON "ContextSections" ("ProjectId", "SourcePath")
                WHERE "Source" <> 0
                  AND "SourcePath" IS NOT NULL
                  AND "SourcePath" <> ''
                  AND "ScopeType" = 0
                  AND "RepoId" IS NULL
                  AND "TaskId" IS NULL
                  AND "Path" = '';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ContextSections_ImportedFileIdentity",
            table: "ContextSections");

        migrationBuilder.CreateIndex(
            name: "IX_ContextSections_ProjectId",
            table: "ContextSections",
            column: "ProjectId");
    }
}
