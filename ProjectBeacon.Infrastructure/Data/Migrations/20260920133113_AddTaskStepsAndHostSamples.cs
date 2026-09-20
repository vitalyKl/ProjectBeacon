using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskStepsAndHostSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceHostSamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: true),
                    RamUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    RamTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    GpuName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GpuUtilizationPercent = table.Column<double>(type: "double precision", nullable: true),
                    GpuMemoryUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    GpuMemoryTotalBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHostSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceHostSamples_DaemonDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "DaemonDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskSteps_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHostSamples_DeviceId_SampledAt",
                table: "DeviceHostSamples",
                columns: new[] { "DeviceId", "SampledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskSteps_TaskId_SortOrder",
                table: "TaskSteps",
                columns: new[] { "TaskId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceHostSamples");

            migrationBuilder.DropTable(
                name: "TaskSteps");
        }
    }
}
