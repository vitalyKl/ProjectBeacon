using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSessionUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UsageAssistantMessages",
                table: "ChatSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsageCompletionTokens",
                table: "ChatSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsageContextLimit",
                table: "ChatSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsagePromptTokens",
                table: "ChatSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "UsageTotalCost",
                table: "ChatSessions",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsageAssistantMessages",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "UsageCompletionTokens",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "UsageContextLimit",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "UsagePromptTokens",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "UsageTotalCost",
                table: "ChatSessions");
        }
    }
}
