using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandRecoveryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "WorkstationCommands",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClaimInstanceId",
                table: "WorkstationCommands",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "WorkstationCommands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveInstanceId",
                table: "DaemonDevices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "WorkstationCommands");

            migrationBuilder.DropColumn(
                name: "ClaimInstanceId",
                table: "WorkstationCommands");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "WorkstationCommands");

            migrationBuilder.DropColumn(
                name: "ActiveInstanceId",
                table: "DaemonDevices");
        }
    }
}
