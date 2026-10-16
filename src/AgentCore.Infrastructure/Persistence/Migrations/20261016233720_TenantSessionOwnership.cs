using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSessionOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_TenantUsers_TenantId_PrincipalId",
                table: "TenantUsers",
                columns: new[] { "TenantId", "PrincipalId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SystemIdentities_TenantId_AgentInstanceId_PrincipalId",
                table: "SystemIdentities",
                columns: new[] { "TenantId", "AgentInstanceId", "PrincipalId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Sessions_SessionId_AgentInstanceId",
                table: "Sessions",
                columns: new[] { "SessionId", "AgentInstanceId" });

            migrationBuilder.CreateTable(
                name: "TenantSessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedByPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSessions", x => x.SessionId);
                    table.UniqueConstraint("AK_TenantSessions_TenantId_SessionId", x => new { x.TenantId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_TenantSessions_Sessions_SessionId_AgentInstanceId",
                        columns: x => new { x.SessionId, x.AgentInstanceId },
                        principalTable: "Sessions",
                        principalColumns: new[] { "SessionId", "AgentInstanceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantSessions_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId",
                        columns: x => new { x.TenantId, x.AgentInstanceId, x.ExecutorPrincipalId },
                        principalTable: "SystemIdentities",
                        principalColumns: new[] { "TenantId", "AgentInstanceId", "PrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantSessions_TenantUsers_TenantId_CreatedByPrincipalId",
                        columns: x => new { x.TenantId, x.CreatedByPrincipalId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSessions_SessionId_AgentInstanceId",
                table: "TenantSessions",
                columns: new[] { "SessionId", "AgentInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSessions_TenantId_AgentInstanceId_ExecutorPrincipalId",
                table: "TenantSessions",
                columns: new[] { "TenantId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSessions_TenantId_AgentInstanceId_SessionId",
                table: "TenantSessions",
                columns: new[] { "TenantId", "AgentInstanceId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSessions_TenantId_CreatedByPrincipalId",
                table: "TenantSessions",
                columns: new[] { "TenantId", "CreatedByPrincipalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantSessions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TenantUsers_TenantId_PrincipalId",
                table: "TenantUsers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SystemIdentities_TenantId_AgentInstanceId_PrincipalId",
                table: "SystemIdentities");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Sessions_SessionId_AgentInstanceId",
                table: "Sessions");
        }
    }
}
