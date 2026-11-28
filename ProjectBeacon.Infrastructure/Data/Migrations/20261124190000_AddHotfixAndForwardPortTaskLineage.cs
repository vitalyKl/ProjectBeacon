using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124190000_AddHotfixAndForwardPortTaskLineage")]
public sealed partial class AddHotfixAndForwardPortTaskLineage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "HotfixSourceReleaseId",
            table: "Tasks",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "HotfixDeployedSha",
            table: "Tasks",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "HotfixTargetBranch",
            table: "Tasks",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "HotfixRequestId",
            table: "Tasks",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "ForwardPortOfTaskId",
            table: "Tasks",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_Tasks_ProjectId_Id",
            table: "Tasks",
            columns: new[] { "ProjectId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_ProjectId_HotfixSourceReleaseId",
            table: "Tasks",
            columns: new[] { "ProjectId", "HotfixSourceReleaseId" });

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_ProjectId_HotfixRequestId",
            table: "Tasks",
            columns: new[] { "ProjectId", "HotfixRequestId" },
            unique: true,
            filter: "\"HotfixRequestId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_ProjectId_ForwardPortOfTaskId",
            table: "Tasks",
            columns: new[] { "ProjectId", "ForwardPortOfTaskId" },
            unique: true,
            filter: "\"ForwardPortOfTaskId\" IS NOT NULL");

        migrationBuilder.AddForeignKey(
            name: "FK_Tasks_Releases_ProjectId_HotfixSourceReleaseId",
            table: "Tasks",
            columns: new[] { "ProjectId", "HotfixSourceReleaseId" },
            principalTable: "Releases",
            principalColumns: new[] { "ProjectId", "Id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Tasks_Tasks_ProjectId_ForwardPortOfTaskId",
            table: "Tasks",
            columns: new[] { "ProjectId", "ForwardPortOfTaskId" },
            principalTable: "Tasks",
            principalColumns: new[] { "ProjectId", "Id" },
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Tasks_Releases_ProjectId_HotfixSourceReleaseId", table: "Tasks");
        migrationBuilder.DropForeignKey(
            name: "FK_Tasks_Tasks_ProjectId_ForwardPortOfTaskId", table: "Tasks");
        migrationBuilder.DropIndex(name: "IX_Tasks_ProjectId_HotfixSourceReleaseId", table: "Tasks");
        migrationBuilder.DropIndex(name: "IX_Tasks_ProjectId_HotfixRequestId", table: "Tasks");
        migrationBuilder.DropIndex(name: "IX_Tasks_ProjectId_ForwardPortOfTaskId", table: "Tasks");
        migrationBuilder.DropUniqueConstraint(name: "AK_Tasks_ProjectId_Id", table: "Tasks");
        migrationBuilder.DropColumn(name: "HotfixSourceReleaseId", table: "Tasks");
        migrationBuilder.DropColumn(name: "HotfixDeployedSha", table: "Tasks");
        migrationBuilder.DropColumn(name: "HotfixTargetBranch", table: "Tasks");
        migrationBuilder.DropColumn(name: "HotfixRequestId", table: "Tasks");
        migrationBuilder.DropColumn(name: "ForwardPortOfTaskId", table: "Tasks");
    }
}
