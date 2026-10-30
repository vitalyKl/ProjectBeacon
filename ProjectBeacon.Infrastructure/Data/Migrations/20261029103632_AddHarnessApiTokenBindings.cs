using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHarnessApiTokenBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientKind",
                table: "ApiTokens",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<Guid>(
                name: "CommandId",
                table: "ApiTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "ApiTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAt",
                table: "ApiTokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_CommandId",
                table: "ApiTokens",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_DeviceId",
                table: "ApiTokens",
                column: "DeviceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiTokens_DaemonDevices_DeviceId",
                table: "ApiTokens",
                column: "DeviceId",
                principalTable: "DaemonDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApiTokens_WorkstationCommands_CommandId",
                table: "ApiTokens",
                column: "CommandId",
                principalTable: "WorkstationCommands",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiTokens_DaemonDevices_DeviceId",
                table: "ApiTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_ApiTokens_WorkstationCommands_CommandId",
                table: "ApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_CommandId",
                table: "ApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_DeviceId",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "ClientKind",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "CommandId",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "ApiTokens");
        }
    }
}
