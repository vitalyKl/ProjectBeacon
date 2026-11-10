using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110150000_UseTextForProposalConflictReason")]
public sealed partial class UseTextForProposalConflictReason : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "ConflictReason",
            table: "Proposals",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(200)",
            oldMaxLength: 200,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "ConflictReason",
            table: "Proposals",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);
    }
}
