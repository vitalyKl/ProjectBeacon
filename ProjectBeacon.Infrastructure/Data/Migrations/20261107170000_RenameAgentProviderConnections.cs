using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectBeacon.Infrastructure.Data;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    [DbContext(typeof(BeaconDbContext))]
    [Migration("20261107170000_RenameAgentProviderConnections")]
    public partial class RenameAgentProviderConnections : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "OpenCodeConnections",
                newName: "AgentProviderConnections");

            migrationBuilder.RenameIndex(
                name: "IX_OpenCodeConnections_UserId_ProviderId",
                table: "AgentProviderConnections",
                newName: "IX_AgentProviderConnections_UserId_ProviderId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_AgentProviderConnections_UserId_ProviderId",
                table: "AgentProviderConnections",
                newName: "IX_OpenCodeConnections_UserId_ProviderId");

            migrationBuilder.RenameTable(
                name: "AgentProviderConnections",
                newName: "OpenCodeConnections");
        }
    }
}