using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatToolFieldsAndSessionArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "ChatSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "ChatParts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tool",
                table: "ChatParts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolInput",
                table: "ChatParts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolResult",
                table: "ChatParts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolStatus",
                table: "ChatParts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "ChatParts");

            migrationBuilder.DropColumn(
                name: "Tool",
                table: "ChatParts");

            migrationBuilder.DropColumn(
                name: "ToolInput",
                table: "ChatParts");

            migrationBuilder.DropColumn(
                name: "ToolResult",
                table: "ChatParts");

            migrationBuilder.DropColumn(
                name: "ToolStatus",
                table: "ChatParts");
        }
    }
}
