using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261110140000_AddProposalApplyCommand")]
public sealed class AddProposalApplyCommand : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ApplyCommandId",
            table: "Proposals",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_ApplyCommandId",
            table: "Proposals",
            column: "ApplyCommandId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Proposals_ApplyCommandId",
            table: "Proposals");
        migrationBuilder.DropColumn(
            name: "ApplyCommandId",
            table: "Proposals");
    }
}
