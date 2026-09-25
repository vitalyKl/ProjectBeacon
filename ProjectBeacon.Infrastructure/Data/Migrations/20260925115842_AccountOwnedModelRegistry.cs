using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountOwnedModelRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "LocalModelBackends",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "LocalModelBackends" AS b
                SET "UserId" = COALESCE(
                    (SELECT m."UserId" FROM "ProjectMembers" AS m WHERE m."ProjectId" = b."ProjectId" AND m."Role" = 0 LIMIT 1),
                    (SELECT m."UserId" FROM "ProjectMembers" AS m WHERE m."ProjectId" = b."ProjectId" LIMIT 1));
                DELETE FROM "RoleBindings"
                WHERE "ModelBackendId" IN (SELECT "Id" FROM "LocalModelBackends" WHERE "UserId" IS NULL);
                DELETE FROM "LocalModelBackends" WHERE "UserId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "LocalModelBackends",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_LocalModelBackends_ProjectId",
                table: "LocalModelBackends");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "LocalModelBackends");

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelBackends_UserId",
                table: "LocalModelBackends",
                column: "UserId");

            migrationBuilder.CreateTable(
                name: "AgentTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    SoloBackendId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannerBackendId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorBackendId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewBackendId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTemplates_LocalModelBackends_ActorBackendId",
                        column: x => x.ActorBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentTemplates_LocalModelBackends_PlannerBackendId",
                        column: x => x.PlannerBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentTemplates_LocalModelBackends_ReviewBackendId",
                        column: x => x.ReviewBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentTemplates_LocalModelBackends_SoloBackendId",
                        column: x => x.SoloBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTemplates_ActorBackendId",
                table: "AgentTemplates",
                column: "ActorBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTemplates_PlannerBackendId",
                table: "AgentTemplates",
                column: "PlannerBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTemplates_ReviewBackendId",
                table: "AgentTemplates",
                column: "ReviewBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTemplates_SoloBackendId",
                table: "AgentTemplates",
                column: "SoloBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTemplates_UserId",
                table: "AgentTemplates",
                column: "UserId");

            migrationBuilder.Sql("""
                INSERT INTO "AgentTemplates" ("Id", "UserId", "Name", "Mode", "SoloBackendId", "PlannerBackendId", "ActorBackendId", "ReviewBackendId")
                SELECT gen_random_uuid(),
                       owner."UserId",
                       LEFT(p."Name", 200),
                       CASE
                           WHEN planner."ModelBackendId" IS NOT NULL
                            AND planner."ModelBackendId" = actor."ModelBackendId"
                            AND actor."ModelBackendId" = review."ModelBackendId"
                           THEN 'Solo' ELSE 'Pipeline'
                       END,
                       CASE
                           WHEN planner."ModelBackendId" IS NOT NULL
                            AND planner."ModelBackendId" = actor."ModelBackendId"
                            AND actor."ModelBackendId" = review."ModelBackendId"
                           THEN planner."ModelBackendId" ELSE NULL
                       END,
                       planner."ModelBackendId",
                       actor."ModelBackendId",
                       review."ModelBackendId"
                FROM "Projects" AS p
                JOIN LATERAL (
                    SELECT m."UserId" FROM "ProjectMembers" AS m
                    WHERE m."ProjectId" = p."Id" AND m."Role" = 0
                    LIMIT 1
                ) AS owner ON true
                LEFT JOIN "RoleBindings" AS planner ON planner."ProjectId" = p."Id" AND planner."Role" = 'Planner'
                LEFT JOIN "RoleBindings" AS actor ON actor."ProjectId" = p."Id" AND actor."Role" = 'Actor'
                LEFT JOIN "RoleBindings" AS review ON review."ProjectId" = p."Id" AND review."Role" = 'Review'
                WHERE planner."ModelBackendId" IS NOT NULL
                   OR actor."ModelBackendId" IS NOT NULL
                   OR review."ModelBackendId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTemplates");

            migrationBuilder.DropIndex(
                name: "IX_LocalModelBackends_UserId",
                table: "LocalModelBackends");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "LocalModelBackends");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "LocalModelBackends",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelBackends_ProjectId",
                table: "LocalModelBackends",
                column: "ProjectId");
        }
    }
}
