using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110130000_AddRepoPatchProfilePolicy")]
public sealed class AddRepoPatchProfilePolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ApplyPolicy",
            table: "AgentProfiles",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Review");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ApplyPolicy",
            table: "AgentProfiles");
    }
}
