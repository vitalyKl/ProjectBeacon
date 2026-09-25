using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaskPhaseId",
                table: "Subtasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "LocalModelBackends",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "TaskKinds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    DecisionBackendId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerBackendId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskKinds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskKinds_LocalModelBackends_DecisionBackendId",
                        column: x => x.DecisionBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskKinds_LocalModelBackends_WorkerBackendId",
                        column: x => x.WorkerBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaskPhases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Instruction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FanOut = table.Column<bool>(type: "boolean", nullable: false),
                    ModelBackendId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskPhases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskPhases_LocalModelBackends_ModelBackendId",
                        column: x => x.ModelBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaskKindPhases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskKindId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Instruction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FanOut = table.Column<bool>(type: "boolean", nullable: false),
                    ModelBackendId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskKindPhases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskKindPhases_LocalModelBackends_ModelBackendId",
                        column: x => x.ModelBackendId,
                        principalTable: "LocalModelBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskKindPhases_TaskKinds_TaskKindId",
                        column: x => x.TaskKindId,
                        principalTable: "TaskKinds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskKindPhases_ModelBackendId",
                table: "TaskKindPhases",
                column: "ModelBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskKindPhases_TaskKindId_SortOrder",
                table: "TaskKindPhases",
                columns: new[] { "TaskKindId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskKinds_DecisionBackendId",
                table: "TaskKinds",
                column: "DecisionBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskKinds_UserId",
                table: "TaskKinds",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskKinds_WorkerBackendId",
                table: "TaskKinds",
                column: "WorkerBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskPhases_ModelBackendId",
                table: "TaskPhases",
                column: "ModelBackendId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskPhases_TaskId_SortOrder",
                table: "TaskPhases",
                columns: new[] { "TaskId", "SortOrder" });

            migrationBuilder.Sql("""
                INSERT INTO "TaskKinds" ("Id", "UserId", "Name", "IsBuiltIn", "DecisionBackendId", "WorkerBackendId")
                SELECT "Id", "UserId", "Name", false,
                       COALESCE("ReviewBackendId", "ActorBackendId", "PlannerBackendId", "SoloBackendId"),
                       COALESCE("ActorBackendId", "SoloBackendId")
                FROM "AgentTemplates";

                INSERT INTO "TaskKindPhases" ("Id", "TaskKindId", "SortOrder", "Key", "Title", "Instruction", "FanOut", "ModelBackendId")
                SELECT gen_random_uuid(), "Id", 0, 'understand', 'Understand the task', '', false, COALESCE("PlannerBackendId", "SoloBackendId") FROM "AgentTemplates"
                UNION ALL
                SELECT gen_random_uuid(), "Id", 1, 'do', 'Do the work', '', false, COALESCE("ActorBackendId", "SoloBackendId") FROM "AgentTemplates"
                UNION ALL
                SELECT gen_random_uuid(), "Id", 2, 'check', 'Check the result', '', false, COALESCE("ReviewBackendId", "SoloBackendId") FROM "AgentTemplates";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskKindPhases");

            migrationBuilder.DropTable(
                name: "TaskPhases");

            migrationBuilder.DropTable(
                name: "TaskKinds");

            migrationBuilder.DropColumn(
                name: "TaskPhaseId",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "LocalModelBackends");
        }
    }
}
