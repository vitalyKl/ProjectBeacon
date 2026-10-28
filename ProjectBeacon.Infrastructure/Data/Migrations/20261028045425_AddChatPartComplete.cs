using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatPartComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Complete",
                table: "ChatParts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Complete",
                table: "ChatParts");
        }
    }
}
