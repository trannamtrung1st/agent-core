using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpenSandboxOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SandboxExecutions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentRunId = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SandboxProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxProfiles", x => new { x.Id, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "SandboxSessionLeases",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxSessionLeases", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_SessionId_AgentRunId_ToolCallId",
                table: "SandboxExecutions",
                columns: new[] { "SessionId", "AgentRunId", "ToolCallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SandboxSessionLeases_AgentInstanceId_ExpiresAt",
                table: "SandboxSessionLeases",
                columns: new[] { "AgentInstanceId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SandboxExecutions");

            migrationBuilder.DropTable(
                name: "SandboxProfiles");

            migrationBuilder.DropTable(
                name: "SandboxSessionLeases");
        }
    }
}
