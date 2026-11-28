using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations;

[DbContext(typeof(BeaconDbContext))]
[Migration("20261124152000_AddTaskVersionImpactReview")]
public sealed partial class AddTaskVersionImpactReview : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "VersionImpactReviewStatus",
            table: "Tasks",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "VersionImpactReviewNote",
            table: "Tasks",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "VersionImpactReviewedAt",
            table: "Tasks",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "VersionImpactReviewedBySessionId",
            table: "Tasks",
            type: "uuid",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "VersionImpactReviewStatus", table: "Tasks");
        migrationBuilder.DropColumn(name: "VersionImpactReviewNote", table: "Tasks");
        migrationBuilder.DropColumn(name: "VersionImpactReviewedAt", table: "Tasks");
        migrationBuilder.DropColumn(name: "VersionImpactReviewedBySessionId", table: "Tasks");
    }
}
