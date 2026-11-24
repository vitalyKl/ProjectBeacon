using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124133000_AddWorkstationCommandPendingSince")]
public sealed class AddWorkstationCommandPendingSince : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "PendingSince",
            table: "WorkstationCommands",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql(
            """UPDATE "WorkstationCommands"
               SET "PendingSince" = CASE
                   WHEN "Attempts" > 0 OR "ClaimInstanceId" IS NOT NULL THEN CURRENT_TIMESTAMP
                   ELSE "CreatedAt"
               END
               WHERE "Status" = 'Pending';""");

        migrationBuilder.CreateIndex(
            name: "IX_WorkstationCommands_Kind_Status_PendingSince",
            table: "WorkstationCommands",
            columns: new[] { "Kind", "Status", "PendingSince" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkstationCommands_Kind_Status_PendingSince",
            table: "WorkstationCommands");

        migrationBuilder.DropColumn(
            name: "PendingSince",
            table: "WorkstationCommands");
    }
}
