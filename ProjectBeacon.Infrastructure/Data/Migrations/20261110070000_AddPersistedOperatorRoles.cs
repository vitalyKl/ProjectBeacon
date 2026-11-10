using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110070000_AddPersistedOperatorRoles")]
public partial class AddPersistedOperatorRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OperatorRole",
            table: "Users",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "Users"
            SET "OperatorRole" = 'SystemAdministrator'
            WHERE "IsAdmin" = TRUE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "OperatorRole", table: "Users");
    }
}
