using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalModelBackends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BackendType = table.Column<string>(type: "text", nullable: false),
                    LaunchCommand = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContextSize = table.Column<int>(type: "integer", nullable: false),
                    Ttl = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtraFlagsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalModelBackends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    ModelBackendId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleBindings_LocalModelBackends_ModelBackendId",
                        column: x => x.ModelBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelBackends_ProjectId",
                table: "LocalModelBackends",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleBindings_ModelBackendId",
                table: "RoleBindings",
                column: "ModelBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleBindings_ProjectId_Role",
                table: "RoleBindings",
                columns: new[] { "ProjectId", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleBindings");

            migrationBuilder.DropTable(
                name: "LocalModelBackends");
        }
    }
}
