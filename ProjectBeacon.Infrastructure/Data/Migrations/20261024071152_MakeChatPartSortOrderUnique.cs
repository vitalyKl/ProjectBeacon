using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeChatPartSortOrderUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatParts_SessionId_SortOrder",
                table: "ChatParts");

            migrationBuilder.CreateIndex(
                name: "IX_ChatParts_SessionId_SortOrder",
                table: "ChatParts",
                columns: new[] { "SessionId", "SortOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatParts_SessionId_SortOrder",
                table: "ChatParts");

            migrationBuilder.CreateIndex(
                name: "IX_ChatParts_SessionId_SortOrder",
                table: "ChatParts",
                columns: new[] { "SessionId", "SortOrder" });
        }
    }
}
