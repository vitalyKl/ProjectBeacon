using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110120000_AddTotpRecoveryCodes")]
public partial class AddTotpRecoveryCodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TotpRecoveryCodeHashesJson",
            table: "Users",
            type: "character varying(1024)",
            maxLength: 1024,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TotpRecoveryCodeHashesJson",
            table: "Users");
    }
}
