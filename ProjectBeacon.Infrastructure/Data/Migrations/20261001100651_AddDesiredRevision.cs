using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDesiredRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AppliedRevision",
                table: "DaemonDevices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DesiredRevision",
                table: "DaemonDevices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DesiredWorkstationJson",
                table: "DaemonDevices",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedRevision",
                table: "DaemonDevices");

            migrationBuilder.DropColumn(
                name: "DesiredRevision",
                table: "DaemonDevices");

            migrationBuilder.DropColumn(
                name: "DesiredWorkstationJson",
                table: "DaemonDevices");
        }
    }
}
