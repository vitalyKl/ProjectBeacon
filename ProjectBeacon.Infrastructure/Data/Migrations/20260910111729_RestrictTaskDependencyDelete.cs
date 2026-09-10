using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectBeacon.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestrictTaskDependencyDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskDependencies_Tasks_DependentTaskId",
                table: "TaskDependencies");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskDependencies_Tasks_DependentTaskId",
                table: "TaskDependencies",
                column: "DependentTaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskDependencies_Tasks_DependentTaskId",
                table: "TaskDependencies");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskDependencies_Tasks_DependentTaskId",
                table: "TaskDependencies",
                column: "DependentTaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
