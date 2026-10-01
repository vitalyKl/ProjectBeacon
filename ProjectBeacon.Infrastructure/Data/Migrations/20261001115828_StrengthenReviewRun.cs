using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class StrengthenReviewRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactRef",
                table: "ReviewRuns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "ReviewRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Findings",
                table: "ReviewRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewerType",
                table: "ReviewRuns",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "ReviewRuns",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ReviewRuns",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetRunId",
                table: "ReviewRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ReviewRuns"
                SET "ReviewerType" = 'Agent',
                    "Status" = 'Completed',
                    "ArtifactRef" = "TranscriptRef",
                    "StartedAt" = "CreatedAt",
                    "CompletedAt" = "CreatedAt"
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ReviewerType",
                table: "ReviewRuns",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "ReviewRuns",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "ReviewRuns",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArtifactRef",
                table: "ReviewRuns");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "ReviewRuns");

            migrationBuilder.DropColumn(
                name: "Findings",
                table: "ReviewRuns");

            migrationBuilder.DropColumn(
                name: "ReviewerType",
                table: "ReviewRuns");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "ReviewRuns");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ReviewRuns");

            migrationBuilder.DropColumn(
                name: "TargetRunId",
                table: "ReviewRuns");
        }
    }
}
