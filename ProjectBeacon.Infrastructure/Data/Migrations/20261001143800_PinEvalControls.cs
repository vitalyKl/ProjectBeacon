using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PinEvalControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckCommand",
                table: "EvalRuns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckExitCode",
                table: "EvalRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckOutput",
                table: "EvalRuns",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "EvalRuns",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "EvalRuns",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepoRevision",
                table: "EvalRuns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "EvalRuns",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "EvalRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolPermissions",
                table: "EvalRuns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckCommand",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "CheckExitCode",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "CheckOutput",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "RepoRevision",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "ToolPermissions",
                table: "EvalRuns");
        }
    }
}
